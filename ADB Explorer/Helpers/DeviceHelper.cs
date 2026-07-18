using ADB_Explorer.Models;
using ADB_Explorer.Services;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;
using Windows.Management.Deployment;
using static ADB_Explorer.Models.AbstractDevice;

namespace ADB_Explorer.Helpers;

public static class DeviceHelper
{
    private static int deviceOpenRequestVersion;
    private static readonly TimeSpan WsaInstallCheckCacheDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RootStatusUpdateInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WsaStatusUpdateInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WsaConnectAttemptInterval = TimeSpan.FromSeconds(5);
    private static readonly object WsaInstallCheckLock = new();
    private static DateTime lastWsaInstallCheck = DateTime.MinValue;
    private static DateTime lastRootStatusUpdate = DateTime.MinValue;
    private static DateTime lastWsaStatusUpdate = DateTime.MinValue;
    private static DateTime lastWsaConnectAttempt = DateTime.MinValue;
    private static bool? cachedWsaInstalled;
    private static int isRootStatusUpdateRunning;
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

    public static LogicalDrive GetMmcDrive(IEnumerable<LogicalDrive> drives, string deviceID)
    {
        if (drives is null)
            return null;

        var currentDevice = Data.CurrentADBDevice;

        // Try to find the MMC in the props
        if (currentDevice?.ID == deviceID && currentDevice.MmcProp is string mmcId)
        {
            return drives.FirstOrDefault(d => d.ID == mmcId);
        }
        // If OTG exists, but no MMC ID - there is no MMC
        else if (currentDevice?.ID == deviceID && currentDevice.OtgProp is not null)
            return null;

        var externalDrives = drives.Where(d => d.Type is AbstractDrive.DriveType.Unknown);

        switch (externalDrives.Count())
        {
            // MMC ID has to be acquired if more than one extension drive exists
            case > 1:
                var mmc = ADBService.GetMmcId(deviceID);
                return drives.FirstOrDefault(d => d.ID == mmc);

            // Only check whether MMC exists if there's only one drive
            case 1:
                return ADBService.MmcExists(deviceID) ? externalDrives.First() : null;
            default:
                return null;
        }
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
        Data.DirList?.Stop();
        Data.DevicesObject.SetOpenDevice((LogicalDeviceViewModel)null);
        DriveHelper.ClearDrives();
        FileActionLogic.ClearExplorer();
        NavHistory.Reset();
        Data.FileActions.IsExplorerVisible = false;
        Data.CurrentADBDevice = null;
        Data.DirList = null;
    }

    public static async Task DisconnectCurrentDeviceAsync()
    {
        var device = Data.DevicesObject.Current;
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
        var existing = Data.DevicesObject.HistoryDeviceViewModels.FirstOrDefault(hist =>
            hist.IpAddress == address && hist.ConnectPort == port);

        if (existing is not null)
        {
            if (string.IsNullOrEmpty(existing.DeviceName) && !string.IsNullOrEmpty(device.Name))
            {
                existing.SetDeviceName(device.Name);
                if (Data.Settings.SaveDevices)
                    Data.DevicesObject.StoreHistoryDevices();
            }

            return;
        }

        HistoryDeviceViewModel history = new(new HistoryDevice(address, port, device.Name));
        Data.DevicesObject.UIList.Add(history);

        if (Data.Settings.SaveDevices)
            Data.DevicesObject.StoreHistoryDevices();
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
                || !Data.RuntimeSettings.IsManualPairingInProgress
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

        await Task.Run(() => device.EnableRoot(!rootEnabled));

        if (device.Root is RootStatus.Forbidden)
        {
            App.Current.Dispatcher.Invoke(() => DialogService.ShowMessage(Strings.Resources.S_ROOT_FORBID, Strings.Resources.S_ROOT_FORBID_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true));
        }
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
        () => Data.RuntimeSettings.ConnectNewDevice = device);

