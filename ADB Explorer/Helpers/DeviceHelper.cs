using ADB_Explorer.Models;
using ADB_Explorer.Services;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;
using Windows.Management.Deployment;
using static ADB_Explorer.Models.AbstractDevice;

namespace ADB_Explorer.Helpers;

public static class DeviceHelper
{
    private static string pendingDeviceAddress;
    private static int deviceOpenRequestVersion;
    private static readonly object currentDeviceLifetimeLock = new();
    private static CancellationTokenSource currentDeviceLifetime;
    private static readonly TimeSpan WsaInstallCheckCacheDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WsaStatusUpdateInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WsaConnectAttemptInterval = TimeSpan.FromSeconds(5);
    private static readonly object WsaInstallCheckLock = new();
    private static DateTime lastWsaInstallCheck = DateTime.MinValue;
    private static DateTime lastWsaStatusUpdate = DateTime.MinValue;
    private static DateTime lastWsaConnectAttempt = DateTime.MinValue;
    private static bool? cachedWsaInstalled;
    private static int isWsaConnectRunning;
    private static int isWsaStatusUpdateRunning;

    public static DeviceStatus GetStatus(string status) => status switch
    {
        "device" or "online" or "recovery" or "sideload" => DeviceStatus.Ok,
        "unauthorized" or "authorizing" => DeviceStatus.Unauthorized,
        _ => DeviceStatus.Offline,
    };

    public static DeviceType GetType(string id, string status)
    {
        if (status == "recovery")
            return DeviceType.Recovery;

        if (status == "sideload")
            return DeviceType.Sideload;

        if (id.Contains("._adb-tls-"))
            return DeviceType.Service;
        if (id.Contains(':'))
        {
            return AdbExplorerConst.LOOPBACK_ADDRESSES.Contains(id.Split(':')[0])
                ? DeviceType.WSA
                : DeviceType.Remote;
        }

        return id.Contains("emulator")
            ? DeviceType.Emulator
            : DeviceType.Local;
    }

    /// <summary>
    /// Sets type of all drives with unknown type as external. Changes the <see cref="Drive"/> object itself.
    /// </summary>
    /// <param name="drives">The collection of <see cref="Drive"/>s to change</param>
    public static void SetExternalDrives(IEnumerable<LogicalDrive> drives)
    {
        if (drives is null)
            return;

        foreach (var item in drives.Where(d => d.Type == AbstractDrive.DriveType.Unknown))
        {
            item.Type = AbstractDrive.DriveType.External;
        }
    }

    public static string ParseDeviceName(string model, string device)
    {
        var name = device;
        if (device == device.ToLower())
            name = model;

        return name.Replace('_', ' ');
    }

    public static void BrowseDeviceAction(LogicalDeviceViewModel device)
    {
        OpenDevice(device);
    }

    public static void CloseCurrentDevice()
    {
        Interlocked.Increment(ref deviceOpenRequestVersion);
        CancelCurrentDeviceWork();
        (Application.Current as App)?.CloseDirectorySession();
        App.ActiveDevices.SetOpenDevice((LogicalDeviceViewModel)null);
        DriveHelper.ClearDrives();
        FileActionLogic.ClearExplorer();
        NavHistory.Reset();
        App.FileActions.IsExplorerVisible = false;
        App.ActiveAdbDevice = null;
    }

