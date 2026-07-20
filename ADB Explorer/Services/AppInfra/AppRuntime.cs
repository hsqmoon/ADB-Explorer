using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

internal enum UiCommand
{
    NewFolder,
    NewFile,
    Rename,
    SelectAll,
    RefreshLocation,
    FilterDrives,
    FilterDevices,
    FilterActions,
    ClearNavigation,
    InitializeDirectory,
    ShowDriveView,
    AutoHideSearch,
    ClearLogs,
    SortFileOperations,
    RefreshBreadcrumbs,
}

internal sealed class AppRuntime : IDisposable
{
    private readonly MainWindow mainWindow;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly AdbDeviceMonitorService deviceMonitor = new();
    private readonly Lazy<ShellStaService> shellSta = new(() => new(), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<ShellIconService> shellIcons;
    private readonly Lazy<ClipboardSnapshotService> clipboardSnapshots;
    private readonly Lazy<DropSnapshotService> dropSnapshots;
    private readonly Lazy<ShellMaterializationService> shellMaterialization;
    private readonly DeviceMetadataService deviceMetadata;
    private readonly ExplorerCoordinator explorer;
    private readonly object deviceMonitorLock = new();
    private readonly object historyDevicesLock = new();
    private readonly object metadataSnapshotLock = new();
    private CancellationTokenSource deviceMonitorLifetime;
    private Task deviceMonitorTask = Task.CompletedTask;
    private int started;
    private int disposed;
    private int activated;
    private int uiReady;
    private Task runTask = Task.CompletedTask;
    private Task historyDevicesTask = Task.CompletedTask;
    private Task metadataSnapshotTask = Task.CompletedTask;
    private (DeviceDelta Delta, LogicalDeviceViewModel[] Devices, bool AutoRoot, bool PollBattery)? pendingMetadataSnapshot;
    private bool metadataSnapshotWorkerActive;
    private long explorerSourceVersion;
    private int historyDevicesLoaded;
    private int pairingRefreshActive;

    public IUiWorkScheduler UiScheduler { get; }
    public ShellIconService ShellIcons => shellIcons.Value;
    public DirectorySession CurrentDirectorySession => explorer.CurrentSession;
    public FileOperationQueue FileOperations { get; }
    public Devices Devices { get; }
    public ADBService.AdbDevice CurrentAdbDevice { get; set; }
    public ExplorerState ExplorerState { get; } = new();

    public AppRuntime(MainWindow mainWindow)
    {
        this.mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        lifetimeToken = lifetime.Token;
        UiScheduler = new UiWorkScheduler(mainWindow.Dispatcher);
        Devices = new();
        FileOperations = new(
            UiScheduler,
            visible => App.FileActions.IsFileOpRingVisible = visible,
            () => App.Settings.AllowMultiOp,
            () => App.Settings.RescanOnPush);
        deviceMetadata = new(UiScheduler, lifetimeToken);
        explorer = new(mainWindow, UiScheduler, lifetimeToken);
        shellIcons = new(() => new(shellSta.Value), LazyThreadSafetyMode.ExecutionAndPublication);
        clipboardSnapshots = new(() => new(shellSta.Value), LazyThreadSafetyMode.ExecutionAndPublication);
        dropSnapshots = new(() => new(shellSta.Value), LazyThreadSafetyMode.ExecutionAndPublication);
        shellMaterialization = new(() => new(shellSta.Value), LazyThreadSafetyMode.ExecutionAndPublication);
        deviceMonitor.SnapshotChanged += DeviceMonitor_SnapshotChanged;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) == 1)
            return;

        App.RuntimeSettings.PropertyChanged += RuntimeSettings_PropertyChanged;
        runTask = RunAsync(lifetimeToken);
        _ = ObserveRunTaskAsync();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var startupStart = Stopwatch.GetTimestamp();
        var adbVersionTask = AdbHelper.CheckAdbVersion(cancellationToken);
        var adbRuntimeTask = InitializeAdbRuntimeAsync(adbVersionTask, startupStart, cancellationToken);
        var browserTask = Task.Run(Network.GetDefaultBrowser, cancellationToken);
        var historyDevicesTask = App.Settings.SaveDevices
            ? LoadHistoryDevicesAsync(cancellationToken)
            : Task.CompletedTask;
        var themeWatcherTask = mainWindow.StartThemeWatcherAsync(cancellationToken);
        var shellStaTask = Task.Run(() => shellSta.Value, cancellationToken);

