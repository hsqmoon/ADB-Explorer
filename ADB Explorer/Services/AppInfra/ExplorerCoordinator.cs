using ADB_Explorer.Controls;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;
using static ADB_Explorer.Helpers.VisibilityHelper;
using static ADB_Explorer.Models.AdbExplorerConst;

namespace ADB_Explorer.Services;

internal sealed class ExplorerCoordinator : IDisposable
{
    private readonly MainWindow view;
    private readonly IUiWorkScheduler uiScheduler;
    private readonly CancellationToken lifetimeToken;
    private CancellationTokenSource navigationCancellation;
    private int navigationRequestVersion;
    private string previousPath = "";
    private bool backForwardNavigation;
    private int disposed;

    public DirectorySession CurrentSession { get; private set; }

    public ExplorerCoordinator(
        MainWindow view,
        IUiWorkScheduler uiScheduler,
        CancellationToken lifetimeToken)
    {
        this.view = view;
        this.uiScheduler = uiScheduler;
        this.lifetimeToken = lifetimeToken;
    }

    public void InitializeDirectory()
    {
        Interlocked.Increment(ref navigationRequestVersion);
        if (CurrentSession is not null)
        {
            CurrentSession.PropertyChanged -= DirectorySession_PropertyChanged;
            CurrentSession.Stop();
        }
        App.FileActions.ListingInProgress = false;

        CurrentSession = new(uiScheduler, App.ActiveAdbDevice, FileHelper.ListerFileManipulator);
        CurrentSession.PropertyChanged += DirectorySession_PropertyChanged;
        CurrentSession.ApplyFilter(App.Settings.ShowHiddenItems, App.FileActions.ExplorerFilter);
        var session = CurrentSession;
        uiScheduler.EnqueueLatest(
            "directory.source",
            "directory.source",
            () =>
            {
                if (ReferenceEquals(session, CurrentSession))
                    (Application.Current as App)?.SetExplorerSource(session.VisibleList);
            });
    }

    public void NavigateDrive(DriveViewModel drive)
    {
        if (drive is not null)
            StartNavigation(drive.Path, true);
    }

    public void NavigatePath(string path)
    {
        if (path == "-")
        {
            backForwardNavigation = true;
            NavigateToLocation(NavHistory.GoBack());
        }
        else if (App.FileActions.IsExplorerVisible)
        {
            NavigateToLocation(new(path));
        }
        else
        {
            StartNavigation(path, true);
        }
    }

    public void Navigate(AdbLocation location)
    {
        if (location is null)
            return;

        view.NavigationBox.CloseSavedItemsFlyout();
        switch (location.Location)
        {
            case Navigation.SpecialLocation.Back:
                backForwardNavigation = true;
                NavigateToLocation(NavHistory.GoBack());
                break;
            case Navigation.SpecialLocation.Forward:
                backForwardNavigation = true;
                NavigateToLocation(NavHistory.GoForward());
                break;
            case Navigation.SpecialLocation.Up:
                backForwardNavigation = false;
                StartNavigation(App.ExplorerState.ParentPath, false);
                break;
            default:
                backForwardNavigation = false;
                if (App.FileActions.IsDriveViewVisible && location.Location is Navigation.SpecialLocation.DriveView)
                    _ = FileActionLogic.RefreshDrives();
                else
                    NavigateToLocation(location);
                break;
        }
    }

    public void NavigateFile(FileClass file)
    {
        if (file is null)
            return;

        backForwardNavigation = false;
        previousPath = file.FullPath;
        string realPath = !string.IsNullOrEmpty(file.LinkTarget)
            ? file.LinkTarget
            : file.FullPath;
        if (realPath is not null)
            StartRealPathNavigation(realPath);
    }

    public void RefreshLocation()
    {
        if (App.FileActions.IsDriveViewVisible)
            _ = FileActionLogic.RefreshDrives();
        else
            StartRealPathNavigation(App.ExplorerState.CurrentPath);
    }

    public void RefreshPackages()
    {
        if (App.FileActions.IsAppDrive)
            StartRealPathNavigation(App.ExplorerState.CurrentPath);
    }

    public void FollowLink(string target)
        => _ = ObserveNavigationAsync(FollowLinkAsync(target));

    public bool ConsumeBackForwardNavigation()
    {
        if (!backForwardNavigation)
            return false;

        backForwardNavigation = false;
        return true;
    }