    internal static void CancelCurrentDeviceWork()
    {
        CancellationTokenSource cancellation;
        lock (currentDeviceLifetimeLock)
        {
            cancellation = currentDeviceLifetime;
            currentDeviceLifetime = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    internal static CancellationToken GetCurrentDeviceWorkToken()
    {
        lock (currentDeviceLifetimeLock)
            return currentDeviceLifetime?.Token ?? CancellationToken.None;
    }

    public static async Task DisconnectCurrentDeviceAsync()
    {
        var device = App.ActiveDevices.Current;
        if (device is null)
            return;

        try
        {
            switch (device.Type)
            {
                case DeviceType.Remote:
                    await Task.Run(() => ADBService.DisconnectNetworkDevice(device.ID));
                    EnsureHistoryDevice(device);
                    RemoveDevice(device);
                    return;

                case DeviceType.Emulator:
                    await Task.Run(() => ADBService.KillEmulator(device.ID));
                    RemoveDevice(device);
                    return;
            }
        }
        catch (Exception ex)
        {
            DialogService.ShowMessage(ex.Message, Strings.Resources.S_DISCONN_FAILED_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true);
            return;
        }

        CloseCurrentDevice();
    }

    private static void EnsureHistoryDevice(LogicalDeviceViewModel device)
    {
        if (device.Type is not DeviceType.Remote)
            return;

        string address = device.IsIpAddressValid ? device.IpAddress : device.ID.Split(':')[0];
        string port = device.ID.Contains(':') ? device.ID.Split(':')[1] : "5555";
        var existing = App.ActiveDevices.HistoryDeviceViewModels.FirstOrDefault(hist =>
            hist.IpAddress == address && hist.ConnectPort == port);

        if (existing is not null)
        {
            if (string.IsNullOrEmpty(existing.DeviceName) && !string.IsNullOrEmpty(device.Name))
            {
                existing.SetDeviceName(device.Name);
                if (App.Settings.SaveDevices)
                    App.ActiveDevices.StoreHistoryDevices();
            }

            return;
        }

        HistoryDeviceViewModel history = new(new HistoryDevice(address, port, device.Name));
        App.ActiveDevices.UIList.Add(history);

        if (App.Settings.SaveDevices)
            App.ActiveDevices.StoreHistoryDevices();
    }

    public static async void SideloadDeviceAction(LogicalDeviceViewModel device)
    {
        OpenFileDialog dialog = new()
        {
            Title = Strings.Resources.S_SIDELOAD_ROM_TITLE,
            Filter = $"{Strings.Resources.S_ROM_FILE}|*.zip",
            Multiselect = false,
        };

        if (dialog.ShowDialog() is not true)
            return;

        (int res, string stdout, string stderr) result;
        try
        {
            result = await Task.Run(() =>
            {
                var res = ADBService.ExecuteDeviceAdbCommand(
                    device.ID,
                    "sideload",
                    out string stdout,
                    out string stderr,
                    CancellationToken.None,
                    ADBService.EscapeAdbString(dialog.FileName));
                return (res, stdout, stderr);
            });
        }
        catch (Exception e)
        {
            DialogService.ShowMessage(
                e.Message,
                Strings.Resources.S_REBOOT_SIDELOAD,
                DialogService.DialogIcon.Critical,
                copyToClipboard: true);
            return;
        }

        DialogService.ShowMessage(string.Join('\n', result.stdout, result.stderr),
                                  Strings.Resources.S_REBOOT_SIDELOAD,
                                  result.res == 0 ? DialogService.DialogIcon.Informational
                                                  : DialogService.DialogIcon.Critical);
    }

    private static async void RemoveDeviceAction(DeviceViewModel device)
    {
        var message = device.Type is DeviceType.Emulator
            ? Strings.Resources.S_KILL_EMULATOR
            : Strings.Resources.S_REM_DEVICE;

        var name = device switch
        {
            HistoryDeviceViewModel dev when string.IsNullOrEmpty(dev.DeviceName) => dev.IpAddress,
            HistoryDeviceViewModel dev => dev.DeviceName,
            LogicalDeviceViewModel dev => dev.Name,
            _ => throw new NotImplementedException(),
        };

        var title = device.Type is DeviceType.Emulator
            ? Strings.Resources.S_KILL_EMULATOR_TITLE
            : Strings.Resources.S_REM_DEVICE_TITLE;

        var dialogTask = await DialogService.ShowConfirmation(message, string.Format(title, name));
        if (dialogTask.Item1 is not ContentDialogResult.Primary)
            return;

        if (device.Type is DeviceType.Emulator)
        {
            try
            {
                await Task.Run(() => ADBService.KillEmulator(device.ID));
            }
            catch (Exception ex)
            {
                DialogService.ShowMessage(ex.Message, Strings.Resources.S_DISCONN_FAILED_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true);
                return;
            }
        }
        else if (device.Type is DeviceType.Remote)
        {
            try
            {
                await Task.Run(() => ADBService.DisconnectNetworkDevice(device.ID));
                EnsureHistoryDevice((LogicalDeviceViewModel)device);
            }
            catch (Exception ex)
            {
                DialogService.ShowMessage(ex.Message, Strings.Resources.S_DISCONN_FAILED_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true);
                return;
            }
        }
        else if (device.Type is DeviceType.History)
        { } // No additional action is required
        else
        {
            throw new NotImplementedException();
        }

        RemoveDevice(device);
    }

    public static DeviceAction RemoveDeviceCommand(DeviceViewModel device) => new(
            () => device.Type is DeviceType.History
                || !App.RuntimeSettings.IsManualPairingInProgress
                    && device.Type is DeviceType.Remote or DeviceType.Emulator,
            () => RemoveDeviceAction(device),
            device.Type switch
            {
                DeviceType.Remote => Strings.Resources.S_REM_DEV,
                DeviceType.Emulator => Strings.Resources.S_REM_EMU,
                DeviceType.History => Strings.Resources.S_REM_HIST_DEV,
                _ => "",
            });

    public static DeviceAction ToggleRootDeviceCommand(LogicalDeviceViewModel device) => new(
        () => device.Root is not RootStatus.Forbidden
            && device.Status is DeviceStatus.Ok
            && device.Type is not DeviceType.Sideload and not DeviceType.Recovery,
        () => ToggleRootAction(device));

    private static async void ToggleRootAction(LogicalDeviceViewModel device)
    {
        bool rootEnabled = device.Root is RootStatus.Enabled;
        var status = await Task.Run(() => device.ChangeRoot(!rootEnabled));
        device.SetRootStatus(status);

        if (device.Root is RootStatus.Forbidden)
            DialogService.ShowMessage(Strings.Resources.S_ROOT_FORBID, Strings.Resources.S_ROOT_FORBID_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true);
    }

    public static DeviceAction ConnectDeviceCommand(NewDeviceViewModel device) => new(
        () => {
            if (!device.IsConnectPortValid)
                return false;

            if (!device.IsIpAddressValid && !device.IsHostNameValid)
                return false;

            return !device.IsPairingEnabled
                   || (device.IsPairingCodeValid && device.IsPairingPortValid);
        },
        () => ConnectDevice(device));

    public static DeviceAction LaunchWsa(WsaPkgDeviceViewModel device) => new(
        () => device.Status is DeviceStatus.Ok,
        async () =>
        {
            if (App.Settings.ShowLaunchWsaMessage)
            {
                var result = await DialogService.ShowConfirmation(Strings.Resources.S_WSA_LAUNCH,
                                                                  Strings.Resources.S_WSA_DIALOG_TITLE,
                                                                  primaryText: Strings.Resources.S_BUTTON_LAUNCH,
                                                                  checkBoxText: Strings.Resources.S_DONT_SHOW_AGAIN,
                                                                  icon: DialogService.DialogIcon.Exclamation,
                                                                  censorContent: false);

                App.Settings.ShowLaunchWsaMessage = !result.Item2;

                if (result.Item1 is not ContentDialogResult.Primary)
                    return;
            }

            device.SetLastLaunch();
            device.SetStatus(DeviceStatus.Unauthorized);
            Process.Start($"{AdbExplorerConst.WSA_PROCESS_NAME}.exe");
        });

    internal static Predicate<DeviceViewModel> CreateDevicePredicate(IEnumerable<DeviceViewModel> devices)
    {
        var deviceList = devices.ToList();
        var logicalDevices = deviceList.OfType<LogicalDeviceViewModel>().ToList();
        var serviceDevices = deviceList.OfType<ServiceDeviceViewModel>().ToList();
        HashSet<string> serviceIps = [.. serviceDevices.Select(service => service.IpAddress)];
        HashSet<string> onlineRemoteOrLocalIps = [.. logicalDevices
            .Where(logical => logical.Type is DeviceType.Remote or DeviceType.Local
                && logical.Status is DeviceStatus.Ok)
            .Select(logical => logical.IpAddress)];
        HashSet<string> onlineLogicalIps = [.. logicalDevices
            .Where(logical => logical.Status is not DeviceStatus.Offline)
            .Select(logical => logical.IpAddress)];
        HashSet<string> logicalIps = [.. logicalDevices.Select(logical => logical.IpAddress)];
        HashSet<string> qrServiceIps = [.. serviceDevices
            .Where(service => service.MdnsType is ServiceDevice.ServiceType.QrCode)
            .Select(service => service.IpAddress)];
        var onlineLocalDevices = logicalDevices
            .Where(logical => logical.Type is DeviceType.Local && logical.Status is DeviceStatus.Ok)
            .ToList();
        var logicalNameCounts = logicalDevices
            .GroupBy(logical => logical.Name ?? "", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        bool onlineWsaExists = logicalDevices.Any(logical =>
            logical.Type is DeviceType.WSA && logical.Status is not DeviceStatus.Offline);

        foreach (var logical in logicalDevices.Where(logical => logical.Type is not DeviceType.Emulator))
            logical.UseIdForName = logicalNameCounts.GetValueOrDefault(logical.Name ?? "") > 1;

        return device =>
        {
            // current device cannot be hidden
            if (device is LogicalDeviceViewModel { IsOpen: true })
                return true;

            if (device is LogicalDeviceViewModel && device.Type is DeviceType.Service)
            {
                if (device.Status is DeviceStatus.Offline)
                {
                    // if a logical service is offline, and we have one of its services - hide the logical service
                    return !serviceIps.Contains(device.IpAddress);
                }

                // if there's a logical service and a remote device with the same IP - hide the logical service
                return !onlineRemoteOrLocalIps.Contains(device.IpAddress);
            }

            if (device is LogicalDeviceViewModel && device.Type is DeviceType.Remote)
            {
                // if a remote device is also connected by USB and both are authorized - hide the remote device
                return !onlineLocalDevices.Any(usb =>
                    (!string.IsNullOrEmpty(device.ID)
                        && !string.IsNullOrEmpty(usb.ID)
                        && device.ID.Contains(usb.ID))
                    || usb.IpAddress == device.IpAddress);
            }

            if (device is HistoryDeviceViewModel hist)
            {
                // if there's any device with the IP of a history device - hide the history device
                return !logicalIps.Contains(hist.IpAddress)
                    && !logicalIps.Contains(hist.HostName)
                    && !serviceIps.Contains(hist.IpAddress)
                    && !serviceIps.Contains(hist.HostName);
            }

            if (device is ServiceDeviceViewModel service)
            {
                // connect services are always hidden
                if (service is ConnectServiceViewModel)
                    return false;

                // if there's any online logical device with the IP of a pairing service - hide the pairing service
                if (onlineLogicalIps.Contains(service.IpAddress))
                    return false;

                // if there's any QR service with the IP of a code pairing service - hide the code pairing service
                if (service.MdnsType is ServiceDevice.ServiceType.PairingCode
                    && qrServiceIps.Contains(service.IpAddress))
                    return false;
            }

            if (device is WsaPkgDeviceViewModel wsaPkg)
            {
                // if WSA is not installed - hide it
                if (wsaPkg.Status is DeviceStatus.Offline)
                    return false;

                // if an online logical WSA device exists, the WSA package is hidden
                if (onlineWsaExists)
                    return false;
            }

            // if there's an offline WSA device - hide it
            if (device is LogicalDeviceViewModel { Type: DeviceType.WSA, Status: DeviceStatus.Offline })
                return false;

            return true;
        };
    }

    internal static DeviceViewModel[] GetVisibleDevices(IEnumerable<DeviceViewModel> devices)
    {
        var deviceList = devices.ToList();
        var predicate = CreateDevicePredicate(deviceList);
        return [.. deviceList.Where(device => predicate(device)).OrderBy(device => device.Type)];
    }

    public static async void ListServices(IEnumerable<ServiceDevice> services)
    {
        if (services is null)
            return;

        var serviceList = services.ToList();

        if (!App.ActiveDevices.ServicesChanged(serviceList))
            return;

        var viewModels = serviceList.Select(service => ServiceDeviceViewModel.New(service, false)).ToList();
        App.ActiveDevices.UpdateServices(viewModels);

        var qrServices = App.ActiveDevices.ServiceDeviceViewModels.Where(service =>
            service.MdnsType == ServiceDevice.ServiceType.QrCode
            && service.ID == App.QrClass.ServiceName);

        if (qrServices.Any())
        {
            await PairService(qrServices.First());
        }
    }

    public static async Task<bool> PairService(ServiceDeviceViewModel service)
    {
        var code = service.MdnsType == ServiceDevice.ServiceType.QrCode
            ? App.QrClass.Password
            : service.PairingCode;

        try
        {
            await Task.Run(() => ADBService.PairNetworkDevice(service.ID, code));
            return true;
        }
        catch (Exception ex)
        {
            DialogService.ShowMessage(ex.Message, Strings.Resources.S_PAIR_ERR_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true);
            return false;
        }
    }

    public static void CollapseDevices()
    {
        if (App.ActiveDevices is not null)
        {
            foreach (var device in App.ActiveDevices.UIList)
                device.DeviceSelected = false;
        }

        App.RuntimeSettings.IsPathBoxFocused = false;
    }

    public static async void PairNewDevice()
    {
        var dev = (NewDeviceViewModel)App.RuntimeSettings.ConnectNewDevice;
        bool success;
        try
        {
            await Task.Run(() => ADBService.PairNetworkDevice(dev.PairingAddress, dev.PairingCode));
            success = true;
        }
        catch (Exception ex)
        {
            DialogService.ShowMessage(ex.Message, Strings.Resources.S_PAIR_ERR_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true);
            success = false;
        }

        if (success)
            ConnectNewDevice();

        App.RuntimeSettings.ConnectNewDevice = null;
        App.RuntimeSettings.IsManualPairingInProgress = false;
    }

    public static async void ConnectNewDevice()
    {
        var dev = (NewDeviceViewModel)App.RuntimeSettings.ConnectNewDevice;
        bool success = false;
        Exception failure = null;
        try
        {
            await Task.Run(() => ADBService.ConnectNetworkDevice(dev.ConnectAddress));
            success = true;
        }
        catch (Exception ex)
        {
            success = AdbExplorerConst.LOOPBACK_ADDRESSES.Contains(dev.IpAddress);
            failure = ex;
        }

        if (!success && failure is not null)
        {
            if (failure.Message.Contains(Strings.Resources.S_FAILED_CONN + dev.ConnectAddress)
                && App.RuntimeSettings.ConnectNewDevice is NewDeviceViewModel { IsPairingEnabled: false })
            {
                App.ActiveDevices.CurrentNewDevice.EnablePairing();
            }
            else
            {
                DialogService.ShowMessage(failure.Message, Strings.Resources.S_FAILED_CONN_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true);
            }
        }

        if (success)
        {
            string newDeviceAddress = "";
            var newDevice = App.RuntimeSettings.ConnectNewDevice is null ? App.ActiveDevices.CurrentNewDevice : App.RuntimeSettings.ConnectNewDevice;

            if (newDevice.Type is DeviceType.New && !AdbExplorerConst.LOOPBACK_ADDRESSES.Contains(newDevice.IpAddress))
            {
                if (App.Settings.SaveDevices)
                    App.ActiveDevices.AddHistoryDevice(HistoryDeviceViewModel.New(dev));

                newDeviceAddress = dev.ConnectAddress;
                ((NewDeviceViewModel)newDevice).ClearDevice();
            }
            else if (newDevice.Type is DeviceType.History)
            {
                newDeviceAddress = ((HistoryDeviceViewModel)newDevice).ConnectAddress;

                if (App.Settings.SaveDevices)
                    App.ActiveDevices.StoreHistoryDevices();
            }

            CollapseDevices();
            RequestOpenDevice(newDeviceAddress);
        }

        App.RuntimeSettings.ConnectNewDevice = null;
        App.RuntimeSettings.IsManualPairingInProgress = false;
    }

    public static IEnumerable<LogicalDeviceViewModel> ReconnectFileOpDevice(IEnumerable<LogicalDeviceViewModel> devices)
    {
        var connectedDevices = devices.ToList();
        var currentUiDevices = App.ActiveDevices.UIList.ToHashSet();
        var pastDevices = App.ActiveFileOperations.Operations
            .Where(op => op.IsPastOp && !currentUiDevices.Contains(op.Device.Device))
            .Select(op => op.Device.Device)
            .GroupBy(device => device.ID)
            .ToDictionary(group => group.Key, group => group.First());

        for (int i = 0; i < connectedDevices.Count; i++)
        {
            if (pastDevices.TryGetValue(connectedDevices[i].ID, out var pastDevice))
            {
                pastDevice.UpdateDevice(connectedDevices[i]);
                connectedDevices[i] = pastDevice;
            }
        }

        return connectedDevices;
    }

    public static void RequestOpenDevice(string deviceAddress)
    {
        if (!string.IsNullOrWhiteSpace(deviceAddress))
            Interlocked.Exchange(ref pendingDeviceAddress, deviceAddress);

        TryOpenPendingDevice();
    }

    internal static void ApplyDeviceSnapshot(DeviceDelta delta)
    {
        var existingIds = App.ActiveDevices.LogicalDeviceViewModels
            .Select(device => device.ID)
            .ToHashSet(StringComparer.Ordinal);
        var addedDevices = ReconnectFileOpDevice(
            delta.Devices
                .Where(device => !existingIds.Contains(device.ID))
                .Select(device => new LogicalDeviceViewModel(device, false))).ToList();
        App.ActiveDevices.ApplySnapshot(delta, addedDevices);
        (Application.Current as App)?.RequestUi(UiCommand.FilterDevices);
    }

    internal static LogicalDeviceViewModel FinalizeDeviceSnapshot()
    {
        var currentDevice = App.ActiveDevices.Current;
        if (currentDevice is not null
            && currentDevice.Status is DeviceStatus.Ok
            && App.ActiveDevices.LogicalDeviceViewModels.Contains(currentDevice))
        {
            return null;
        }

        if (currentDevice is not null || App.ActiveAdbDevice is not null)
            CloseCurrentDevice();

        return TakePendingDeviceToOpen();
    }

    private static void TryOpenPendingDevice()
    {
        var device = TakePendingDeviceToOpen();
        if (device is not null)
            OpenDevice(device);
    }

    private static LogicalDeviceViewModel TakePendingDeviceToOpen()
    {
        if (App.ActiveDevices?.DevicesAvailable(true) is true)
            return null;

        var selectedAddress = Interlocked.Exchange(ref pendingDeviceAddress, null);
        if (string.IsNullOrEmpty(selectedAddress) && !App.Settings.AutoOpen)
            return null;

        var availableDevices = App.ActiveDevices?.LogicalDeviceViewModels
            .Where(device => device.Status is DeviceStatus.Ok)
            .ToList() ?? [];
        if (availableDevices.Count == 0)
        {
            if (!string.IsNullOrEmpty(selectedAddress))
                Interlocked.CompareExchange(ref pendingDeviceAddress, selectedAddress, null);
            return null;
        }

        var device = !string.IsNullOrEmpty(selectedAddress)
            ? availableDevices.FirstOrDefault(item => item.ID == selectedAddress)
            : availableDevices.FirstOrDefault(item => item.ID == App.Settings.LastDeviceId)
                ?? availableDevices.FirstOrDefault(item => item.Name == App.Settings.LastDevice)
                ?? availableDevices.FirstOrDefault();

        if (device is null)
        {
            Interlocked.CompareExchange(ref pendingDeviceAddress, selectedAddress, null);
            return null;
        }

        return device;
    }

    private static async Task InitDevice(
        LogicalDeviceViewModel currentDevice,
        ADBService.AdbDevice currentAdbDevice,
        string reopenPath,
        CancellationToken cancellationToken)
    {
        if (currentDevice is null || currentAdbDevice is null || cancellationToken.IsCancellationRequested)
            return;

        try
        {
            string version = await currentAdbDevice.GetAndroidVersion(cancellationToken).ConfigureAwait(false);
            if (Application.Current is App app)
            {
                await app.EnqueueUiAsync(
                    "device.initialize.version",
                    () =>
                    {
                        if (ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
                            && ReferenceEquals(App.ActiveDevices.Current, currentDevice))
                        {
                            currentDevice.SetAndroidVersion(version);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            App.AddCommandLog($"@ADB Explorer: failed to read Android version: {e.Message}");
        }

        if (!ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
            || !ReferenceEquals(App.ActiveDevices.Current, currentDevice))
        {
            return;
        }

        await FileActionLogic.RefreshDrives(
            string.IsNullOrEmpty(reopenPath),
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(reopenPath)
            || !ReferenceEquals(App.ActiveAdbDevice, currentAdbDevice)
            || !ReferenceEquals(App.ActiveDevices.Current, currentDevice))
        {
            return;
        }

        (Application.Current as App)?.RequestNavigation(new(reopenPath));
    }

    private static async Task ObserveDeviceInitializationAsync(Task initialization)
    {
        try
        {
            await initialization.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "device.initialize");
        }
    }

    public static void TestDevices()
    {
        //DevicesObject.UpdateServices(new List<ServiceDevice>() { new PairingService("sdfsdfdsf_adb-tls-pairing._tcp.", "192.168.1.20", "5555") { MdnsType = ServiceDevice.ServiceType.PairingCode } });
        //DevicesObject.UpdateDevices(new List<LogicalDevice>() { LogicalDevice.New("Test", "test.ID", "device") });
    }

    public static void ConnectDevice(DeviceViewModel device)
    {
        App.RuntimeSettings.ConnectNewDevice = device;
        App.RuntimeSettings.IsManualPairingInProgress = true;
        App.ActiveDevices.CurrentNewDevice = (NewDeviceViewModel)device;

        if (device is NewDeviceViewModel newDevice && newDevice.IsPairingEnabled)
            PairNewDevice();
        else
            ConnectNewDevice();
    }

    public static void RemoveDevice(DeviceViewModel device)
    {
        switch (device)
        {
            case LogicalDeviceViewModel logical:
                if (logical.IsOpen)
                {
                    CloseCurrentDevice();
                }

                App.ActiveDevices.UIList.Remove(device);
                logical.DetachRuntimeSettings();
                (Application.Current as App)?.RequestUi(UiCommand.FilterDevices);
                ((App)Application.Current).RefreshDevices();
                break;
            case HistoryDeviceViewModel hist:
                App.ActiveDevices.RemoveHistoryDevice(hist);
                break;
            default:
                throw new NotSupportedException();
        }
    }

    public static void OpenDevice(LogicalDeviceViewModel device, AdbLocation navigation = null)
    {
        if (device is null
            || device.Status is not DeviceStatus.Ok
            || Application.Current is not App app)
        {
            return;
        }

        int requestVersion = Interlocked.Increment(ref deviceOpenRequestVersion);
        CancellationTokenSource deviceLifetime = null;
        ADBService.AdbDevice currentAdbDevice = null;
        string reopenPath = null;

        bool IsCurrentRequest() =>
            Volatile.Read(ref deviceOpenRequestVersion) == requestVersion
            && device.Status is DeviceStatus.Ok
            && App.ActiveDevices.UIList.Contains(device);

        void Enqueue(string workName, Action action) =>
            app.EnqueueUiLatest("devices.open", workName, action);

        void Clear()
        {
            if (!IsCurrentRequest())
                return;

            FileActionLogic.ClearExplorer();
            NavHistory.Reset();
            CancelCurrentDeviceWork();
            Enqueue("devices.open.prepare-drives", PrepareDrives);
        }

        void PrepareDrives()
        {
            if (!IsCurrentRequest())
                return;

            device.InitializeDrives();
            Enqueue("devices.open.prepare-connection", PrepareConnection);
        }

        void PrepareConnection()
        {
            if (!IsCurrentRequest())
                return;

            deviceLifetime = new();
            lock (currentDeviceLifetimeLock)
                currentDeviceLifetime = deviceLifetime;
            currentAdbDevice = new(device);
            App.ActiveAdbDevice = currentAdbDevice;
            Enqueue("devices.open.commit", Commit);
        }

        void Commit()
        {
            if (!IsCurrentRequest() || deviceLifetime is null || currentAdbDevice is null)
                return;

            App.ActiveDevices.SetOpenDevice(device);
            app.RequestUi(UiCommand.InitializeDirectory);
            App.RuntimeSettings.IsDevicesPaneOpen = false;
            if (navigation is not null)
                app.RequestNavigation(navigation);
            Enqueue("devices.open.initialize-view", InitializeView);
        }

        void InitializeView()
        {
            if (!IsCurrentRequest() || !ReferenceEquals(App.ActiveDevices.Current, device))
                return;

            reopenPath = navigation is null ? GetAutoOpenPath(device) : null;
            if (navigation is null && string.IsNullOrEmpty(reopenPath))
                app.RequestUi(UiCommand.ShowDriveView);
            else
                App.FileActions.IsDriveViewVisible = false;

            app.RequestUi(UiCommand.FilterDrives);
            App.FileActions.PushPackageEnabled = App.Settings.EnableApk && device.Type is not DeviceType.Recovery;
            Enqueue("devices.open.initialize-operations", InitializeOperations);
        }

        void InitializeOperations()
        {
            if (!IsCurrentRequest() || !ReferenceEquals(App.ActiveDevices.Current, device))
                return;

            App.ActiveFileOperations.MoveOperationsToPast();
            FileActionLogic.UpdateFileActions();
            Enqueue("devices.open.initialize-background", InitializeBackground);
        }

        void InitializeBackground()
        {
            if (!IsCurrentRequest()
                || !ReferenceEquals(App.ActiveDevices.Current, device)
                || deviceLifetime is null
                || currentAdbDevice is null)
            {
                return;
            }

            _ = ObserveDeviceInitializationAsync(Task.Run(() =>
                InitDevice(device, currentAdbDevice, reopenPath, deviceLifetime.Token)));
        }

        Enqueue("devices.open.clear", Clear);
    }

    private static string GetAutoOpenPath(LogicalDeviceViewModel device)
    {
        if (!App.Settings.AutoOpen || device is null)
            return null;

        string path = App.Settings.GetLastDevicePath(device.ID);
        if (!string.IsNullOrWhiteSpace(path))
            return path;

        return string.Equals(App.Settings.LastDevice, device.Name, StringComparison.Ordinal)
            ? App.Settings.LastDevicePath
            : null;
    }

    public static void ConnectWsaDevice()
    {
        if (DateTime.Now - lastWsaConnectAttempt < WsaConnectAttemptInterval
            || Interlocked.Exchange(ref isWsaConnectRunning, 1) == 1)
        {
            return;
        }

        lastWsaConnectAttempt = DateTime.Now;

        try
        {
            if (App.ActiveDevices.UIList.OfType<WsaPkgDeviceViewModel>().Any(wsa => wsa.Status is not DeviceStatus.Unauthorized))
                return;

            if (App.ActiveDevices.LogicalDeviceViewModels.Any(dev => dev.Type is DeviceType.WSA && dev.Status is not DeviceStatus.Offline))
                return;

            var wsaPid = GetWsaPid();
            if (wsaPid is null)
                return;

            var wsaIp = Network.GetWsaIp();
            if (wsaIp is null)
                return;

            var retCode = ADBService.ExecuteCommand("cmd.exe",
                                                    "/C",
                                                    out string stdout,
                                                    out _,
                                                    Encoding.UTF8,
                                                    CancellationToken.None, "\"netstat", "-nao", "|", "findstr", $"{wsaPid.Value}\"");

            if (retCode != 0)
                return;

            var match = AdbRegEx.RE_NETSTAT_TCP_SOCK().Match(stdout);
            if (match.Groups?.Count < 2)
                return;

            var netstatIp = match.Groups["IP"].Value;
            App.ActiveDevices.WsaPort = match.Groups["Port"].Value;
            if (!AdbExplorerConst.LOOPBACK_ADDRESSES.Contains(netstatIp))
                return;

            App.ActiveDevices.CurrentNewDevice = new(new())
            {
                IpAddress = AdbExplorerConst.WIN_LOOPBACK_ADDRESS,
                ConnectPort = App.ActiveDevices.WsaPort,
            };
            App.ActiveDevices.CurrentNewDevice.ConnectCommand.Execute();
        }
        finally
        {
            Interlocked.Exchange(ref isWsaConnectRunning, 0);
        }
    }

    public static int? GetWsaPid()
    {
        var processes = Process.GetProcessesByName(AdbExplorerConst.WSA_PROCESS_NAME);
        try
        {
            return processes.FirstOrDefault()?.Id;
        }
        finally
        {
            processes.ForEach(process => process.Dispose());
        }
    }

    private static bool IsWsaInstalled()
    {
        lock (WsaInstallCheckLock)
        {
            if (cachedWsaInstalled is bool installed
                && DateTime.Now - lastWsaInstallCheck < WsaInstallCheckCacheDuration)
            {
                return installed;
            }
        }

        bool isInstalled = false;
        try
        {
            foreach (var pkg in new PackageManager().FindPackagesForUser("") ?? [])
            {
                try
                {
                    if (pkg.DisplayName?.Contains(AdbExplorerConst.WSA_PACKAGE_NAME, StringComparison.OrdinalIgnoreCase) is true)
                    {
                        isInstalled = true;
                        break;
                    }
                }
                catch (COMException)
                { }
            }
        }
        catch
        { }

        lock (WsaInstallCheckLock)
        {
            cachedWsaInstalled = isInstalled;
            lastWsaInstallCheck = DateTime.Now;
        }

        return isInstalled;
    }

    public static void UpdateWsaPkgStatus()
    {
        if (DateTime.Now - lastWsaStatusUpdate < WsaStatusUpdateInterval
            || Interlocked.Exchange(ref isWsaStatusUpdateRunning, 1) == 1)
            return;

        var wsa = App.ActiveDevices.UIList.OfType<WsaPkgDeviceViewModel>().FirstOrDefault();
        if (wsa is null)
        {
            Interlocked.Exchange(ref isWsaStatusUpdateRunning, 0);
            return;
        }

        if (App.ActiveDevices.LogicalDeviceViewModels.Any(dev => dev.Type is DeviceType.WSA && dev.Status is not DeviceStatus.Offline))
        {
            Interlocked.Exchange(ref isWsaStatusUpdateRunning, 0);
            return;
        }

        if (wsa.LastLaunch == DateTime.MaxValue || DateTime.Now - wsa.LastLaunch < AdbExplorerConst.WSA_LAUNCH_DELAY)
        {
            Interlocked.Exchange(ref isWsaStatusUpdateRunning, 0);
            return;
        }

        var oldStatus = wsa.Status;
        var lastLaunch = wsa.LastLaunch;
        _ = UpdateWsaPkgStatusAsync(wsa, oldStatus, lastLaunch);
    }

    private static async Task UpdateWsaPkgStatusAsync(
        WsaPkgDeviceViewModel wsa,
        DeviceStatus oldStatus,
        DateTime lastLaunch)
    {
        try
        {
            if (oldStatus is DeviceStatus.Unauthorized
                && DateTime.Now - lastLaunch > AdbExplorerConst.WSA_CONNECT_TIMEOUT
                && lastLaunch == DateTime.MinValue)
            {
                if (Application.Current is App app)
                    await app.EnqueueUiAsync("wsa.launch-time", () => wsa.SetLastLaunch());
                return;
            }

            var result = await Task.Run(() =>
            {
                if (oldStatus is DeviceStatus.Unauthorized
                    && DateTime.Now - lastLaunch > AdbExplorerConst.WSA_CONNECT_TIMEOUT)
                {
                    return (Status: DeviceStatus.Ok, ResetLaunchTime: true);
                }

                var status = GetWsaPid() is not null
                    ? DeviceStatus.Unauthorized
                    : IsWsaInstalled()
                        ? DeviceStatus.Ok
                        : DeviceStatus.Offline;
                return (Status: status, ResetLaunchTime: false);
            }).ConfigureAwait(false);

            lastWsaStatusUpdate = DateTime.Now;
            if ((result.Status != oldStatus || result.ResetLaunchTime)
                && Application.Current is App currentApp)
            {
                await currentApp.EnqueueUiAsync("wsa.status", () =>
                {
                    if (result.ResetLaunchTime)
                        wsa.SetLastLaunch(DateTime.MaxValue);

                    if (result.Status != oldStatus)
                    {
                        wsa.SetStatus(result.Status);
                        currentApp.RequestUi(UiCommand.FilterDevices);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "wsa.status");
        }
        finally
        {
            Interlocked.Exchange(ref isWsaStatusUpdateRunning, 0);
        }
    }
}
