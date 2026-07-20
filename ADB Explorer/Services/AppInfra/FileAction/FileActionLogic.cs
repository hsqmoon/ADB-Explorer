using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using System.Windows.Threading;
using static ADB_Explorer.Models.AbstractFile;
using static ADB_Explorer.Services.FileAction;

namespace ADB_Explorer.Services.AppInfra;

internal static class FileActionLogic
{
    private static readonly object DriveRefreshLock = new();
    private static readonly HashSet<string> ActiveDriveRefreshes = [];
    private static readonly Dictionary<string, DateTime> LastDriveRefresh = [];
    private static readonly object PushedFilesLock = new();
    private static readonly Dictionary<
        (string DeviceId, string ParentPath, string FullName),
        (string FullPath, FileType Type, long? Size, DateTime? Modified)> PendingPushedFiles = [];
    private static bool PushedFilesUpdateScheduled;
    private static int packageRefreshVersion;

    private static string RemoveApkMessage(IEnumerable<IBrowserItem> objects)
    {
        var count = objects.Count();

        if (count == 1)
            return string.Format(Strings.Resources.S_REM_APK, objects.First().DisplayName);

        return string.Format(Strings.Resources.S_REM_APK_PLURAL, count);
    }

    public static async void UninstallPackages()
    {
        var pkgs = App.ExplorerState.SelectedPackages;
        var files = App.ExplorerState.SelectedFiles;

        var result = await DialogService.ShowConfirmation(
            RemoveApkMessage(App.FileActions.IsAppDrive ? pkgs : files),
            Strings.Resources.S_CONF_UNI_TITLE,
            Strings.Resources.S_UNINSTALL,
            icon: DialogService.DialogIcon.Exclamation);

        if (result.Item1 is not ContentDialogResult.Primary)
            return;

        var packageTask = await Task.Run(() =>
        {
            if (App.FileActions.IsAppDrive)
                return pkgs.Select(pkg => pkg.Name);

            return files.Select(item => ShellFileOperation.GetPackageName(App.ActiveAdbDevice, item.FullPath));
        });

        await ShellFileOperation.UninstallPackagesAsync(App.ActiveAdbDevice, packageTask, App.Current.Dispatcher);
    }

    public static async void InstallPackages()
    {
        var packages = App.ExplorerState.SelectedFiles;

        await ShellFileOperation.InstallPackagesAsync(App.ActiveAdbDevice, packages, App.Current.Dispatcher);
    }

    public static void CopyToTemp()
    {
        _ = App.CopyPaste.VerifyAndPasteAsync(
            DragDropEffects.Copy,
            AdbExplorerConst.TEMP_PATH,
            App.ExplorerState.SelectedFiles,
            App.Current.Dispatcher,
            App.ActiveAdbDevice,
            App.ExplorerState.CurrentPath);
    }

    public static async void PushPackages()
    {
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = false,
            Multiselect = true,
            DefaultDirectory = App.Settings.DefaultFolder,
            Title = Strings.Resources.S_INSTALL_APK,
        };
        dialog.Filters.Add(new(Strings.Resources.S_FILE_TYPE_APK, string.Join(';', AdbExplorerConst.INSTALL_APK.Select(name => name[1..]))));

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            return;