    public void ShowDriveView()
    {
        CancelPendingNavigation();
        Interlocked.Increment(ref navigationRequestVersion);
        CurrentSession?.Stop();
        FileActionLogic.ClearExplorer(false);
        App.FileActions.IsDriveViewVisible = true;
        App.FileActions.IsExplorerVisible = false;
        view.UpdateFileOperationView();

        view.NavigationBox.ShowPath(AdbLocation.StringFromLocation(Navigation.SpecialLocation.DriveView));
        NavHistory.Navigate(Navigation.SpecialLocation.DriveView);
        App.Settings.LastDevicePath = "";
        App.Settings.SetLastDevicePath(App.ActiveDevices.Current?.ID, null);

        view.DriveList.ItemsSource = App.ActiveDevices.Current.Drives;
        App.ExplorerState.CurrentDrive = null;

        if (view.DriveList.SelectedIndex > -1)
            SelectionHelper.GetListViewItemContainer(view.DriveList).Focus();

        view.HomeSavedLocationsList.ItemsSource = view.NavigationBox.SavedItems;
        if (view.NavigationBox.SavedItems.Count == 0)
            App.Settings.HomeLocationsExpanded = false;
    }

    private void StartNavigation(string path, bool initialize)
        => _ = ObserveNavigationAsync(NavigateToPathAsync(path, initialize));

    private void StartRealPathNavigation(string path)
        => _ = ObserveNavigationAsync(NavigateRealPathAsync(path));