    public static DeviceAction LaunchWsa(WsaPkgDeviceViewModel device) => new(
        () => device.Status is DeviceStatus.Ok,
        async () =>
        {
            if (Data.Settings.ShowLaunchWsaMessage)
            {
                var result = await DialogService.ShowConfirmation(Strings.Resources.S_WSA_LAUNCH,
                                                                  Strings.Resources.S_WSA_DIALOG_TITLE,
                                                                  primaryText: Strings.Resources.S_BUTTON_LAUNCH,
                                                                  checkBoxText: Strings.Resources.S_DONT_SHOW_AGAIN,
                                                                  icon: DialogService.DialogIcon.Exclamation,
                                                                  censorContent: false);

                Data.Settings.ShowLaunchWsaMessage = !result.Item2;

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

    internal static int CountVisibleDevices(IEnumerable<DeviceViewModel> devices)
    {
        var deviceList = devices.ToList();
        var predicate = CreateDevicePredicate(deviceList);
        return deviceList.Count(device => predicate(device)
            && device is not HistoryDeviceViewModel and not NewDeviceViewModel);
    }

    public static void FilterDevices(ICollectionView collectionView)
    {
        if (collectionView is null)
            return;

        if (collectionView.Filter is null)
        {
            collectionView.SortDescriptions.Clear();
            collectionView.SortDescriptions.Add(new SortDescription(nameof(DeviceViewModel.Type), ListSortDirection.Ascending));
        }

        var predicate = CreateDevicePredicate(Data.DevicesObject.UIList);
        collectionView.Filter = item => item is DeviceViewModel device && predicate(device);
    }

    public static void UpdateDevicesBatInfo()
    {
        LogicalDeviceViewModel currentDevice = null;
        List<LogicalDeviceViewModel> items = [];
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            void captureDevices()
            {
                currentDevice = Data.DevicesObject.Current;
                items = [.. Data.DevicesObject.LogicalDeviceViewModels
                    .Where(device => !device.IsOpen && device.Status is DeviceStatus.Ok)];
            }

            if (dispatcher.CheckAccess())
                captureDevices();
            else
                dispatcher.Invoke(captureDevices);
        }

        if (currentDevice?.Status is DeviceStatus.Ok)
            currentDevice.UpdateBattery();

        if (DateTime.Now - Data.DevicesObject.LastUpdate <= AdbExplorerConst.BATTERY_UPDATE_INTERVAL && !Data.RuntimeSettings.IsDevicesPaneOpen)
            return;

        foreach (var item in items)
        {
            item.UpdateBattery();
        }

        Data.DevicesObject.LastUpdate = DateTime.Now;
    }

    public static async void ListServices(IEnumerable<ServiceDevice> services)
    {
        if (services is null)
            return;

        var serviceList = services.ToList();

        if (!Data.DevicesObject.ServicesChanged(serviceList))
            return;

        var viewModels = serviceList.Select(service => ServiceDeviceViewModel.New(service, false)).ToList();
        Data.DevicesObject.UpdateServices(viewModels);

        var qrServices = Data.DevicesObject.ServiceDeviceViewModels.Where(service =>
            service.MdnsType == ServiceDevice.ServiceType.QrCode
            && service.ID == Data.QrClass.ServiceName);

        if (qrServices.Any())
        {
            await PairService(qrServices.First());
        }
    }

    public static async Task<bool> PairService(ServiceDeviceViewModel service)
    {
        var code = service.MdnsType == ServiceDevice.ServiceType.QrCode
            ? Data.QrClass.Password
            : service.PairingCode;

        return await Task.Run(() =>
        {
            try
            {
                ADBService.PairNetworkDevice(service.ID, code);
            }
            catch (Exception ex)
            {
                App.Current.Dispatcher.Invoke(() => DialogService.ShowMessage(ex.Message, Strings.Resources.S_PAIR_ERR_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true));
                return false;
            }

            return true;
        });
    }

    public static void CollapseDevices()
    {
        // To make sure value changes to true
        Data.RuntimeSettings.CollapseDevices = false;
        Data.RuntimeSettings.CollapseDevices = true;

        Data.RuntimeSettings.IsPathBoxFocused = false;
    }

    public static void UpdateDevicesRootAccess()
    {
        if (DateTime.Now - lastRootStatusUpdate < RootStatusUpdateInterval
            || Interlocked.Exchange(ref isRootStatusUpdateRunning, 1) == 1)
            return;

        var devices = Data.DevicesObject.LogicalDeviceViewModels.Where(d => d.Root is RootStatus.Unchecked).ToList();
        Task.Run(() =>
        {
            try
            {
                foreach (var device in devices.Where(d => d.Status is DeviceStatus.Ok))
                {
                    bool? rootState = ADBService.TryGetRootState(device.ID);
                    if (rootState is null)
                        continue;

                    _ = App.Current?.Dispatcher?.BeginInvoke(new Action(() => device.SetRootStatus(rootState.Value
                        ? RootStatus.Enabled
                        : RootStatus.Disabled)));
                }

                lastRootStatusUpdate = DateTime.Now;
            }
            finally
            {
                Interlocked.Exchange(ref isRootStatusUpdateRunning, 0);
            }
        });
    }

    public static async void PairNewDevice()
    {
        var dev = (NewDeviceViewModel)Data.RuntimeSettings.ConnectNewDevice;
        await Task.Run(() =>
        {
            try
            {
                ADBService.PairNetworkDevice(dev.PairingAddress, dev.PairingCode);
                return true;
            }
            catch (Exception ex)
            {
                App.Current.Dispatcher.Invoke(() => DialogService.ShowMessage(ex.Message, Strings.Resources.S_PAIR_ERR_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true));
                return false;
            }
        }).ContinueWith(t =>
        {
            if (t.IsCanceled)
                return;

            App.Current.Dispatcher.Invoke(() =>
            {
                if (t.Result)
                    ConnectNewDevice();

                Data.RuntimeSettings.ConnectNewDevice = null;
                Data.RuntimeSettings.IsManualPairingInProgress = false;
            });
        });
    }

    public static async void ConnectNewDevice()
    {
        var dev = (NewDeviceViewModel)Data.RuntimeSettings.ConnectNewDevice;
        await Task.Run(() =>
        {
            try
            {
                ADBService.ConnectNetworkDevice(dev.ConnectAddress);
                return true;
            }
            catch (Exception ex)
            {
                if (AdbExplorerConst.LOOPBACK_ADDRESSES.Contains(dev.IpAddress))
                    return true;

                if (ex.Message.Contains(Strings.Resources.S_FAILED_CONN + dev.ConnectAddress)
                    && !((NewDeviceViewModel)Data.RuntimeSettings.ConnectNewDevice).IsPairingEnabled)
                {
                    Data.DevicesObject.CurrentNewDevice.EnablePairing();
                }
                else
                    App.Current.Dispatcher.Invoke(() => DialogService.ShowMessage(ex.Message, Strings.Resources.S_FAILED_CONN_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true));

                return false;
            }
        }).ContinueWith(t =>
        {
            if (t.IsCanceled)
                return;

            App.Current.Dispatcher.Invoke(() =>
            {
                if (t.Result)
                {
                    string newDeviceAddress = "";
                    var newDevice = Data.RuntimeSettings.ConnectNewDevice is null ? Data.DevicesObject.CurrentNewDevice : Data.RuntimeSettings.ConnectNewDevice;

                    if (newDevice.Type is DeviceType.New && !AdbExplorerConst.LOOPBACK_ADDRESSES.Contains(newDevice.IpAddress))
                    {
                        if (Data.Settings.SaveDevices)
                            Data.DevicesObject.AddHistoryDevice(HistoryDeviceViewModel.New(dev));

                        newDeviceAddress = dev.ConnectAddress;
                        ((NewDeviceViewModel)newDevice).ClearDevice();
                    }
                    else if (newDevice.Type is DeviceType.History)
                    {
                        newDeviceAddress = ((HistoryDeviceViewModel)newDevice).ConnectAddress;

                        // In case user has changed the port of the history device
                        if (Data.Settings.SaveDevices)
                            Data.DevicesObject.StoreHistoryDevices();
                    }

                    CollapseDevices();
                    DeviceListSetup(newDeviceAddress);
                }

                Data.RuntimeSettings.ConnectNewDevice = null;
                Data.RuntimeSettings.IsManualPairingInProgress = false;
            });
        });
    }

    public static IEnumerable<LogicalDeviceViewModel> ReconnectFileOpDevice(IEnumerable<LogicalDeviceViewModel> devices)
    {
        var connectedDevices = devices.ToList();
        var currentUiDevices = Data.DevicesObject.UIList.ToHashSet();
        var pastDevices = Data.FileOpQ.Operations
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

    public static void DeviceListSetup(string selectedAddress = "")
    {
        Task.Run(ADBService.GetDevices).ContinueWith((t) =>
        {
            if (!t.IsCompletedSuccessfully || App.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
                return;

            _ = dispatcher.BeginInvoke(new Action(() => DeviceListSetup(
                t.Result.Select(l => new LogicalDeviceViewModel(l, false)).ToList(), selectedAddress)));
        });
    }

    public static void DeviceListSetup(IEnumerable<LogicalDeviceViewModel> devices, string selectedAddress = "")
    {
        int openRequestVersion = Interlocked.Increment(ref deviceOpenRequestVersion);
        var deviceList = ReconnectFileOpDevice(devices).ToList();
        Data.DevicesObject.UpdateDevices(deviceList);
        Data.RuntimeSettings.FilterDevices = true;

        var currentDevice = Data.DevicesObject.Current;
        if (currentDevice is null
            || currentDevice.Status is not DeviceStatus.Ok
            || !Data.DevicesObject.LogicalDeviceViewModels.Contains(currentDevice))
        {
            Data.DirList?.Stop();
            DriveHelper.ClearDrives();
            Data.DevicesObject.SetOpenDevice((LogicalDeviceViewModel)null);
            Data.CurrentADBDevice = null;
            Data.RuntimeSettings.CurrentDevice = null;
            Data.DirList = null;
        }

        if (Data.DevicesObject.DevicesAvailable(true))
            return;

        CollapseDevices();

        Data.DirList?.Stop();
        Data.DevicesObject.SetOpenDevice((LogicalDeviceViewModel)null);
        Data.CurrentADBDevice = null;
        Data.RuntimeSettings.CurrentDevice = null;
        Data.DirList = null;

        FileActionLogic.ClearExplorer();
        Data.FileActions.IsExplorerVisible = false;

        NavHistory.Reset();
        DriveHelper.ClearDrives();

        if (string.IsNullOrEmpty(selectedAddress) && !Data.Settings.AutoOpen)
            return;

        var availableDevices = Data.DevicesObject.LogicalDeviceViewModels.ToList();
        if (availableDevices.Count == 0)
            return;

        LogicalDeviceViewModel device;
        if (!string.IsNullOrEmpty(selectedAddress))
        {
            device = availableDevices.FirstOrDefault(d => d.ID == selectedAddress && d.Status is DeviceStatus.Ok);
        }
        else
        {
            device = availableDevices.FirstOrDefault(d => d.ID == Data.Settings.LastDeviceId)
                ?? availableDevices.FirstOrDefault(d => d.Name == Data.Settings.LastDevice)
                ?? availableDevices.FirstOrDefault();
        }

        _ = Task.Run(async () =>
        {
            if (device is null)
                return false;

            var startTime = DateTime.Now;
            while (device.Status is not DeviceStatus.Ok)
            {
                if (openRequestVersion != Volatile.Read(ref deviceOpenRequestVersion)
                    || DateTime.Now - startTime > TimeSpan.FromSeconds(6))
                {
                    return false;
                }

                await Task.Delay(500);
            }
            return true;
        }).ContinueWith(t =>
        {
            _ = App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!t.IsCompletedSuccessfully || !t.Result
                    || openRequestVersion != Volatile.Read(ref deviceOpenRequestVersion)
                    || device.Status is not DeviceStatus.Ok)
                {
                    return;
                }

                OpenDevice(device);
            }));
        });
    }