        await ShellFileOperation.PushPackagesAsync(App.ActiveAdbDevice, dialog.FileNames, App.Current.Dispatcher);
    }

    public static async void UpdateModifiedDates()
    {
        await ShellFileOperation.ChangeDateFromNameAsync(App.ActiveAdbDevice, App.ExplorerState.SelectedFiles, App.Current.Dispatcher);
    }

    public static async void OpenEditor()
    {
        if (!App.FileActions.EditFileEnabled || App.FileActions.IsEditorOpen && App.FileActions.EditorAndroidPath.Equals(App.ExplorerState.SelectedFiles.First()))
        {
            App.FileActions.IsEditorOpen = false;
            return;
        }
        App.FileActions.IsEditorOpen = true;

        App.FileActions.EditorAndroidPath = App.ExplorerState.SelectedFiles.First();
        
        string text;
        try
        {
            text = await Task.Run(() =>
                AdbHelper.ReadFile(App.ActiveAdbDevice, App.FileActions.EditorAndroidPath.FullPath));
        }
        catch (Exception e)
        {
            DialogService.ShowMessage(
                e.Message,
                Strings.Resources.S_READ_FILE_ERROR_TITLE,
                DialogService.DialogIcon.Exclamation,
                copyToClipboard: true);
            text = "";
        }

        App.FileActions.EditorText =
        App.FileActions.OriginalEditorText = text;
    }

    public static async void SaveEditorText()
    {
        try
        {
            await Task.Run(() =>
                AdbHelper.WriteFile(App.ActiveAdbDevice, App.FileActions.EditorAndroidPath.FullPath, App.FileActions.EditorText));
        }
        catch (Exception e)
        {
            DialogService.ShowMessage(
                e.Message,
                Strings.Resources.S_WRITE_FILE_ERROR_TITLE,
                DialogService.DialogIcon.Exclamation,
                copyToClipboard: true);
            return;
        }

        App.FileActions.OriginalEditorText = App.FileActions.EditorText;

        if (App.FileActions.EditorAndroidPath.ParentPath == App.ExplorerState.CurrentPath)
            (Application.Current as App)?.RequestUi(UiCommand.RefreshLocation);
    }

    public static async void RestoreItems()
    {
        var device = App.ActiveAdbDevice;
        var lister = App.ActiveDirectorySession;
        var currentPath = App.ExplorerState.CurrentPath;
        if (device is null || lister is null)
            return;

        var restoreItems = (!App.ExplorerState.SelectedFiles.Any() ? lister.FileList : App.ExplorerState.SelectedFiles)
            .Where(file => file.TrashIndex is not null
                && !string.IsNullOrEmpty(file.TrashIndex.OriginalPath))
            .ToList();
        if (restoreItems.Count == 0)
            return;

        HashSet<FileClass> conflictingItems;
        bool merge;
        try
        {
            (conflictingItems, merge) = await Task.Run(() =>
            {
                var existingPaths = (ADBService.FindFiles(
                        device.ID,
                        restoreItems.Select(file => file.TrashIndex.OriginalPath)) ?? [])
                    .ToHashSet(StringComparer.Ordinal);
                HashSet<FileClass> conflicts = restoreItems
                    .Where(item => existingPaths.Contains(item.TrashIndex.OriginalPath))
                    .ToHashSet();

                foreach (var group in restoreItems.GroupBy(
                    item => item.TrashIndex.OriginalPath,
                    StringComparer.Ordinal))
                {
                    conflicts.UnionWith(group.Skip(1));
                }

                return (conflicts, conflicts.Any(item => item.IsDirectory));
            });
        }
        catch (Exception e)
        {
            App.AddCommandLog($"@ADB Explorer: failed to check restore conflicts: {e.Message}");
            return;
        }

        if (conflictingItems.Count > 0)
        {
            var result = await DialogService.ShowConfirmation(
                conflictingItems.Count == 1
                    ? Strings.Resources.S_CONFLICT_ITEMS
                    : string.Format(Strings.Resources.S_CONFLICT_ITEMS_PLURAL, conflictingItems.Count),
                Strings.Resources.S_RESTORE_CONF_TITLE,
                primaryText: merge
                    ? Strings.Resources.S_MERGE_OR_REPLACE
                    : Strings.Resources.S_REPLACE,
                secondaryText: conflictingItems.Count == restoreItems.Count ? "" : Strings.Resources.S_SKIP,
                cancelText: Strings.Resources.S_CANCEL,
                icon: DialogService.DialogIcon.Exclamation);

            if (result.Item1 is ContentDialogResult.None)
                return;

            if (result.Item1 is ContentDialogResult.Secondary)
                restoreItems = restoreItems.Where(item => !conflictingItems.Contains(item)).ToList();
        }

        await ShellFileOperation.MoveItems(
            device: device,
            items: restoreItems,
            targetPath: null,
            currentPath: currentPath,
            fileList: lister.FileList,
            dispatcher: App.Current.Dispatcher);

        if (!ReferenceEquals(App.ActiveDirectorySession, lister))
            return;

        var remainingItems = lister.FileList.Except(restoreItems).ToList();
        TrashHelper.EnableRecycleButtons(remainingItems);

        // Clear all remaining files if none of them are indexed
        if (!remainingItems.Any(item => item.TrashIndex is not null))
            _ = Task.Run(() => ShellFileOperation.SilentDelete(device, remainingItems));

        if (!App.ExplorerState.SelectedFiles.Any())
            TrashHelper.EnableRecycleButtons();
    }

    public static void CopyItemPath()
    {
        var path = App.FileActions.IsAppDrive ? App.ExplorerState.SelectedPackages.First().Name : App.ExplorerState.SelectedFiles.First().FullPath;
        if (Application.Current is App app)
            _ = app.SetClipboardTextAsync(path);
    }

    public static void CreateNewItem(FileClass file, string newName = null)
    {
        if (!string.IsNullOrEmpty(newName))
            file.UpdatePath($"{App.ExplorerState.CurrentPath}{(App.ExplorerState.CurrentPath == "/" ? "" : "/")}{newName}");

        if (App.Settings.ShowExtensions)
            file.UpdateType();

        try
        {
            if (file.Type is FileType.Folder)
                _ = ShellFileOperation.MakeDir(App.ActiveAdbDevice, file.FullPath);
            else if (file.Type is FileType.File)
                ShellFileOperation.MakeFile(App.ActiveAdbDevice, file.FullPath);
            else
                throw new NotSupportedException();
        }
        catch (Exception e)
        {
            DialogService.ShowMessage(e.Message, Strings.Resources.S_CREATE_ERR_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true);
            App.ActiveDirectorySession.RemoveItem(file);
            throw;
        }

        file.IsTemp = false;
        file.ModifiedTime = DateTime.Now;
        if (file.Type is FileType.File)
            file.Size = 0;

        App.ActiveDirectorySession.RefreshItem(file);
        (Application.Current as App)?.SelectExplorerItem(file);
    }

    public static void IsPasteEnabled()
    {
        // Do not update if drag is active
        if (App.CopyPaste.IsDrag)
            return;

        // Explorer view AND source is clipboard
        if (App.FileActions.IsPasteStateVisible && App.CopyPaste.Files.Length > 0)
        {
            App.FileActions.CutItemsCount.Value = App.CopyPaste.Files.Length.ToString();
        }
        else
        {
            App.FileActions.CutItemsCount.Value = "";
            App.FileActions.IsCopyState.Value = false;
            App.FileActions.IsCutState.Value = false;

            App.FileActions.PasteEnabled = false;
            App.FileActions.IsKeyboardPasteEnabled = false;

            return;
        }

        string stringFormat;
        if (App.CopyPaste.Files.Length > 1)
        {
            if (App.FileActions.IsAppDrive)
            {
                stringFormat = Strings.Resources.S_DRAG_INSTALL_MULTIPLE;
            }
            else
            {
                stringFormat = App.CopyPaste.PasteState is DragDropEffects.Move
                    ? Strings.Resources.S_PASTE_PLURAL_CUT_ITEMS
                    : Strings.Resources.S_PASTE_PLURAL_COPIED_ITEMS;
            }

            App.FileActions.PasteDescription.Value = string.Format(stringFormat, App.CopyPaste.Files.Length);
        }
        else
        {
            if (App.FileActions.IsAppDrive)
            {
                stringFormat = string.Format(
                    Strings.Resources.S_DRAG_INSTALL_SINGLE,
                    Path.GetFileNameWithoutExtension(App.CopyPaste.Files.FirstOrDefault()));
            }
            else
            {
                stringFormat = App.CopyPaste.PasteState is DragDropEffects.Move
                    ? Strings.Resources.S_PASTE_ONE_CUT_ITEM
                    : Strings.Resources.S_PASTE_ONE_COPIED_ITEM;
            }

            App.FileActions.PasteDescription.Value = stringFormat;
        }

        App.FileActions.PasteEnabled = EnableUiPaste();
        App.FileActions.IsKeyboardPasteEnabled = EnableKeyboardPaste();
    }

    public static bool EnableUiPaste()
    {
        string[] files = App.CopyPaste.Files;
        if (App.CopyPaste.IsWindows
            && App.CopyPaste.IsVirtual
            && App.CopyPaste.Descriptors.Length == files.Length)
        {
            files = [.. App.CopyPaste.Descriptors.Select(d => d.Name)];
        }

        App.FileActions.IsPastingInDescendant = files.Length == 1
            && FileHelper.RelationFrom(files[0], App.ExplorerState.CurrentPath) is RelationType.Descendant or RelationType.Self;

        if (App.FileActions.IsPastingInDescendant)
            return false;

        var selected = App.ExplorerState.SelectedFiles?.Count();

        string targetPath;
        if (selected == 1)
        {
            var targetFile = App.ExplorerState.SelectedFiles.First();
            targetPath = targetFile.IsLink ? targetFile.LinkTarget : targetFile.FullPath;
        }
        else
        {
            targetPath = App.ExplorerState.CurrentPath;
        }

        PastingOnFuse(targetPath, files);

        if (App.FileActions.IsPastingIllegalOnFuse || App.FileActions.IsPastingConflictingOnFuse)
            return false;

        switch (selected)
        {
            case 0:
                App.FileActions.IsPastingInDescendant = App.CopyPaste.ParentFolder == App.ExplorerState.CurrentPath
                    && App.CopyPaste.PasteState is DragDropEffects.Move;

                break;
            case 1:
                var item = App.ExplorerState.SelectedFiles.First();
                if (!item.IsDirectory)
                    return false;

                App.FileActions.IsPastingInDescendant = (files.Length == 1 && files[0] == item.FullPath)
                    || (App.CopyPaste.ParentFolder == item.FullPath);

                break;
            default:
                return false;
        }

        return !App.FileActions.IsPastingInDescendant;
    }

    public static bool EnableKeyboardPaste()
    {
        string[] files = App.CopyPaste.Files;
        if (App.CopyPaste.IsWindows
            && App.CopyPaste.IsVirtual
            && App.CopyPaste.Descriptors.Length == files.Length)
        {
            files = [.. App.CopyPaste.Descriptors.Select(d => d.Name)];
        }

        App.FileActions.IsPastingInDescendant = files.Length == 1
            && FileHelper.RelationFrom(files[0], App.ExplorerState.CurrentPath) is RelationType.Descendant or RelationType.Self;

        if (App.FileActions.IsPastingInDescendant)
            return false;

        var selected = App.ExplorerState.SelectedFiles?.Count() > 1 ? 0 : App.ExplorerState.SelectedFiles?.Count();

        string targetPath;
        if (selected == 1)
        {
            var targetFile = App.ExplorerState.SelectedFiles.First();
            targetPath = targetFile.IsLink ? targetFile.LinkTarget : targetFile.FullPath;
        }
        else
        {
            targetPath = App.ExplorerState.CurrentPath;
        }

        PastingOnFuse(targetPath, files);

        if (App.FileActions.IsPastingIllegalOnFuse || App.FileActions.IsPastingConflictingOnFuse)
            return false;

        switch (selected)
        {
            case 0:
                App.FileActions.IsPastingInDescendant = App.CopyPaste.ParentFolder == App.ExplorerState.CurrentPath
                    && App.CopyPaste.PasteState is DragDropEffects.Move;

                break;
            case 1:
                // When duplicating a file multiple times using the keyboard, the selection is the previous copy
                if (App.CopyPaste.PasteState is DragDropEffects.Copy && App.ActiveDirectorySession.FileList.Any(f => f.FullPath == files[0]))
                    return true;

                var item = App.ExplorerState.SelectedFiles.First();
                if (!item.IsDirectory)
                    return false;

                App.FileActions.IsPastingInDescendant = (files.Length == 1 && files[0] == item.FullPath)
                    || (App.CopyPaste.ParentFolder == item.FullPath);

                break;
            default:
                return false;
        }

        return !App.FileActions.IsPastingInDescendant;
    }

    public static DragDropEffects EnableDropPaste(FileClass target = null)
    {
        if (App.CopyPaste.DragFiles.Length == 0)
            return DragDropEffects.None;

        var pastingInDescendant = App.CopyPaste.DragFiles.Length == 1
            && FileHelper.RelationFrom(App.CopyPaste.DragFiles[0], App.ExplorerState.CurrentPath) is RelationType.Descendant or RelationType.Self;

        if (pastingInDescendant || App.FileActions.IsRecycleBin)
            return DragDropEffects.None;

        if (FileHelper.RelationFrom(App.CopyPaste.DragParent, AdbExplorerConst.RECYCLE_PATH) is RelationType.Self or RelationType.Ancestor)
            return DragDropEffects.Move;

        string targetPath = target switch
        {
            null => App.ExplorerState.CurrentPath,
            _ when target.IsLink => target.LinkTarget,
            _ => target.FullPath,
        };

        PastingOnFuse(targetPath, App.CopyPaste.DragFiles);

        var result = DragDropEffects.Copy;
        if (App.RuntimeSettings.IsRootActive
            && App.CopyPaste.IsSelf
            && DriveHelper.GetCurrentDrive(targetPath)?.IsFUSE is false
            && App.CopyPaste.DragFiles.Length == 1)
            result |= DragDropEffects.Link;

        if (App.FileActions.IsPastingIllegalOnFuse || App.FileActions.IsPastingConflictingOnFuse)
            return DragDropEffects.None;

        if (target is null)
        {
            if (App.CopyPaste.DragParent == App.ExplorerState.CurrentPath)
                return result;
        }
        else
        {
            if (!target.IsDirectory)
                return DragDropEffects.None;

            pastingInDescendant = (App.CopyPaste.DragFiles.Length == 1 && App.CopyPaste.DragFiles[0] == target.FullPath)
                || (App.CopyPaste.DragParent == target.FullPath);
        }

        return pastingInDescendant
            ? DragDropEffects.None
            : result | DragDropEffects.Move;
    }

    private static void PastingOnFuse(string targetPath, string[] files)
    {
        if (App.FileActions.IsAppDrive)
        {
            App.FileActions.IsPastingIllegalOnFuse = App.CopyPaste.IsSelf && DriveHelper.GetCurrentDrive(files[0])?.IsFUSE is true;
            return;
        }
        bool isFuse = DriveHelper.GetCurrentDrive(targetPath)?.IsFUSE is true;

        App.FileActions.IsPastingIllegalOnFuse = isFuse
            && !FileHelper.FileNameLegal(files.Select(FileHelper.GetFullName), FileHelper.RenameTarget.FUSE);

        App.FileActions.IsPastingConflictingOnFuse = isFuse
            && files.Distinct(StringComparer.InvariantCultureIgnoreCase)
            .Count() != files.Length;
    }

    public static void PasteFiles(IEnumerable<FileClass> selectedFiles, bool isLink = false)
    {
        App.CopyPaste.AcceptClipboard(selectedFiles, isLink);

        IsPasteEnabled();
    }

    public static void CutItems(bool isCopy = false)
    {
        if (App.FileActions.IsAppDrive)
            CopyPackages(App.ExplorerState.SelectedPackages);
        else
            CutFiles(App.ExplorerState.SelectedFiles, isCopy);
    }

    public static void CopyPackages(IEnumerable<Package> items)
    {
        App.FileActions.CopyEnabled = false;
        App.FileActions.CutEnabled = true;

        IsPasteEnabled();

        var vfdo = VirtualFileDataObject.PrepareTransfer(items, VirtualFileDataObject.DataObjectMethod.Clipboard);
        vfdo?.SendObjectToShell(VirtualFileDataObject.DataObjectMethod.Clipboard, allowedEffects: DragDropEffects.Copy);
    }

    public static void CutFiles(IEnumerable<FileClass> items, bool isCopy = false)
    {
        var itemsToCut = App.ActiveDevices.Current.Root is not AbstractDevice.RootStatus.Enabled
                    ? items.Where(file => file.Type is FileType.File or FileType.Folder) : items;

        App.FileActions.CopyEnabled = !isCopy;
        App.FileActions.CutEnabled = isCopy;

        IsPasteEnabled();

        var dropEffect = isCopy ? DragDropEffects.Copy : DragDropEffects.Move;
        var vfdo = VirtualFileDataObject.PrepareTransfer(itemsToCut, dropEffect, VirtualFileDataObject.DataObjectMethod.Clipboard);
        vfdo?.SendObjectToShell(VirtualFileDataObject.DataObjectMethod.Clipboard, allowedEffects: dropEffect);
    }

    public static void Rename(TextBox textBox)
    {
        if (textBox.DataContext is not FileClass file)
            return;
        
        var name = FileHelper.DisplayName(textBox);

        if (!App.FileActions.IsRenameUnixLegal
            || (App.ExplorerState.CurrentDrive?.IsFUSE is true && !App.FileActions.IsRenameFuseLegal)
            || !App.FileActions.IsRenameUnique)
        {
            return;
        }

        if (file.IsTemp)
        {
            if (string.IsNullOrEmpty(textBox.Text))
            {
                App.ActiveDirectorySession.RemoveItem(file);
                return;
            }
            
            try
            {
                CreateNewItem(file, textBox.Text);
            }
            catch (Exception e)
            {
                if (e is NotImplementedException)
                    throw;
            }
        }
        else if (!string.IsNullOrEmpty(textBox.Text) && textBox.Text != name)
        {
            try
            {
                string text = textBox.Text;
                if (text.Count(c => c == TextHelper.RTL_MARK) == 1)
                    text = text.Replace($"{TextHelper.RTL_MARK}", "");

                FileHelper.RenameFile(file, text);
            }
            catch (Exception)
            { }
        }
    }

    public static async void DeleteFiles()
    {
        List<FileClass> itemsToDelete;
        if (App.FileActions.IsRecycleBin && !App.ExplorerState.SelectedFiles.Any())
        {
            itemsToDelete = [.. App.ActiveDirectorySession.FileList.Where(f => f.Extension != AdbExplorerConst.RECYCLE_INDEX_SUFFIX)];
        }
        else
        {
            itemsToDelete = [.. App.ActiveDevices.Current.Root != AbstractDevice.RootStatus.Enabled
                ? App.ExplorerState.SelectedFiles.Where(file => file.Type is FileType.File or FileType.Folder)
                : App.ExplorerState.SelectedFiles];
        }

        string deletedString;
        if (itemsToDelete.Count == 1)
            deletedString = FileHelper.DisplayName(itemsToDelete.First());
        else
        {
            deletedString = $"{itemsToDelete.Count} ";
            if (itemsToDelete.All(item => item.IsDirectory))
                deletedString += Strings.Resources.S_MENU_FOLDERS;
            else if (itemsToDelete.All(item => !item.IsDirectory))
                deletedString += Strings.Resources.S_MENU_FILES;
            else
                deletedString += Strings.Resources.S_BROWSER_ITEMS_PLURAL;
        }

        var result = await DialogService.ShowConfirmation(
            string.Format(App.FileActions.IsRecycleBin
                ? Strings.Resources.S_DELETE_PERMANENT
                : Strings.Resources.S_DELETE_CONFIRMATION, deletedString),
            Strings.Resources.S_DEL_CONF_TITLE,
            Strings.Resources.S_DELETE_ACTION,
            checkBoxText: App.Settings.EnableRecycle && !App.FileActions.IsRecycleBin ? Strings.Resources.S_PERM_DEL : "",
            icon: DialogService.DialogIcon.Delete);

        if (result.Item1 is not ContentDialogResult.Primary)
            return;

        if (!App.FileActions.IsRecycleBin && App.Settings.EnableRecycle && !result.Item2)
        {
            await ShellFileOperation.MakeDir(App.ActiveAdbDevice, AdbExplorerConst.RECYCLE_PATH);

            await ShellFileOperation.MoveItems(App.ActiveAdbDevice,
                                         itemsToDelete,
                                         AdbExplorerConst.RECYCLE_PATH,
                                         App.ExplorerState.CurrentPath,
                                         App.ActiveDirectorySession.FileList,
                                         App.Current.Dispatcher);
        }
        else
        {
            await ShellFileOperation.DeleteItemsAsync(App.ActiveAdbDevice, itemsToDelete, App.Current.Dispatcher);

            if (App.FileActions.IsRecycleBin)
            {
                var remainingItems = App.ActiveDirectorySession.FileList.Except(itemsToDelete).ToList();
                TrashHelper.EnableRecycleButtons(remainingItems);

                // Clear all remaining files if none of them are indexed
                if (!remainingItems.Any(item => item.TrashIndex is not null))
                {
                    _ = Task.Run(() => ShellFileOperation.SilentDelete(App.ActiveAdbDevice, remainingItems));
                }
            }
        }
    }

    public static async Task RefreshDrives(
        bool updateCounts = true,
        CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled)
            cancellationToken = DeviceHelper.GetCurrentDeviceWorkToken();

        var currentDevice = App.ActiveDevices.Current;
        var currentAdbDevice = App.ActiveAdbDevice;

        if (currentDevice is null || currentAdbDevice is null || currentDevice.Status is not AbstractDevice.DeviceStatus.Ok || currentAdbDevice.Status is not AbstractDevice.DeviceStatus.Ok)
            return;

        string deviceId = currentDevice.ID;
        bool refreshTopology;
        lock (DriveRefreshLock)
        {
            if (ActiveDriveRefreshes.Contains(deviceId))
                return;

            refreshTopology = !LastDriveRefresh.TryGetValue(deviceId, out DateTime lastRefresh)
                || currentDevice.Drives?.Count is not > 0
                || DateTime.Now - lastRefresh >= AdbExplorerConst.DRIVE_UPDATE_INTERVAL;

            if (refreshTopology)
                ActiveDriveRefreshes.Add(deviceId);
        }

        if (refreshTopology)
        {
            try
            {
                var drives = currentAdbDevice.Status is AbstractDevice.DeviceStatus.Ok
                    ? await currentAdbDevice.GetDrivesAsync(cancellationToken).ConfigureAwait(false)
                    : null;

                if (drives is null
                    || !ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
                    || !ReferenceEquals(App.ActiveDevices.Current, currentDevice)
                    || Application.Current is not App app)
                {
                    return;
                }

                await app.EnqueueUiAsync(
                    "drives.update",
                    () =>
                    {
                        if (ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
                            && ReferenceEquals(App.ActiveDevices.Current, currentDevice))
                        {
                            currentDevice.UpdateDrives(drives);
                        }
                    },
                    cancellationToken);

                if (!ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
                    || !ReferenceEquals(App.ActiveDevices.Current, currentDevice))
                {
                    return;
                }

                await app.EnqueueUiAsync(
                    "drives.display",
                    () =>
                    {
                        if (!ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
                            || !ReferenceEquals(App.ActiveDevices.Current, currentDevice))
                        {
                            return;
                        }

                        (Application.Current as App)?.RequestUi(UiCommand.FilterDrives);
                        FolderHelper.CombineDisplayNames();
                    },
                    cancellationToken);

                lock (DriveRefreshLock)
                {
                    LastDriveRefresh[deviceId] = DateTime.Now;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                App.AddCommandLog($"@ADB Explorer: failed to refresh drives: {e.Message}");
            }
            finally
            {
                lock (DriveRefreshLock)
                {
                    ActiveDriveRefreshes.Remove(deviceId);
                }
            }
        }

        if (!updateCounts
            || !ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
            || !ReferenceEquals(App.ActiveDevices.Current, currentDevice))
        {
            return;
        }

        bool isRecovery = currentDevice.Type is AbstractDevice.DeviceType.Recovery;
        bool hasTrashDrive = currentDevice.Drives?.Any(d => d.Type is AbstractDrive.DriveType.Trash) == true;
        bool hasTempDrive = currentDevice.Drives?.Any(d => d.Type is AbstractDrive.DriveType.Temp) == true;
        bool hasPackageDrive = currentDevice.Drives?.Any(d => d.Type is AbstractDrive.DriveType.Package) == true;

        if (isRecovery)
        {
            if (Application.Current is not App app)
                return;

            await app.EnqueueUiAsync("drives.recovery-counts", () =>
            {
                if (!ReferenceEquals(App.ActiveDevices.Current, currentDevice))
                    return;

                foreach (var item in currentDevice.Drives?.OfType<VirtualDriveViewModel>() ?? [])
                {
                    item.SetItemsCount(item.Type is AbstractDrive.DriveType.Package ? -1 : null);
                }
            });
            return;
        }

        if (App.Settings.EnableRecycle && hasTrashDrive)
            await TrashHelper.UpdateRecycledItemsCount();

        if (!ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
            || !ReferenceEquals(App.ActiveDevices.Current, currentDevice))
        {
            return;
        }

        if (App.Settings.EnableApk && hasTempDrive)
            await UpdateInstallersCount();

        if (!ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
            || !ReferenceEquals(App.ActiveDevices.Current, currentDevice))
        {
            return;
        }

        if (App.Settings.EnableApk && hasPackageDrive)
            await UpdatePackagesCount();
    }

    public static async Task UpdateInstallersCount()
    {
        var currentDevice = App.ActiveDevices.Current;
        var currentAdbDevice = App.ActiveAdbDevice;
        if (currentDevice is null || currentAdbDevice is null)
            return;

        ulong count;
        try
        {
            count = await currentAdbDevice.CountFilesAsync(
                AdbExplorerConst.TEMP_PATH,
                includeNames: AdbExplorerConst.INSTALL_APK.Select(name => "*" + name),
                excludeNames: null,
                cancellationToken: DeviceHelper.GetCurrentDeviceWorkToken());
        }
        catch
        {
            return;
        }

        if (Application.Current is not App app)
            return;

        await app.EnqueueUiAsync("drives.installer-count", () =>
        {
            if (!ReferenceEquals(App.ActiveDevices.Current, currentDevice)
                || !ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice))
                return;

            var temp = App.ActiveDevices.Current.Drives.Find(d => d.Type is AbstractDrive.DriveType.Temp);
            ((VirtualDriveViewModel)temp)?.SetItemsCount(
                count <= (ulong)long.MaxValue ? (long)count : null);
        });
    }

    public static async Task UpdatePackagesCount()
    {
        var currentDevice = App.ActiveDevices.Current;
        var currentAdbDevice = App.ActiveAdbDevice;
        if (currentDevice is null || currentAdbDevice is null)
            return;

        ulong? count;
        try
        {
            count = await currentAdbDevice.GetPackagesCountAsync(
                DeviceHelper.GetCurrentDeviceWorkToken());
        }
        catch
        {
            return;
        }

        if (count is null
            || !ReferenceEquals(App.ActiveDevices.Current, currentDevice)
            || !ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
            || Application.Current is not App app)
        {
            return;
        }

        await app.EnqueueUiAsync("drives.package-count", () =>
        {
            if (!ReferenceEquals(App.ActiveDevices.Current, currentDevice)
                || !ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice))
                return;

            var package = App.ActiveDevices.Current.Drives.Find(d => d.Type is AbstractDrive.DriveType.Package);
            ((VirtualDriveViewModel)package)?.SetItemsCount(
                count.Value <= (ulong)long.MaxValue ? (long)count.Value : null);
        });
    }

    public static async void UpdatePackages(bool updateExplorer = false)
    {
        int requestVersion = Interlocked.Increment(ref packageRefreshVersion);
        var currentDevice = App.ActiveDevices.Current;
        var currentAdbDevice = App.ActiveAdbDevice;
        if (currentDevice is null
            || currentAdbDevice is null
            || Application.Current is not App app)
        {
            if (updateExplorer)
                App.FileActions.ListingInProgress = false;
            return;
        }

        if (updateExplorer)
            App.FileActions.ListingInProgress = true;

        var version = currentDevice.AndroidVersion;
        bool showSystemPackages = App.Settings.ShowSystemPackages;
        ObservableList<Package> packages;
        try
        {
            packages = new(await currentAdbDevice.GetPackagesAsync(
                showSystemPackages,
                version is not null && version >= AdbExplorerConst.MIN_PKG_UID_ANDROID_VER,
                DeviceHelper.GetCurrentDeviceWorkToken()).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                App.AddCommandLog($"@ADB Explorer: failed to list packages: {ex.GetBaseException().Message}");

            try
            {
                await app.EnqueueUiAsync("packages.failed", () =>
                {
                    if (updateExplorer
                        && requestVersion == Volatile.Read(ref packageRefreshVersion)
                        && ReferenceEquals(App.ActiveDevices.Current, currentDevice)
                        && ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice))
                    {
                        App.FileActions.ListingInProgress = false;
                    }
                });
            }
            catch (Exception enqueueException) when (enqueueException is OperationCanceledException or ObjectDisposedException)
            { }
            return;
        }

        try
        {
            await app.EnqueueUiAsync("packages.publish", () =>
            {
                if (requestVersion != Volatile.Read(ref packageRefreshVersion)
                    || !ReferenceEquals(App.ActiveDevices.Current, currentDevice)
                    || !ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice))
                {
                    return;
                }

                App.ExplorerState.Packages = packages;
                if (updateExplorer)
                {
                    app.SetExplorerSource(App.ExplorerState.VisiblePackages);
                    App.FileActions.ListingInProgress = false;
                }
                else
                {
                    var package = currentDevice.Drives.Find(d => d.Type is AbstractDrive.DriveType.Package);
                    ((VirtualDriveViewModel)package)?.SetItemsCount(App.ExplorerState.Packages.Count);
                }
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public static void ClearExplorer(bool clearDevice = true)
    {
        Interlocked.Increment(ref packageRefreshVersion);
        App.FileActions.ListingInProgress = false;
        App.ActiveDirectorySession?.Stop();
        App.ExplorerState.Packages = [];
        App.ExplorerState.SelectedFiles = [];
        App.ExplorerState.SelectedPackages = [];

        App.FileActions.PushFilesFoldersEnabled =
        App.FileActions.PullEnabled =
        App.FileActions.DeleteEnabled =
        App.FileActions.RenameEnabled =
        App.FileActions.HomeEnabled =
        App.FileActions.NewEnabled =
        App.FileActions.PasteEnabled =
        App.FileActions.IsUninstallVisible.Value =
        App.FileActions.CutEnabled =
        App.FileActions.CopyEnabled =
        App.FileActions.IsExplorerVisible =
        App.FileActions.PackageActionsEnabled =
        App.FileActions.IsCopyItemPathEnabled =
        App.FileActions.UpdateModifiedEnabled =
        App.FileActions.IsFollowLinkEnabled =
        App.RuntimeSettings.IsExplorerLoaded =
        App.FileActions.ParentEnabled = false;

        App.FileActions.ExplorerFilter = "";

        if (clearDevice)
        {
            App.ExplorerState.CurrentDisplayNames.Clear();
            App.ExplorerState.CurrentPath = null;
            App.RuntimeSettings.CurrentDevice = null;
            (Application.Current as App)?.RequestUi(UiCommand.ClearNavigation);

            UpdateFileActions();
        }

        (Application.Current as App)?.RequestUi(UiCommand.FilterActions);
    }

    public static void UpdateFileActions()
    {
        App.FileActions.IsApkActionsVisible.Value = App.Settings.EnableApk && App.ActiveDevices?.Current;
        App.FileActions.PushPackageEnabled = App.FileActions.IsApkActionsVisible && App.ActiveDevices.Current.Type is not AbstractDevice.DeviceType.Recovery;

        App.FileActions.UninstallPackageEnabled = App.FileActions.IsAppDrive && App.ExplorerState.SelectedPackages.Any();
        App.FileActions.ContextPushPackagesEnabled = App.FileActions.IsAppDrive && !App.ExplorerState.SelectedPackages.Any();

        App.FileActions.IsRefreshEnabled = App.FileActions.IsDriveViewVisible || App.FileActions.IsExplorerVisible;
        App.FileActions.IsCopyCurrentPathEnabled = App.FileActions.IsExplorerVisible && !App.FileActions.IsRecycleBin && !App.FileActions.IsAppDrive;

        App.FileActions.IsOpenApkLocationEnabled = App.FileActions.IsAppDrive && App.ExplorerState.SelectedPackages.Count() == 1;
        App.FileActions.IsApkWebSearchEnabled = App.FileActions.IsOpenApkLocationEnabled && !string.IsNullOrEmpty(App.RuntimeSettings.DefaultBrowserPath);

        App.FileActions.IsRegularItem = !App.ExplorerState.SelectedFiles.Any() || App.RuntimeSettings.IsRootActive
            || App.ExplorerState.SelectedFiles.AnyAll(item => item.Type is FileType.File or FileType.Folder);

        App.FileActions.IsFollowLinkEnabled = !App.FileActions.IsRecycleBin
                                               && App.ExplorerState.SelectedFiles.Count() == 1
                                               && App.ExplorerState.SelectedFiles.First().IsLink
                                               && App.ExplorerState.SelectedFiles.First().Type is not FileType.BrokenLink;

        if (App.FileActions.IsRecycleBin)
        {
            TrashHelper.EnableRecycleButtons(App.ExplorerState.SelectedFiles.Any() ? App.ExplorerState.SelectedFiles : App.ActiveDirectorySession.FileList);
        }
        else
        {
            App.FileActions.DeleteEnabled = App.ExplorerState.SelectedFiles.Any() && App.FileActions.IsRegularItem
                && (!App.FileActions.IsFollowLinkEnabled || App.RuntimeSettings.IsRootActive);

            App.FileActions.RestoreEnabled = false;
        }

        App.FileActions.PullDescription.Value = App.FileActions.IsFollowLinkEnabled ? Strings.Resources.S_PULL_ACTION_LINK : Strings.Resources.S_PULL_ACTION;
        App.FileActions.DeleteDescription.Value = App.FileActions.IsRecycleBin && !App.ExplorerState.SelectedFiles.Any() ? Strings.Resources.S_EMPTY_TRASH : Strings.Resources.S_DELETE_ACTION;
        App.FileActions.RestoreDescription.Value = App.FileActions.IsRecycleBin && !App.ExplorerState.SelectedFiles.Any() ? Strings.Resources.S_RESTORE_ALL : Strings.Resources.S_RESTORE_ACTION;

        App.FileActions.IsSelectionIllegalOnWindows = App.ExplorerState.SelectedFiles.Any() && !FileHelper.FileNameLegal(App.ExplorerState.SelectedFiles, FileHelper.RenameTarget.Windows);
        App.FileActions.IsSelectionIllegalOnFuse = App.ExplorerState.SelectedFiles.Any() && !FileHelper.FileNameLegal(App.ExplorerState.SelectedFiles, FileHelper.RenameTarget.FUSE);
        App.FileActions.IsSelectionIllegalOnWinRoot = App.ExplorerState.SelectedFiles.Any() && !FileHelper.FileNameLegal(App.ExplorerState.SelectedFiles, FileHelper.RenameTarget.WinRoot);
        App.FileActions.IsSelectionConflictingOnFuse = App.ExplorerState.SelectedFiles.Select(f => f.FullName)
            .Distinct(StringComparer.InvariantCultureIgnoreCase)
            .Count() != App.ExplorerState.SelectedFiles.Count();

        App.FileActions.PullEnabled = !App.FileActions.IsRecycleBin
                                       && App.ExplorerState.SelectedFiles.AnyAll(f => f.Type is not FileType.BrokenLink)
                                       && App.FileActions.IsRegularItem
                                       && !App.FileActions.IsSelectionIllegalOnWindows
                                       && !App.FileActions.IsSelectionConflictingOnFuse;

        App.FileActions.ContextPushEnabled = !App.FileActions.IsRecycleBin && !App.FileActions.IsAppDrive && (!App.ExplorerState.SelectedFiles.Any() || (App.ExplorerState.SelectedFiles.Count() == 1 && App.ExplorerState.SelectedFiles.First().IsDirectory));

        App.FileActions.RenameEnabled = !App.FileActions.IsRecycleBin
                                         && App.ExplorerState.SelectedFiles.Count() == 1
                                         && App.FileActions.IsRegularItem
                                         && (!App.FileActions.IsFollowLinkEnabled || App.RuntimeSettings.IsRootActive);

        var allSelectedAreCut = false;
        if (App.CopyPaste.IsSelf && App.CopyPaste.Files.Length == App.ExplorerState.SelectedFiles.Count())
        {
            var selectedPaths = App.ExplorerState.SelectedFiles.Select(file => file.FullPath).ToHashSet();
            allSelectedAreCut = App.CopyPaste.Files.All(selectedPaths.Contains);
        }
        
        App.FileActions.CutEnabled = App.ExplorerState.SelectedFiles.AnyAll(f => f.Type is not FileType.BrokenLink)
                                      && !(allSelectedAreCut && App.CopyPaste.PasteState is DragDropEffects.Move)
                                      && App.FileActions.IsRegularItem
                                      && (!App.FileActions.IsFollowLinkEnabled || App.RuntimeSettings.IsRootActive);

        if (App.FileActions.IsAppDrive)
        {
            App.FileActions.CopyEnabled = App.ExplorerState.SelectedPackages.Any();
        }
        else
        {
            App.FileActions.CopyEnabled = App.ExplorerState.SelectedFiles.AnyAll(f => f.Type is not FileType.BrokenLink)
                                           && !(allSelectedAreCut && App.CopyPaste.PasteState is DragDropEffects.Copy)
                                           && App.FileActions.IsRegularItem
                                           && !App.FileActions.IsRecycleBin;
        }
        
        IsPasteEnabled();

        // APK enabled in settings
        // All selected files are installable
        // Not in trash
        // If recovery, only enabled outside temp drive (to enable copy to temp, but install is disabled, even in temp drive)
        App.FileActions.PackageActionsEnabled = App.Settings.EnableApk
                                                 && App.ExplorerState.SelectedFiles.AnyAll(file => file.IsInstallApk)
                                                 && !App.FileActions.IsRecycleBin
                                                 && !(App.ActiveDevices?.Current?.Type is AbstractDevice.DeviceType.Recovery
                                                 && App.FileActions.IsTemp);

        App.FileActions.IsCopyItemPathEnabled = App.FileActions.IsAppDrive
            ? App.ExplorerState.SelectedPackages.Count() == 1
            : App.ExplorerState.SelectedFiles.Count() == 1 && !App.FileActions.IsRecycleBin;

        App.FileActions.ContextNewEnabled = !App.ExplorerState.SelectedFiles.Any() && !App.FileActions.IsRecycleBin && !App.FileActions.IsAppDrive;

        App.FileActions.SubmenuUninstallEnabled = App.ExplorerState.CurrentDrive?.IsFUSE is not true
            && App.ExplorerState.SelectedFiles.AnyAll(file => file.IsInstallApk)
            && App.ActiveDevices?.Current?.Type is not AbstractDevice.DeviceType.Recovery;

        App.FileActions.UpdateModifiedEnabled = !App.FileActions.IsRecycleBin
            && App.ExplorerState.SelectedFiles.AnyAll(file => file.Type is FileType.File && !file.IsApk && !file.IsLink);

        App.FileActions.EditFileEnabled = !App.FileActions.IsRecycleBin
            && App.ExplorerState.SelectedFiles.Count() == 1
            && App.ExplorerState.SelectedFiles.First().Type is FileType.File
            && !App.ExplorerState.SelectedFiles.First().IsApk
            && !App.ExplorerState.SelectedFiles.First().IsLink
            && App.ExplorerState.SelectedFiles.First().Size < App.Settings.EditorMaxFileSize;

        App.FileActions.IsPasteLinkEnabled = App.ExplorerState.CurrentDrive?.IsFUSE is not true
            && App.RuntimeSettings.IsRootActive
            && App.CopyPaste.Files.Length == 1
            && App.CopyPaste.IsSelf
            && App.CopyPaste.PasteState is DragDropEffects.Copy
            && (!App.ExplorerState.SelectedFiles.Any() ||
            (App.ExplorerState.SelectedFiles.Count() == 1 && App.ExplorerState.SelectedFiles.First().IsDirectory));

        App.FileActions.InstallPackageEnabled = App.ActiveDevices?.Current?.Type is not AbstractDevice.DeviceType.Recovery
            && App.ExplorerState.CurrentDrive?.IsFUSE is not true;

        if (!App.CopyPaste.IsDrag)
            (Application.Current as App)?.RequestUi(UiCommand.FilterActions);

        AppActions.RaiseCanExecuteChanged();
    }

    public static void ScheduleUpdateFileActions()
    {
        if (Application.Current is not App app)
            return;

        app.EnqueueUiLatest("file-actions.update", "file-actions.update", UpdateFileActions);
    }

    public static void PushItems(bool isFolderPicker, bool isContextMenu)
    {
        App.RuntimeSettings.IsPathBoxFocused = false;

        string targetPath, targetName = "";
        string title = "";
        if (isContextMenu && App.ExplorerState.SelectedFiles.Count() == 1)
        {
            targetPath = App.ExplorerState.SelectedFiles.First().FullPath;
            targetName = App.ExplorerState.SelectedFiles.First().FullName;

            title = isFolderPicker
                ? Strings.Resources.S_SELECT_FOLDER_PUSH_DESTINATION
                : Strings.Resources.S_SELECT_FILE_PUSH_DESTINATION;
        }
        else
        {
            targetPath = App.ExplorerState.CurrentPath;

            title = isFolderPicker
                ? Strings.Resources.S_SELECT_FOLDER_PUSH
                : Strings.Resources.S_SELECT_FILE_PUSH;
        }
        
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = isFolderPicker,
            Multiselect = true,
            DefaultDirectory = App.Settings.DefaultFolder,
            Title = title,
        };

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            return;

        _ = CopyPasteService.VerifyAndPush(targetPath, dialog.FileNames, App.ActiveAdbDevice);
    }

    public static async Task<IReadOnlyList<FileSyncOperation>> PushShellObjects(
        IEnumerable<string> itemPaths,
        string targetPath,
        ADBService.AdbDevice device,
        DragDropEffects dropEffects = DragDropEffects.Copy)
    {
        var paths = itemPaths.ToList();
        List<(string Path, string Message)> failures = [];
        var syncItems = await Task.Run(() =>
        {
            List<SyncFile> sources = [];
            foreach (var path in paths)
            {
                SyncFile source = null;

                try
                {
                    source = SyncFile.FromWindowsPath(path);

                    sources.Add(source);
                    source = null;
                }
                catch (Exception e)
                {
                    source?.ClearAll();
                    failures.Add((path, e.Message));
                }
            }

            List<(SyncFile source, SyncFile target, bool isBatch)> items = [];
            foreach (var group in sources.GroupBy(source => source.ParentPath, StringComparer.OrdinalIgnoreCase))
            {
                var groupItems = group.ToList();
                if (groupItems.Count == 1)
                {
                    var source = groupItems[0];
                    var target = new SyncFile(FileHelper.ConcatPaths(targetPath, source.FullName),
                        source.IsDirectory ? FileType.Folder : FileType.File)
                        { Size = source.Size };
                    items.Add((source, target, false));
                    continue;
                }

                SyncFile batchSource = new(group.Key, FileType.Folder)
                {
                    PathType = FilePathType.Windows,
                };
                batchSource.Children.AddRange(groupItems);
                items.Add((batchSource, new SyncFile(targetPath, FileType.Folder), true));
            }

            return items;
        });

        if (failures.Count > 0)
        {
            App.AddCommandLog($"@Windows: failed to prepare {failures.Count} item(s) for upload. "
                + $"{failures[0].Path}: {failures[0].Message}");
        }

        if (syncItems.Count == 0)
            return [];

        List<FileSyncOperation> queuedOperations = [];
        void addPushOperations()
        {
            queuedOperations = syncItems.Select(item =>
            {
                var pushOperation = FileSyncOperation.PushFile(item.source, item.target, device, App.Current.Dispatcher);
                pushOperation.DropEffects = dropEffects;
                pushOperation.IsBatch = item.isBatch;
                pushOperation.PropertyChanged += PushOperation_PropertyChanged;
                return pushOperation;
            }).ToList();

        }

        try
        {
            if (App.Current.Dispatcher.CheckAccess())
                addPushOperations();
            else if (Application.Current is App app)
                await app.EnqueueUiAsync("transfer.queue-push", addPushOperations);

            await App.ActiveFileOperations.AddOperationsAsync(queuedOperations).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            syncItems.ForEach(item => item.source.ClearAll());
            App.AddCommandLog($"@ADB Explorer: failed to queue upload: {e.Message}");
            return [];
        }

        return queuedOperations;
    }

    private static void PushOperation_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        var op = sender as FileSyncOperation;

        if (e.PropertyName != nameof(FileOperation.Status)
            || op.Status is FileOperation.OperationStatus.Waiting
            or FileOperation.OperationStatus.InProgress)
            return;

        try
        {
            // If operation was cancelled or had failed - don't delete the source.
            if (op.Status is not FileOperation.OperationStatus.Completed)
                return;

            var hasSkippedFiles = op.StatusInfo is CompletedSyncProgressViewModel { FilesSkipped: > 0 };
            if (hasSkippedFiles)
            {
                if (op.Device.ID == App.ActiveAdbDevice?.ID
                    && (op.IsBatch ? op.TargetPath.FullPath : op.TargetPath.ParentPath) == App.ExplorerState.CurrentPath)
                {
                    (Application.Current as App)?.RequestUi(UiCommand.RefreshLocation);
                }
            }
            else
            {
                SchedulePushedFileUpdate(op);
            }

            // In push we can delete the source once the operation has completed
            if (op.DropEffects is DragDropEffects.Move && !hasSkippedFiles)
            {
                var sources = op.PushedItems
                    .Select(item => (item.SourcePath, item.SourceIsDirectory))
                    .ToArray();

                _ = Task.Run(async () =>
                {
                    await op.Completion.ConfigureAwait(false);

                    foreach (var (sourcePath, isDirectory) in sources)
                    {
                        try
                        {
                            if (isDirectory)
                                Directory.Delete(sourcePath, true);
                            else
                                File.Delete(sourcePath);
                        }
                        catch
                        { }
                    }
                });
            }
        }
        finally
        {
            op.PropertyChanged -= PushOperation_PropertyChanged;
        }
    }

    private static void SchedulePushedFileUpdate(FileSyncOperation op)
    {
        IEnumerable<(
            (string DeviceId, string ParentPath, string FullName) Key,
            (string FullPath, FileType Type, long? Size, DateTime? Modified) Value)> updates =
            op.PushedItems.Select(item => (
                Key: (op.Device.ID, item.ParentPath, item.FullName),
                Value: (item.FullPath, item.Type, item.Size, item.Modified)));

        lock (PushedFilesLock)
        {
            foreach (var update in updates)
                PendingPushedFiles[update.Key] = update.Value;

            if (PushedFilesUpdateScheduled)
                return;

            PushedFilesUpdateScheduled = true;
        }

        _ = FlushPushedFileUpdatesAsync();
    }

    private static async Task FlushPushedFileUpdatesAsync()
    {
        try
        {
            await Task.Delay(100).ConfigureAwait(false);

            KeyValuePair<
                (string DeviceId, string ParentPath, string FullName),
                (string FullPath, FileType Type, long? Size, DateTime? Modified)>[] updates;

            lock (PushedFilesLock)
            {
                updates = [.. PendingPushedFiles];
                PendingPushedFiles.Clear();
                PushedFilesUpdateScheduled = false;
            }

            var preparedFiles = updates.Select(update => (
                update.Key,
                File: new FileClass(
                    update.Key.FullName,
                    update.Value.FullPath,
                    update.Value.Type,
                    size: update.Value.Size,
                    modifiedTime: update.Value.Modified,
                    loadIcon: false))).ToArray();

            if (Application.Current is not App app)
                return;

            DirectorySession targetSession = null;
            Dictionary<string, FileClass> existingFiles = null;
            await app.EnqueueUiAsync("directory.pushed-files.prepare", () =>
            {
                if (App.ActiveAdbDevice is null || App.ActiveDirectorySession is null)
                    return;

                targetSession = App.ActiveDirectorySession;
                existingFiles = [];
                foreach (var file in targetSession.FileList)
                    existingFiles.TryAdd(file.FullName, file);
            });

            if (targetSession is null)
                return;

            List<FileClass> completedFiles = [];
            foreach (var update in preparedFiles)
            {
                await app.EnqueueUiAsync("directory.pushed-files.add", () =>
                {
                    if (!ReferenceEquals(App.ActiveDirectorySession, targetSession)
                        || App.ActiveAdbDevice is null)
                    {
                        return;
                    }

                    if (update.Key.DeviceId == App.ActiveAdbDevice.ID
                        && update.Key.ParentPath == App.ExplorerState.CurrentPath)
                    {
                        if (!existingFiles.TryGetValue(update.Key.FullName, out var file))
                        {
                            file = update.File;
                            existingFiles.Add(update.Key.FullName, file);
                            targetSession.AddItem(file);
                        }

                        completedFiles.Add(file);
                    }
                });
            }

            app.SelectExplorerItems(completedFiles, targetSession);
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "directory.pushed-files");
        }
    }

    // Pull where we know the actual target path
    public static void PullFiles(string targetPath = "")
    {
        App.RuntimeSettings.IsPathBoxFocused = false;

        var pullItems = App.ExplorerState.SelectedFiles;
        string path;

        if (!string.IsNullOrEmpty(targetPath))
        {
            path = targetPath;
        }
        else
        {
            var dialog = new CommonOpenFileDialog()
            {
                IsFolderPicker = true,
                Multiselect = false,
                DefaultDirectory = App.Settings.DefaultFolder,
                Title = pullItems.Count() > 1
                    ? Strings.Resources.S_ITEM_DESTINATION_PLURAL
                    : string.Format(Strings.Resources.S_ITEM_DESTINATION, pullItems.First()),
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                return;
            
            path = dialog.FileName;
        }

        _ = PullFiles(path, pullItems, true);
    }

    private static async Task PullFiles(string targetPath, IEnumerable<FileClass> pullItems, bool notify = false)
    {
        try
        {
            var pullItemList = pullItems?.ToList();
            if (pullItemList is null || pullItemList.Count == 0)
                return;

            var device = App.ActiveAdbDevice;
            if (device is null)
                return;

            var dispatcher = App.Current.Dispatcher;
            var match = AdbRegEx.RE_WINDOWS_DRIVE_ROOT().Match(targetPath);
            var invalidFiles = pullItemList
                .Where(f => AdbExplorerConst.INVALID_WINDOWS_ROOT_PATHS.Contains(f.FullName))
                .ToList();

            if (match.Success && invalidFiles.Count > 0)
            {
                var result = await DialogService.ShowConfirmation(string.Format(Strings.Resources.S_WIN_ROOT_ILLEGAL, invalidFiles.Count),
                                                     Strings.Resources.S_WIN_ROOT_ILLEGAL_TITLE,
                                                     primaryText: Strings.Resources.S_SKIP,
                                                     icon: DialogService.DialogIcon.Exclamation);

                if (result.Item1 is not ContentDialogResult.Primary)
                    return;

                pullItemList = pullItemList.Except(invalidFiles).ToList();
            }

            string directoryError = await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(targetPath);
                    return null;
                }
                catch (Exception e)
                {
                    return e.Message;
                }
            });

            if (directoryError is not null)
            {
                DialogService.ShowMessage(directoryError, Strings.Resources.S_DEST_ERR,
                    DialogService.DialogIcon.Critical, copyToClipboard: true);
                return;
            }

            var files = await CopyPasteService.MergeFiles(
                pullItemList.Select(f => f.FullPath), targetPath, device);
            var fileSet = files.ToHashSet();
            if (fileSet.Count < pullItemList.Count)
                pullItemList = pullItemList.Where(f => fileSet.Contains(f.FullPath)).ToList();

            var operations = await Task.Run(() => GeneratePullOps(
                targetPath, pullItemList, device, dispatcher, notify));
            await App.ActiveFileOperations.AddOperationsAsync(operations).ConfigureAwait(false);

            static List<FileSyncOperation> GeneratePullOps(
                string targetPath,
                IEnumerable<FileClass> pullItems,
                ADBService.AdbDevice device,
                Dispatcher dispatcher,
                bool notify)
            {
                List<FileSyncOperation> operations = [];
                var itemList = pullItems.ToList();
                var treesBySource = FileHelper.GetFolderTrees(
                    itemList.Where(file => file.IsDirectory).Select(file => file.FullPath),
                    device);

                foreach (var group in itemList.GroupBy(file => file.ParentPath, StringComparer.Ordinal))
                {
                    var sources = group.Select(file => new SyncFile(
                        file,
                        file.IsDirectory && treesBySource.TryGetValue(file.FullPath, out var tree)
                            ? tree.Skip(1)
                            : null)).ToList();
                    FileSyncOperation fileOp;
                    if (sources.Count == 1)
                    {
                        var source = sources[0];
                        fileOp = FileSyncOperation.PullFile(
                            source,
                            SyncFile.MergeToWindowsPath(source, targetPath),
                            device,
                            dispatcher);
                    }
                    else
                    {
                        SyncFile batchSource = new(group.Key, FileType.Folder);
                        batchSource.Children.AddRange(sources);
                        SyncFile batchTarget = new(targetPath, FileType.Folder)
                        {
                            PathType = FilePathType.Windows,
                        };
                        fileOp = FileSyncOperation.PullFile(batchSource, batchTarget, device, dispatcher);
                        fileOp.IsBatch = true;
                    }

                    if (notify)
                        fileOp.PropertyChanged += PullOperation_PropertyChanged;

                    operations.Add(fileOp);
                }

                return operations;
            }
        }
        catch (Exception e)
        {
            App.AddCommandLog($"@ADB Explorer: failed to prepare download: {e.Message}");
        }
    }

    private static readonly object explorerRefreshLock = new();
    private static readonly HashSet<string> pendingExplorerRefreshes = [];
    private static bool explorerRefreshScheduled;

    private static void PullOperation_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileOperation.Status))
            return;

        var op = sender as FileSyncOperation;
        if (op.Status is FileOperation.OperationStatus.Waiting or FileOperation.OperationStatus.InProgress)
            return;

        op.PropertyChanged -= PullOperation_PropertyChanged;
        if (op.Status is not FileOperation.OperationStatus.Completed)
            return;

        lock (explorerRefreshLock)
        {
            pendingExplorerRefreshes.Add(op.IsBatch
                ? op.TargetPath.FullPath
                : op.TargetPath.ParentPath);
            if (explorerRefreshScheduled)
                return;

            explorerRefreshScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);

            string[] directories;
            lock (explorerRefreshLock)
            {
                directories = [.. pendingExplorerRefreshes];
                pendingExplorerRefreshes.Clear();
                explorerRefreshScheduled = false;
            }

            directories.ForEach(NativeMethods.RefreshExplorerDirectory);
        });
    }

    public static void ToggleFileOpQ()
    {
        App.ActiveFileOperations.IsAutoPlayOn ^= true;

        if (App.ActiveFileOperations.IsAutoPlayOn)
            App.ActiveFileOperations.Start();
        else
            App.ActiveFileOperations.Stop();

        AppActions.RaiseCanExecuteChanged(
            FileActionType.FileOpStop,
            FileActionType.FileOpFilter);
    }

    public static void UpdateFileOpControls()
    {
        if (Application.Current is not App app)
            return;

        app.EnqueueUiLatest("file-operation.controls", "file-operation.controls", () =>
        {
            var plural = App.FileActions.SelectedFileOps.Value.Count() != 1;
            var opString = plural
                ? Strings.Resources.S_ACTION_OPERATION_PLURAL
                : Strings.Resources.S_ACTION_OPERATION;

            var removeAction = string.Format(Strings.Resources.S_REM_DEVICE_TITLE, opString);
            if (App.FileActions.RemoveFileOpDescription.Value != removeAction)
                App.FileActions.RemoveFileOpDescription.Value = removeAction;

            var validateAction = string.Format(Strings.Resources.S_ACTION_VALIDATE, opString);
            if (App.FileActions.ValidateDescription.Value != validateAction)
                App.FileActions.ValidateDescription.Value = validateAction;

            AppActions.RaiseCanExecuteChanged(
                FileActionType.FileOpRemove,
                FileActionType.FileOpValidate);
        });
    }

    public static async void ResetAppSettings()
    {
        var result = await DialogService.ShowConfirmation(
                        Strings.Resources.S_RESET_SETTINGS,
                        Strings.Resources.S_RESET_SETTINGS_TITLE,
                        primaryText: Strings.Resources.S_CONFIRM,
                        cancelText: Strings.Resources.S_CANCEL,
                        icon: DialogService.DialogIcon.Exclamation);

        if (result.Item1 == ContentDialogResult.None)
            return;

        App.RuntimeSettings.ResetAppSettings = true;
    }

    public static void ToggleSettingsSort()
    {
        App.RuntimeSettings.SortedView ^= true;
        App.FileActions.IsExpandSettingsVisible.Value ^= true;
    }

    public static void ToggleSettingsExpand()
    {
        App.RuntimeSettings.GroupsExpanded ^= true;
    }

    public static void FollowLink()
    {
        var target = App.ExplorerState.SelectedFiles.First().LinkTarget;

        if (string.IsNullOrEmpty(target))
            return;

        (Application.Current as App)?.FollowLink(target);
    }

    public static void OpenApkLocation(Package apk = null)
    {
        apk ??= App.ExplorerState.SelectedPackages.First();

        (Application.Current as App)?.RequestNavigation(new(FileHelper.GetParentPath(apk.Path)));
    }

    public static void ApkWebSearch()
    {
        var apk = App.ExplorerState.SelectedPackages.First();
        
        Process.Start(App.RuntimeSettings.DefaultBrowserPath, $"\"? {apk.Name}\"");
    }

    public static void RemoveFileOps()
    {
        var ops = App.FileActions.SelectedFileOps.Value;
        if (!ops.Any())
            ops = App.ActiveFileOperations.Operations;

        _ = App.ActiveFileOperations.RemoveOperationsAsync(ops);
    }
}