    private static async Task ObserveNavigationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "explorer.navigation");
        }
    }

    private async Task NavigateToPathAsync(string path, bool initialize)
    {
        if (path is null || lifetimeToken.IsCancellationRequested)
            return;

        var cancellation = BeginNavigationRequest(out int requestVersion);
        var device = App.ActiveAdbDevice;
        if (device is null)
            return;

        string requestedPath = string.IsNullOrEmpty(path) ? DEFAULT_PATH : path;
        string errorMessage = null;
        await uiScheduler.EnqueueAsync(
            "navigation.start",
            () =>
            {
                if (IsCurrentNavigation(requestVersion, device))
                    App.FileActions.ListingInProgress = true;
            },
            cancellation.Token).ConfigureAwait(false);

        string realPath;
        if (requestedPath == AdbLocation.StringFromLocation(Navigation.SpecialLocation.PackageDrive))
        {
            realPath = requestedPath;
        }
        else if (requestedPath == AdbLocation.StringFromLocation(Navigation.SpecialLocation.RecycleBin))
        {
            realPath = RECYCLE_PATH;
        }
        else
        {
            try
            {
                realPath = await device.TranslateDevicePathAsync(
                    requestedPath,
                    cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                realPath = null;
            }
        }

        if (!IsCurrentNavigation(requestVersion, device))
            return;

        if (realPath is null)
        {
            await uiScheduler.EnqueueAsync(
                "navigation.failed",
                () =>
                {
                    if (!IsCurrentNavigation(requestVersion, device))
                        return;

                    App.FileActions.ListingInProgress = false;
                    if (requestedPath != RECYCLE_PATH)
                    {
                        DialogService.ShowMessage(
                            errorMessage ?? Strings.Resources.S_LS_ERROR,
                            Strings.Resources.S_NAV_ERR_TITLE,
                            DialogService.DialogIcon.Critical,
                            copyToClipboard: true);
                    }

                    if (initialize)
                        ShowDriveView();
                },
                cancellation.Token).ConfigureAwait(false);

            return;
        }

        if (initialize)
        {
            await uiScheduler.EnqueueAsync(
                "navigation.initialize",
                () =>
                {
                    if (!IsCurrentNavigation(requestVersion, device))
                        return;

                    App.FileActions.IsDriveViewVisible = false;
                    App.FileActions.IsExplorerVisible = true;
                    App.FileActions.HomeEnabled = true;
                    view.UpdateFileOperationView();
                },
                cancellation.Token).ConfigureAwait(false);
            _ = MarkExplorerLoadedAsync();
        }

        if (!backForwardNavigation)
            previousPath = path;

        var session = await NavigateCoreAsync(
            realPath,
            requestVersion,
            device,
            cancellation.Token).ConfigureAwait(false);
        if (session?.InProgress is true)
            await session.Completion.WaitAsync(cancellation.Token).ConfigureAwait(false);
    }

    private async Task NavigateRealPathAsync(string realPath)
    {
        if (realPath is null || lifetimeToken.IsCancellationRequested)
            return;

        var cancellation = BeginNavigationRequest(out int requestVersion);
        var device = App.ActiveAdbDevice;
        if (device is null)
            return;

        var session = await NavigateCoreAsync(
            realPath,
            requestVersion,
            device,
            cancellation.Token).ConfigureAwait(false);
        if (session?.InProgress is true)
            await session.Completion.WaitAsync(cancellation.Token).ConfigureAwait(false);
    }

    private async Task FollowLinkAsync(string target)
    {
        if (string.IsNullOrEmpty(target))
            return;

        string parentPath = FileHelper.GetParentPath(target);
        if (parentPath != App.ExplorerState.CurrentPath)
            await NavigateToPathAsync(parentPath, false).ConfigureAwait(false);
        else if (CurrentSession is { InProgress: true } activeSession)
            await activeSession.Completion.WaitAsync(lifetimeToken).ConfigureAwait(false);

        var session = CurrentSession;
        await uiScheduler.EnqueueAsync(
            "explorer.follow-link",
            () =>
            {
                if (!ReferenceEquals(session, CurrentSession)
                    || App.ExplorerState.CurrentPath != parentPath)
                {
                    return;
                }

                var file = session?.FileList.FirstOrDefault(item => item.FullPath == target);
                if (file is not null)
                    view.SelectExplorerItem(file);
            },
            lifetimeToken).ConfigureAwait(false);
    }

    private async Task MarkExplorerLoadedAsync()
    {
        try
        {
            await Task.Delay(EXPLORER_NAV_DELAY, lifetimeToken).ConfigureAwait(false);
            uiScheduler.EnqueueLatest(
                "explorer.loaded",
                "explorer.loaded",
                () => App.RuntimeSettings.IsExplorerLoaded = true);
        }
        catch (OperationCanceledException)
        { }
    }

    private async Task<DirectorySession> NavigateCoreAsync(
        string realPath,
        int requestVersion,
        ADBService.AdbDevice device,
        CancellationToken cancellationToken)
    {
        bool isRecycleBin = realPath == RECYCLE_PATH;
        bool isAppDrive = realPath == AdbLocation.StringFromLocation(Navigation.SpecialLocation.PackageDrive);
        bool isTemp = realPath == TEMP_PATH;
        DirectorySession session = null;

        await uiScheduler.EnqueueAsync(
            "navigation.prepare",
            () =>
            {
                if (!IsCurrentNavigation(requestVersion, device))
                    return;

                view.PasteGrid.Visibility = Visibility.Collapsed;
                App.FileActions.ListingInProgress = true;
                App.FileActions.ExplorerFilter = "";
                NavHistory.Navigate(realPath);
            },
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrentNavigation(requestVersion, device))
            return null;

        bool selectionResetStarted = false;
        int selectionResetVersion = 0;
        try
        {
            await uiScheduler.EnqueueAsync(
                "navigation.selection.begin",
                () =>
                {
                    if (!IsCurrentNavigation(requestVersion, device))
                        return;

                    selectionResetVersion = view.BeginExplorerSelectionReset();
                    selectionResetStarted = true;
                },
                cancellationToken).ConfigureAwait(false);

            while (selectionResetStarted)
            {
                bool removed = false;
                await uiScheduler.EnqueueAsync(
                    "navigation.selection.remove",
                    () =>
                    {
                        if (IsCurrentNavigation(requestVersion, device))
                            removed = view.RemoveLastExplorerSelection(selectionResetVersion);
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!removed)
                    break;
            }

            await uiScheduler.EnqueueAsync(
                "navigation.selection.end",
                () =>
                {
                    if (selectionResetStarted)
                    {
                        view.EndExplorerSelectionReset(selectionResetVersion);
                        selectionResetStarted = false;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (selectionResetStarted)
            {
                uiScheduler.EnqueueLatest(
                    "navigation.selection.restore",
                    "navigation.selection.restore",
                    () => view.EndExplorerSelectionReset(selectionResetVersion));
            }
        }
        if (!IsCurrentNavigation(requestVersion, device))
            return null;

        await uiScheduler.EnqueueAsync(
            "navigation.path-state",
            () =>
            {
                if (!IsCurrentNavigation(requestVersion, device))
                    return;

                App.ExplorerState.CurrentPath = realPath;
                App.ExplorerState.ParentPath = FileHelper.GetParentPath(realPath);
                App.ExplorerState.CurrentDrive = DriveHelper.GetCurrentDrive(realPath);
            },
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrentNavigation(requestVersion, device))
            return null;

        await uiScheduler.EnqueueAsync(
            "navigation.saved-path",
            () =>
            {
                if (!IsCurrentNavigation(requestVersion, device)
                    || App.ActiveDevices.Current is null)
                {
                    return;
                }

                App.Settings.LastDevicePath = realPath;
                App.Settings.SetLastDevicePath(App.ActiveDevices.Current.ID, realPath);
            },
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrentNavigation(requestVersion, device))
            return null;

        await uiScheduler.EnqueueAsync(
            "navigation.path-box",
            () =>
            {
                if (!IsCurrentNavigation(requestVersion, device))
                    return;

                view.NavigationBox.ShowPath(isRecycleBin
                    ? AdbLocation.StringFromLocation(Navigation.SpecialLocation.RecycleBin)
                    : realPath);
            },
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrentNavigation(requestVersion, device))
            return null;

        await uiScheduler.EnqueueAsync(
            "navigation.mode",
            () =>
            {
                if (!IsCurrentNavigation(requestVersion, device))
                    return;

                App.FileActions.IsRecycleBin = isRecycleBin;
                App.FileActions.IsAppDrive = isAppDrive;
                App.FileActions.IsTemp = isTemp;
                App.FileActions.ParentEnabled = realPath != App.ExplorerState.ParentPath
                    && !isRecycleBin
                    && !isAppDrive;
                if (!App.RuntimeSettings.IsRootActive
                    && App.ActiveDevices.Current.Root is AbstractDevice.RootStatus.Enabled)
                {
                    App.RuntimeSettings.IsRootActive = true;
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrentNavigation(requestVersion, device))
            return null;

        await uiScheduler.EnqueueAsync(
            "navigation.actions",
            () =>
            {
                if (!IsCurrentNavigation(requestVersion, device))
                    return;

                FileActionLogic.IsPasteEnabled();
                App.FileActions.PushPackageEnabled = App.Settings.EnableApk
                    && App.ActiveDevices?.Current?.Type is not AbstractDevice.DeviceType.Recovery;
                App.FileActions.UninstallPackageEnabled = false;
                App.FileActions.ContextPushPackagesEnabled =
                App.FileActions.IsUninstallVisible.Value = isAppDrive;
                App.FileActions.PushFilesFoldersEnabled =
                App.FileActions.ContextNewEnabled =
                App.FileActions.ContextPushEnabled =
                App.FileActions.NewEnabled = !isRecycleBin && !isAppDrive;
            },
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrentNavigation(requestVersion, device))
            return null;

        await uiScheduler.EnqueueAsync(
            "navigation.columns",
            () =>
            {
                if (!IsCurrentNavigation(requestVersion, device))
                    return;

                view.OriginalPath.Visibility =
                view.OriginalDate.Visibility = Visible(isRecycleBin);
                view.PackageName.Visibility =
                view.PackageType.Visibility =
                view.PackageUid.Visibility =
                view.PackageVersion.Visibility = Visible(isAppDrive);
                view.IconColumn.Visibility =
                view.NameColumn.Visibility =
                view.DateColumn.Visibility =
                view.TypeColumn.Visibility =
                view.SizeColumn.Visibility = Visible(!isAppDrive);
            },
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrentNavigation(requestVersion, device))
            return null;

        await uiScheduler.EnqueueAsync(
            "navigation.descriptions",
            () =>
            {
                if (!IsCurrentNavigation(requestVersion, device))
                    return;

                App.FileActions.CopyPathDescription.Value = isAppDrive
                    ? Strings.Resources.S_COPY_APK_NAME
                    : Strings.Resources.S_COPY_PATH;
                App.FileActions.DeleteDescription.Value = isRecycleBin
                    ? Strings.Resources.S_EMPTY_TRASH
                    : Strings.Resources.S_DELETE_ACTION;
                if (isRecycleBin)
                    App.FileActions.RestoreDescription.Value = Strings.Resources.S_RESTORE_ALL;
            },
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrentNavigation(requestVersion, device))
            return null;

        await uiScheduler.EnqueueAsync(
            "navigation.session",
            () =>
            {
                if (!IsCurrentNavigation(requestVersion, device))
                    return;

                session = CurrentSession;
                if (isRecycleBin)
                {
                    _ = NavigateToRecyclePathAsync(realPath, requestVersion, session, device);
                }
                else if (isAppDrive)
                {
                    FileActionLogic.UpdatePackages(true);
                }
                else
                {
                    session?.Navigate(realPath);
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrentNavigation(requestVersion, device))
            return null;

        await uiScheduler.EnqueueAsync(
            "navigation.command-state",
            () =>
            {
                if (IsCurrentNavigation(requestVersion, device))
                    FileActionLogic.UpdateFileActions();
            },
            cancellationToken).ConfigureAwait(false);
        return IsCurrentNavigation(requestVersion, device) ? session : null;
    }

    private async Task NavigateToRecyclePathAsync(
        string path,
        int requestVersion,
        DirectorySession lister,
        ADBService.AdbDevice device)
    {
        if (lister is null || device is null)
            return;

        try
        {
            await TrashHelper.ParseIndexersAsync(device).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.AddCommandLog($"@ADB Explorer: failed to read recycle index: {ex.Message}");
        }

        await uiScheduler.EnqueueAsync(
            "explorer.recycle",
            () =>
            {
                if (requestVersion == Volatile.Read(ref navigationRequestVersion)
                    && ReferenceEquals(CurrentSession, lister)
                    && App.ActiveAdbDevice?.ID == device.ID)
                {
                    lister.Navigate(path);
                }
            },
            lifetimeToken).ConfigureAwait(false);
    }

    private void NavigateToLocation(AdbLocation location)
    {
        SelectionHelper.SetIsMenuOpen(view.ExplorerGrid.ContextMenu, false);

        if (location.Location is Navigation.SpecialLocation.DriveView)
        {
            App.FileActions.IsRecycleBin = false;
            App.RuntimeSettings.IsPathBoxFocused = false;
            _ = FileActionLogic.RefreshDrives();
            ShowDriveView();
            FileActionLogic.UpdateFileActions();
        }
        else if (!App.FileActions.IsExplorerVisible)
        {
            StartNavigation(location.DisplayName, true);
        }
        else
        {
            StartNavigation(location.DisplayName, false);
        }
    }

    private void DirectorySession_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (view.Dispatcher.HasShutdownStarted || sender is not DirectorySession lister)
            return;

        void handle()
        {
            if (!ReferenceEquals(lister, CurrentSession))
                return;

            switch (e.PropertyName)
            {
                case nameof(DirectorySession.VisibleList):
                    (Application.Current as App)?.SetExplorerSource(lister.VisibleList);
                    break;
                case nameof(DirectorySession.IsProgressVisible):
                    view.UnfinishedBlock.Visible(lister.IsProgressVisible);
                    view.NavigationBox.IsLoadingProgressVisible = lister.IsProgressVisible;
                    break;
                case nameof(DirectorySession.InProgress):
                    App.FileActions.ListingInProgress = lister.InProgress;
                    if (!lister.InProgress && App.FileActions.IsRecycleBin)
                        TrashHelper.EnableRecycleButtons();
                    break;
                case nameof(DirectorySession.Error) when lister.Error is not null:
                    DialogService.ShowMessage(
                        lister.Error.InnerException?.Message ?? lister.Error.Message,
                        Strings.Resources.S_LS_ERROR_TITLE,
                        DialogService.DialogIcon.Critical,
                        true,
                        copyToClipboard: true);
                    break;
                case nameof(DirectorySession.IsLinkListingFinished)
                    when view.ExplorerGrid.Items.Count < 1 || !lister.IsLinkListingFinished:
                    return;
                case nameof(DirectorySession.IsLinkListingFinished)
                    when backForwardNavigation
                        && !string.IsNullOrEmpty(previousPath)
                        && lister.FileList.FirstOrDefault(item => item.FullPath == previousPath) is var previousItem
                        && previousItem is not null:
                    view.SelectExplorerItem(previousItem);
                    break;
                case nameof(DirectorySession.IsLinkListingFinished):
                    view.ExplorerGrid.ScrollIntoView(view.ExplorerGrid.Items[0]);
                    break;
            }
        }

        string workName = e.PropertyName == nameof(DirectorySession.VisibleList)
            ? "directory.source"
            : $"directory-property.{e.PropertyName}";
        uiScheduler.EnqueueLatest(workName, workName, handle);
    }

    private CancellationTokenSource BeginNavigationRequest(out int requestVersion)
    {
        requestVersion = Interlocked.Increment(ref navigationRequestVersion);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        var previousCancellation = Interlocked.Exchange(ref navigationCancellation, cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        return cancellation;
    }

    private bool IsCurrentNavigation(int requestVersion, ADBService.AdbDevice device) =>
        requestVersion == Volatile.Read(ref navigationRequestVersion)
        && ReferenceEquals(App.ActiveAdbDevice, device);

    private void CancelPendingNavigation()
    {
        var cancellation = Interlocked.Exchange(ref navigationCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        CancelPendingNavigation();
        CloseDirectory();
    }

    public void CloseDirectory()
    {
        Interlocked.Increment(ref navigationRequestVersion);
        CancelPendingNavigation();
        var session = CurrentSession;
        CurrentSession = null;
        if (session is not null)
        {
            session.PropertyChanged -= DirectorySession_PropertyChanged;
            session.Stop();
        }
    }
}