    private static async Task InitDevice()
    {
        var currentDevice = Data.DevicesObject.Current;
        var currentAdbDevice = Data.CurrentADBDevice;
        if (currentDevice is null || currentAdbDevice is null)
            return;

        string reopenPath = GetAutoOpenPath(currentDevice);
        if (string.IsNullOrEmpty(reopenPath))
            Data.RuntimeSettings.DriveViewNav = true;
        else
            Data.FileActions.IsDriveViewVisible = false;

        Data.RuntimeSettings.FilterDrives = true;
        Data.FileActions.PushPackageEnabled = Data.Settings.EnableApk && currentDevice.Type is not DeviceType.Recovery;

        Data.FileOpQ.MoveOperationsToPast();
        FileActionLogic.UpdateFileActions();

        try
        {
            string version = await currentAdbDevice.GetAndroidVersion();
            if (ReferenceEquals(Data.CurrentADBDevice, currentAdbDevice)
                && ReferenceEquals(Data.DevicesObject.Current, currentDevice))
            {
                currentDevice.SetAndroidVersion(version);
            }
        }
        catch (Exception e)
        {
            Data.AddCommandLog($"@ADB Explorer: failed to read Android version: {e.Message}");
        }

        if (!ReferenceEquals(Data.CurrentADBDevice, currentAdbDevice)
            || !ReferenceEquals(Data.DevicesObject.Current, currentDevice))
        {
            return;
        }

        await FileActionLogic.RefreshDrives(true, string.IsNullOrEmpty(reopenPath));

        if (string.IsNullOrEmpty(reopenPath)
            || !ReferenceEquals(Data.CurrentADBDevice, currentAdbDevice)
            || !ReferenceEquals(Data.DevicesObject.Current, currentDevice))
        {
            return;
        }

        Data.RuntimeSettings.LocationToNavigate = new(reopenPath);
    }