        try
        {
            await historyDevicesTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.ReportBackgroundFailure(ex, "startup.history-devices");
        }

        try
        {
            await shellStaTask.ConfigureAwait(false);
            await UiScheduler.EnqueueAsync(
                "startup.clipboard",
                NativeMethods.InterceptClipboard.ScheduleClipboardRefresh,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.ReportBackgroundFailure(ex, "startup.shell-sta");
        }

        await adbRuntimeTask.ConfigureAwait(false);

        try
        {
            App.RuntimeSettings.DefaultBrowserPath = await browserTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.ReportBackgroundFailure(ex, "startup.default-browser");
        }

        try
        {
            await themeWatcherTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.ReportBackgroundFailure(ex, "startup.theme-watcher");
        }
    }

    public void LoadHistoryDevices()
    {
        if (!App.Settings.SaveDevices || lifetimeToken.IsCancellationRequested)
            return;

        _ = ObserveHistoryDevicesAsync(LoadHistoryDevicesAsync(lifetimeToken));
    }

    private Task LoadHistoryDevicesAsync(CancellationToken cancellationToken)
    {
        lock (historyDevicesLock)
        {
            if (Volatile.Read(ref historyDevicesLoaded) != 0)
                return Task.CompletedTask;
            if (!historyDevicesTask.IsCompleted)
                return historyDevicesTask;

            historyDevicesTask = LoadHistoryDevicesCoreAsync(cancellationToken);
            return historyDevicesTask;
        }
    }

    private async Task LoadHistoryDevicesCoreAsync(CancellationToken cancellationToken)
    {
        var devices = await Task.Run(Devices.LoadHistoryDevices, cancellationToken).ConfigureAwait(false);
        foreach (var device in devices)
        {
            await UiScheduler.EnqueueAsync(
                "startup.history-device",
                () =>
                {
                    if (!Devices.HistoryDeviceViewModels.Any(existing => existing.ID == device.ID))
                        Devices.UIList.Add(device);
                },
                cancellationToken).ConfigureAwait(false);
        }

        Volatile.Write(ref historyDevicesLoaded, 1);
    }

    private static async Task ObserveHistoryDevicesAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "devices.history");
        }
    }

    private async Task InitializeAdbRuntimeAsync(
        Task<bool> adbVersionTask,
        long startupStart,
        CancellationToken cancellationToken)
    {
        bool adbReady = false;
        try
        {
            adbReady = await adbVersionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            PerformanceTrace.Log.StartupStage("startup.adb-version", Stopwatch.GetElapsedTime(startupStart).Ticks / 10);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "startup.adb-version");
        }

        SetAdbAvailability(adbReady);
        Interlocked.Exchange(ref activated, 1);
    }

    private async Task ObserveRunTaskAsync()
    {
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, nameof(AppRuntime));
        }
    }

    private void RuntimeSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppRuntimeSettings.AdbVersion)
            || Volatile.Read(ref activated) == 0)
        {
            return;
        }

        SetAdbAvailability(App.RuntimeSettings.AdbVersion >= AdbExplorerConst.MIN_ADB_VERSION);
    }

    private void SetAdbAvailability(bool adbReady)
    {
        UiScheduler.EnqueueLatest(
            "runtime.adb-state",
            "runtime.adb-state",
            () => mainWindow.SetAdbRuntimeAvailable(adbReady));

        if (adbReady && Volatile.Read(ref uiReady) != 0)
            StartDeviceMonitor();
        else if (!adbReady)
            StopDeviceMonitor(clearDevices: true);
    }

    public void NotifyUiReady()
    {
        if (Interlocked.Exchange(ref uiReady, 1) != 0)
            return;

        if (App.RuntimeSettings.AdbVersion >= AdbExplorerConst.MIN_ADB_VERSION)
            StartDeviceMonitor();
    }

    private void StartDeviceMonitor()
    {
        lock (deviceMonitorLock)
        {
            if (deviceMonitorLifetime is not null || lifetimeToken.IsCancellationRequested)
                return;

            deviceMonitorLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            var monitorToken = deviceMonitorLifetime.Token;
            deviceMonitorTask = Task.WhenAll(
                RunResilientServiceAsync("device-monitor", deviceMonitor.RunAsync, monitorToken),
                RunResilientServiceAsync("device-metadata-loop", RunDeviceMetadataLoopAsync, monitorToken),
                RunResilientServiceAsync("runtime-status-loop", RunRuntimeStatusLoopAsync, monitorToken));
            _ = ObserveDeviceMonitorAsync(deviceMonitorTask, monitorToken);
        }
    }

    private static async Task RunResilientServiceAsync(
        string serviceName,
        Func<CancellationToken, Task> service,
        CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                await service(cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                    throw new InvalidOperationException($"{serviceName} stopped unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                App.ReportBackgroundFailure(ex, serviceName);
            }

            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                retryDelay = TimeSpan.FromSeconds(1);

            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
        }
    }

    private async Task RunDeviceMetadataLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(AdbExplorerConst.BATTERY_UPDATE_INTERVAL);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            UiScheduler.EnqueueLatest(
                "devices.metadata",
                "devices.metadata",
                RefreshDeviceMetadata);
        }
    }

    private void RefreshDeviceMetadata()
    {
        deviceMetadata.Refresh(
            Devices.LogicalDeviceViewModels,
            App.Settings.AutoRoot,
            App.Settings.PollBattery);
        DeviceHelper.UpdateWsaPkgStatus();

        if (App.FileActions.IsDriveViewVisible && App.Settings.PollDrives)
            _ = FileActionLogic.RefreshDrives();
    }

    private async Task RunRuntimeStatusLoopAsync(CancellationToken cancellationToken)
    {
        int watchdogTicks = 0;
        using var timer = new PeriodicTimer(AdbExplorerConst.RESPONSE_TIMER_INTERVAL);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!FileOperations.HasRunningSyncOperations)
            {
                try
                {
                    DiskUsageHelper.GetAdbDiskUsage();
                }
                catch (Exception ex)
                {
                    App.ReportBackgroundFailure(ex, "disk-usage.refresh");
                }
            }

            if (++watchdogTicks == 5)
            {
                watchdogTicks = 0;
                await deviceMonitor.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }

            UiScheduler.EnqueueLatest(
                "adb.server-health",
                "adb.server-health",
                mainWindow.RefreshServerHealth);
        }
    }

    private void StopDeviceMonitor(bool clearDevices)
    {
        CancellationTokenSource monitorLifetime;
        Task monitorTask;
        lock (deviceMonitorLock)
        {
            monitorLifetime = deviceMonitorLifetime;
            monitorTask = deviceMonitorTask;
            deviceMonitorLifetime = null;
            deviceMonitorTask = Task.CompletedTask;
        }

        if (monitorLifetime is not null)
        {
            monitorLifetime.Cancel();
            _ = DisposeMonitorLifetimeAsync(monitorTask, monitorLifetime);
        }

        if (clearDevices)
            deviceMonitor.Reset();
    }

    private static async Task DisposeMonitorLifetimeAsync(
        Task monitorTask,
        CancellationTokenSource monitorLifetime)
    {
        try
        {
            await monitorTask.ConfigureAwait(false);
        }
        catch
        { }
        finally
        {
            monitorLifetime.Dispose();
        }
    }

    private static async Task ObserveDeviceMonitorAsync(Task monitorTask, CancellationToken cancellationToken)
    {
        try
        {
            await monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "device-monitor.run");
        }
    }

    private void DeviceMonitor_SnapshotChanged(DeviceDelta delta) =>
        UiScheduler.EnqueueLatest(
            "devices.snapshot",
            "devices.snapshot",
            () =>
            {
                if (delta is null)
                    return;

                DeviceHelper.ApplyDeviceSnapshot(delta);
                PerformanceTrace.Log.DeviceSnapshotApplied(
                    delta.Sequence,
                    delta.Devices.Count,
                    Stopwatch.GetElapsedTime(delta.CapturedTimestamp).Ticks / 10);

                QueueDeviceMetadata(
                    delta,
                    Devices.LogicalDeviceViewModels.ToArray(),
                    App.Settings.AutoRoot,
                    App.Settings.PollBattery);
                UiScheduler.EnqueueLatest(
                    "devices.snapshot.wsa",
                    "devices.snapshot.wsa",
                    DeviceHelper.UpdateWsaPkgStatus);
                UiScheduler.EnqueueLatest(
                    "devices.snapshot.finalize",
                    "devices.snapshot.finalize",
                    () =>
                    {
                        var deviceToOpen = DeviceHelper.FinalizeDeviceSnapshot();
                        if (deviceToOpen is not null)
                        {
                            UiScheduler.EnqueueLatest(
                                "devices.open",
                                "devices.open",
                                () => DeviceHelper.OpenDevice(deviceToOpen));
                        }

                        if (App.RuntimeSettings.IsDevicesPaneOpen
                            && App.MdnsService.State is MDNS.MdnsState.Running)
                        {
                            _ = RefreshPairingServicesAsync();
                        }
                    });
            });

    private void QueueDeviceMetadata(
        DeviceDelta delta,
        LogicalDeviceViewModel[] devices,
        bool autoRoot,
        bool pollBattery)
    {
        lock (metadataSnapshotLock)
        {
            pendingMetadataSnapshot = (delta, devices, autoRoot, pollBattery);
            if (metadataSnapshotWorkerActive)
                return;

            metadataSnapshotWorkerActive = true;
            metadataSnapshotTask = Task.Run(ProcessDeviceMetadataSnapshots);
        }
    }

    private void ProcessDeviceMetadataSnapshots()
    {
        while (!lifetimeToken.IsCancellationRequested)
        {
            (DeviceDelta Delta, LogicalDeviceViewModel[] Devices, bool AutoRoot, bool PollBattery)? snapshot;
            lock (metadataSnapshotLock)
            {
                snapshot = pendingMetadataSnapshot;
                pendingMetadataSnapshot = null;
                if (snapshot is null)
                {
                    metadataSnapshotWorkerActive = false;
                    return;
                }
            }

            try
            {
                var value = snapshot.Value;
                deviceMetadata.ApplySnapshot(
                    value.Delta,
                    value.Devices,
                    value.AutoRoot,
                    value.PollBattery);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                App.ReportBackgroundFailure(ex, "devices.snapshot.metadata");
            }
        }

        lock (metadataSnapshotLock)
        {
            pendingMetadataSnapshot = null;
            metadataSnapshotWorkerActive = false;
        }
    }

    private async Task RefreshPairingServicesAsync()
    {
        if (Interlocked.Exchange(ref pairingRefreshActive, 1) != 0)
            return;

        try
        {
            var services = await Task.Run(WiFiPairingService.GetServices, lifetimeToken).ConfigureAwait(false);
            UiScheduler.EnqueueLatest(
                "devices.services",
                "devices.services",
                () => DeviceHelper.ListServices(services));
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "devices.services");
        }
        finally
        {
            Interlocked.Exchange(ref pairingRefreshActive, 0);
        }
    }

    public void RefreshDevices()
    {
        CancellationToken token;
        lock (deviceMonitorLock)
            token = deviceMonitorLifetime?.Token ?? CancellationToken.None;

        if (token.CanBeCanceled)
            _ = deviceMonitor.RefreshAsync(token);
    }

    public async Task RestartAdbServerAsync()
    {
        try
        {
            await Task.Run(() => ADBService.KillAdbServer(), lifetimeToken).ConfigureAwait(false);
            await UiScheduler.EnqueueAsync(
                "adb.restart",
                () =>
                {
                    App.MdnsService.State = MDNS.MdnsState.Disabled;
                    mainWindow.UpdateMdns();
                },
                lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "adb.restart");
        }
    }

    public async Task KillAdbProcessAsync()
    {
        try
        {
            await Task.Run(ADBService.KillAdbProcess, lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "adb.kill-process");
        }
    }

    public void RequestUi(UiCommand command)
    {
        if (command is UiCommand.NewFolder or UiCommand.NewFile or UiCommand.Rename or UiCommand.SelectAll)
        {
            _ = RunUiCommandAsync(command);
            return;
        }

        UiScheduler.EnqueueLatest(
            $"ui-command.{command}",
            $"ui-command.{command}",
            () => ExecuteUiCommand(command));
    }

    private async Task RunUiCommandAsync(UiCommand command)
        => await RunUiActionAsync(
            $"ui-command.{command}",
            () => ExecuteUiCommand(command)).ConfigureAwait(false);

    private void ExecuteUiCommand(UiCommand command)
    {
        switch (command)
        {
            case UiCommand.RefreshLocation:
                explorer.RefreshLocation();
                break;
            case UiCommand.InitializeDirectory:
                explorer.InitializeDirectory();
                break;
            case UiCommand.ShowDriveView:
                explorer.ShowDriveView();
                break;
            default:
                mainWindow.ExecuteUiCommand(command);
                break;
        }
    }

    public void RequestNavigation(AdbLocation location) =>
        _ = RunUiActionAsync("navigation.location", () => explorer.Navigate(location));

    public void RequestPathNavigation(string path) =>
        _ = RunUiActionAsync("navigation.path", () => explorer.NavigatePath(path));

    public void RequestDriveNavigation(DriveViewModel drive) =>
        _ = RunUiActionAsync("navigation.drive", () => explorer.NavigateDrive(drive));

    public void RequestFileNavigation(FileClass file) =>
        _ = RunUiActionAsync("navigation.file", () => explorer.NavigateFile(file));

    public bool ConsumeBackForwardNavigation() => explorer.ConsumeBackForwardNavigation();

    public void CloseDirectorySession()
    {
        if (UiScheduler.CheckAccess)
            explorer.CloseDirectory();
        else
            UiScheduler.EnqueueLatest("directory.close", "directory.close", explorer.CloseDirectory);
    }

    public void FollowLink(string target) => explorer.FollowLink(target);

    public void SetExplorerSource(System.Collections.IEnumerable source)
    {
        long version = Interlocked.Increment(ref explorerSourceVersion);
        UiScheduler.EnqueueLatest(
            "explorer.source.prepare",
            "explorer.source.prepare",
            () =>
            {
                if (version != Volatile.Read(ref explorerSourceVersion))
                    return;

                var view = CollectionViewSource.GetDefaultView(source);
                UiScheduler.EnqueueLatest(
                    "explorer.source",
                    "explorer.source",
                    () =>
                    {
                        if (version == Volatile.Read(ref explorerSourceVersion))
                            mainWindow.SetExplorerSource(view);
                    });
            });
    }

    public void SelectExplorerItem(object item) =>
        _ = RunUiActionAsync("explorer.select-item", () => mainWindow.SelectExplorerItem(item));

    public void SelectExplorerItems(IEnumerable<FileClass> items, DirectorySession expectedSession)
    {
        var itemList = items?.Distinct().ToArray() ?? [];
        if (itemList.Length > 0 && expectedSession is not null)
            _ = SelectExplorerItemsAsync(itemList, expectedSession);
    }

    private async Task SelectExplorerItemsAsync(
        IReadOnlyList<FileClass> items,
        DirectorySession expectedSession)
    {
        int selectionVersion = 0;
        bool selectionStarted = false;

        bool IsCurrentSession() =>
            ReferenceEquals(expectedSession, explorer.CurrentSession)
            && ReferenceEquals(expectedSession, App.ActiveDirectorySession);

        try
        {
            await UiScheduler.EnqueueAsync(
                "explorer.select-items.begin",
                () =>
                {
                    if (!IsCurrentSession())
                        return;

                    selectionVersion = mainWindow.BeginExplorerSelectionReset();
                    selectionStarted = true;
                },
                lifetimeToken).ConfigureAwait(false);

            if (!selectionStarted)
                return;

            while (selectionStarted)
            {
                bool sessionMatches = false;
                bool removed = false;
                await UiScheduler.EnqueueAsync(
                    "explorer.select-items.remove",
                    () =>
                    {
                        sessionMatches = IsCurrentSession();
                        if (sessionMatches)
                            removed = mainWindow.RemoveLastExplorerSelection(selectionVersion);
                    },
                    lifetimeToken).ConfigureAwait(false);

                if (!sessionMatches)
                    return;
                if (!removed)
                    break;
            }

            foreach (var item in items)
            {
                bool sessionMatches = false;
                await UiScheduler.EnqueueAsync(
                    "explorer.select-items.add",
                    () =>
                    {
                        sessionMatches = IsCurrentSession();
                        if (sessionMatches)
                            mainWindow.AddExplorerSelection(selectionVersion, item);
                    },
                    lifetimeToken).ConfigureAwait(false);

                if (!sessionMatches)
                    return;
            }

            await UiScheduler.EnqueueAsync(
                "explorer.select-items.end",
                () =>
                {
                    if (!selectionStarted)
                        return;

                    mainWindow.EndExplorerSelectionReset(
                        selectionVersion,
                        IsCurrentSession());
                    selectionStarted = false;
                },
                lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "explorer.select-items");
        }
        finally
        {
            if (selectionStarted)
            {
                UiScheduler.EnqueueLatest(
                    "explorer.select-items.restore",
                    "explorer.select-items.restore",
                    () => mainWindow.EndExplorerSelectionReset(selectionVersion));
            }
        }
    }

    public void RefreshPackages() =>
        UiScheduler.EnqueueLatest(
            "packages.refresh",
            "packages.refresh",
            explorer.RefreshPackages);

    private async Task RunUiActionAsync(string workName, Action action)
    {
        try
        {
            await UiScheduler.EnqueueAsync(
                workName,
                action,
                lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, workName);
        }
    }

    public Task<ClipboardSnapshot> ReadClipboardAsync(string currentDeviceId, CancellationToken cancellationToken) =>
        clipboardSnapshots.Value.ReadAsync(currentDeviceId, cancellationToken);

    public Task SetClipboardAsync(VirtualFileDataObject dataObject, CancellationToken cancellationToken) =>
        clipboardSnapshots.Value.SetAsync(dataObject, cancellationToken);

    public Task ClearClipboardAsync(bool clearSystemClipboard, CancellationToken cancellationToken) =>
        clipboardSnapshots.Value.ClearAsync(clearSystemClipboard, cancellationToken);

    public Task<bool> SetClipboardTextAsync(string text, CancellationToken cancellationToken) =>
        clipboardSnapshots.Value.SetTextAsync(text, cancellationToken);

    public Task<string> ReadClipboardTextAsync(CancellationToken cancellationToken) =>
        clipboardSnapshots.Value.ReadTextAsync(cancellationToken);

    public Task<DropSnapshot> ReadDropAsync(
        IDataObject dataObject,
        string currentDeviceId,
        CancellationToken cancellationToken) =>
        dropSnapshots.Value.ReadAsync(dataObject, currentDeviceId, cancellationToken);

    public Task<ShellMaterializationSnapshot> MaterializeDropAsync(
        IDataObject dataObject,
        FileDescriptor[] descriptors,
        bool hasShellIdList,
        bool hasFileContents,
        string targetDirectory,
        CancellationToken cancellationToken) =>
        shellMaterialization.Value.MaterializeDropAsync(
            dataObject,
            descriptors,
            hasShellIdList,
            hasFileContents,
            targetDirectory,
            cancellationToken);

    public Task<ShellMaterializationSnapshot> MaterializeClipboardAsync(
        FileDescriptor[] descriptors,
        bool hasShellIdList,
        bool hasFileContents,
        string targetDirectory,
        CancellationToken cancellationToken) =>
        shellMaterialization.Value.MaterializeClipboardAsync(
            descriptors,
            hasShellIdList,
            hasFileContents,
            targetDirectory,
            cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return;

        App.RuntimeSettings.PropertyChanged -= RuntimeSettings_PropertyChanged;
        deviceMonitor.SnapshotChanged -= DeviceMonitor_SnapshotChanged;
        FileOperations.Stop();
        lifetime.Cancel();
        explorer.Dispose();
        StopDeviceMonitor(clearDevices: false);
        if (shellIcons.IsValueCreated)
            shellIcons.Value.Dispose();
        if (shellSta.IsValueCreated)
            shellSta.Value.Dispose();
        CurrentAdbDevice = null;
        _ = FinishDisposeAsync();
    }

    private async Task FinishDisposeAsync()
    {
        try
        {
            await Task.WhenAll(runTask, historyDevicesTask, metadataSnapshotTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "runtime.dispose");
        }
        finally
        {
            deviceMetadata.Dispose();
            UiScheduler.Dispose();
            lifetime.Dispose();
        }
    }
}