    public static void TestDevices()
    {
        //ConnectTimer.IsEnabled = false;

        //DevicesObject.UpdateServices(new List<ServiceDevice>() { new PairingService("sdfsdfdsf_adb-tls-pairing._tcp.", "192.168.1.20", "5555") { MdnsType = ServiceDevice.ServiceType.PairingCode } });
        //DevicesObject.UpdateDevices(new List<LogicalDevice>() { LogicalDevice.New("Test", "test.ID", "device") });
    }

    public static void ConnectDevice(DeviceViewModel device)
    {
        Data.RuntimeSettings.IsManualPairingInProgress = true;
        Data.DevicesObject.CurrentNewDevice = (NewDeviceViewModel)device;

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

                Data.DevicesObject.UIList.Remove(device);
                logical.DetachRuntimeSettings();
                Data.RuntimeSettings.FilterDevices = true;
                DeviceListSetup();
                break;
            case HistoryDeviceViewModel hist:
                Data.DevicesObject.RemoveHistoryDevice(hist);
                break;
            default:
                throw new NotSupportedException();
        }
    }

    public static void OpenDevice(LogicalDeviceViewModel device)
    {
        if (device is null || device.Status is not DeviceStatus.Ok)
            return;

        Interlocked.Increment(ref deviceOpenRequestVersion);
        FileActionLogic.ClearExplorer();
        NavHistory.Reset();
        Data.CurrentADBDevice = new(device);
        Data.DevicesObject.SetOpenDevice(device);
        Data.RuntimeSettings.InitLister = true;
        _ = InitDevice();

        Data.RuntimeSettings.IsDevicesPaneOpen = false;
    }

    private static string GetAutoOpenPath(LogicalDeviceViewModel device)
    {
        if (!Data.Settings.AutoOpen || device is null)
            return null;

        string path = Data.Settings.GetLastDevicePath(device.ID);
        if (!string.IsNullOrWhiteSpace(path))
            return path;

        return string.Equals(Data.Settings.LastDevice, device.Name, StringComparison.Ordinal)
            ? Data.Settings.LastDevicePath
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
            if (Data.DevicesObject.UIList.OfType<WsaPkgDeviceViewModel>().Any(wsa => wsa.Status is not DeviceStatus.Unauthorized))
                return;

            if (Data.DevicesObject.LogicalDeviceViewModels.Any(dev => dev.Type is DeviceType.WSA && dev.Status is not DeviceStatus.Offline))
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
            Data.DevicesObject.WsaPort = match.Groups["Port"].Value;
            if (!AdbExplorerConst.LOOPBACK_ADDRESSES.Contains(netstatIp))
                return;

            Data.DevicesObject.CurrentNewDevice = new(new())
            {
                IpAddress = AdbExplorerConst.WIN_LOOPBACK_ADDRESS,
                ConnectPort = Data.DevicesObject.WsaPort,
            };
            Data.DevicesObject.CurrentNewDevice.ConnectCommand.Execute();
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

        var wsa = Data.DevicesObject.UIList.OfType<WsaPkgDeviceViewModel>().FirstOrDefault();
        if (wsa is null)
        {
            Interlocked.Exchange(ref isWsaStatusUpdateRunning, 0);
            return;
        }

        if (Data.DevicesObject.LogicalDeviceViewModels.Any(dev => dev.Type is DeviceType.WSA && dev.Status is not DeviceStatus.Offline))
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
        Task.Run(() =>
        {
            try
            {
                DeviceStatus newStatus;
                bool resetLaunchTime = false;

                if (oldStatus is DeviceStatus.Unauthorized && DateTime.Now - wsa.LastLaunch > AdbExplorerConst.WSA_CONNECT_TIMEOUT)
                {
                    if (wsa.LastLaunch == DateTime.MinValue)
                    {
                        _ = App.Current?.Dispatcher?.BeginInvoke(new Action(() => wsa.SetLastLaunch()));
                        return;
                    }

                    newStatus = DeviceStatus.Ok;
                    resetLaunchTime = true;
                }
                else
                {
                    if (GetWsaPid() is not null)
                        newStatus = DeviceStatus.Unauthorized;
                    else if (IsWsaInstalled())
                        newStatus = DeviceStatus.Ok;
                    else
                        newStatus = DeviceStatus.Offline;
                }

                lastWsaStatusUpdate = DateTime.Now;

                if (newStatus != oldStatus || resetLaunchTime)
                {
                    _ = App.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        if (resetLaunchTime)
                            wsa.SetLastLaunch(DateTime.MaxValue);

                        if (newStatus != oldStatus)
                        {
                            wsa.SetStatus(newStatus);
                            Data.RuntimeSettings.FilterDevices = true;
                        }
                    }));
                }
            }
            finally
            {
                Interlocked.Exchange(ref isWsaStatusUpdateRunning, 0);
            }
        });
    }
}
