using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Resources;
using ADB_Explorer.Services;
using AdvancedSharpAdbClient.Models;

namespace ADB_Explorer.ViewModels;

public class Devices : AbstractDevice
{
    private int isLogicalIpUpdateRunning;

    #region Full properties

    private ObservableList<DeviceViewModel> uiDevices = new();
    public ObservableList<DeviceViewModel> UIList
    {
        get => uiDevices;
        private set => Set(ref uiDevices, value);
    }

    public DateTime LastUpdate { get; set; }

    public List<string> RootDevices { get; protected set; } = new();

    public NewDeviceViewModel CurrentNewDevice { get; set; }

    public string WsaPort { get; set; }

    #endregion

    #region Read only lists

    public IEnumerable<LogicalDeviceViewModel> LogicalDeviceViewModels => UIList?.OfType<LogicalDeviceViewModel>();
    public IEnumerable<ServiceDeviceViewModel> ServiceDeviceViewModels => UIList?.OfType<ServiceDeviceViewModel>();
    public IEnumerable<HistoryDeviceViewModel> HistoryDeviceViewModels => UIList?.OfType<HistoryDeviceViewModel>();

    #endregion

    #region Read only properties

    public LogicalDeviceViewModel Current => LogicalDeviceViewModels?.FirstOrDefault(device => device.IsOpen)
        ?? Data.RuntimeSettings.DeviceToOpen;

    public int Count => DeviceHelper.CountVisibleDevices(UIList);

    public ObservableProperty<string> ObservableCount = new();

    #endregion

    public Devices()
    {
        UIList.Add(new NewDeviceViewModel(new()));
        UIList.Add(new WsaPkgDeviceViewModel(new()));

        if (Data.Settings.SaveDevices)
            RetrieveHistoryDevices();

        UIList.CollectionChanged += UIList_CollectionChanged;
        PropertyChanged += Devices_PropertyChanged;

        ObservableCount.Value = "0";
    }

    private void Devices_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Count))
            ObservableCount.Value = Count.ToString();
    }

    private void UIList_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Count));
    }

    #region History device handling

    public void RetrieveHistoryDevices() => RetrieveHistoryDevices(UIList);

    public static void RetrieveHistoryDevices(ObservableList<DeviceViewModel> uiList)
    {
        var value = Storage.RetrieveValue("SavedDevices");
        if (value is null)
            return;

        var jArray = value.ToString();
        bool legacy = jArray.Contains(typeof(HistoryDevice).FullName);
        var historyType = legacy ? typeof(List<HistoryDevice>) : typeof(List<StorageDevice>);

        var devices = JsonConvert.DeserializeObject(jArray, historyType);
        if (devices is null)
            return;

        var items = legacy ? ((List<HistoryDevice>)devices).Select(s => new HistoryDeviceViewModel(s)) : ((List<StorageDevice>)devices).Select(HistoryDeviceViewModel.New);
        uiList.AddRange(items);
    }

    public void StoreHistoryDevices() => StoreHistoryDevices(UIList.OfType<HistoryDeviceViewModel>());

    public static void StoreHistoryDevices(IEnumerable<HistoryDeviceViewModel> devices)
    {
        Storage.StoreValue("SavedDevices", devices.Select(h => h.GetStorage()));
    }

    public void AddHistoryDevice(HistoryDeviceViewModel device)
    {
        UIList.Add(device);
        StoreHistoryDevices();
    }

    public void RemoveHistoryDevice(HistoryDeviceViewModel device)
    {
        UIList.Remove(device);
        device.DetachBaseRuntimeSettings();
        StoreHistoryDevices();
    }

    #endregion

    #region Service device handling

    public void UpdateServices(IEnumerable<ServiceDeviceViewModel> other) => UpdateServices(UIList, other);

    public static void UpdateServices(ObservableList<DeviceViewModel> self, IEnumerable<ServiceDeviceViewModel> other)
    {
        var incomingDevices = other.ToList();
        var incomingIds = incomingDevices.Select(device => device.ID).ToHashSet(StringComparer.Ordinal);
        var devicesToRemove = self.OfType<ServiceDeviceViewModel>()
            .Where(device => !incomingIds.Contains(device.ID))
            .ToList();
        self.RemoveAll(devicesToRemove);
        devicesToRemove.ForEach(device => device.DetachBaseRuntimeSettings());

        Dictionary<string, ServiceDeviceViewModel> existingById = new(StringComparer.Ordinal);
        foreach (var device in self.OfType<ServiceDeviceViewModel>())
            existingById.TryAdd(device.ID, device);

        List<ServiceDeviceViewModel> devicesToAdd = [];
        foreach (var item in incomingDevices)
        {
            if (existingById.TryGetValue(item.ID, out var service))
            {
                service.UpdateService(item);
            }
            else
            {
                devicesToAdd.Add(item);
            }
        }

        devicesToAdd.ForEach(device => device.AttachBaseRuntimeSettings());
        self.AddRange(devicesToAdd);
    }

    public bool ServicesChanged(IEnumerable<ServiceDevice> other)
    {
        // if the list is null, we're probably not ready to update
        if (other is null)
            return false;

        var incomingServices = other.ToList();

        // if the list is empty, we need to update (and remove all items)
        if (incomingServices.Count == 0)
            return ServiceDeviceViewModels.Any();

        var pairing = incomingServices.OfType<PairingService>().OrderBy(service => service.ID).ToList();
        var logicalDevices = LogicalDeviceViewModels.ToList();
        var onlineBaseIds = logicalDevices
            .Where(device => device.Status == DeviceStatus.Ok)
            .Select(device => device.BaseID)
            .ToHashSet(StringComparer.Ordinal);
        var logicalIpAddresses = logicalDevices
            .Select(device => device.IpAddress)
            .ToHashSet(StringComparer.Ordinal);
        // if there's any service whose ID is not found in any logical device,
        // AND an ordering of both new and old lists doesn't match up all IDs
        if (!pairing.Any(service => !onlineBaseIds.Contains(service.ID)
                && !logicalIpAddresses.Contains(service.IpAddress)))
        {
            return false;
        }

        var currentServices = ServiceDeviceViewModels.OrderBy(service => service.ID).ToList();
        if (currentServices.Count != pairing.Count)
            return true;

        for (int i = 0; i < currentServices.Count; i++)
        {
            if (currentServices[i].ID != pairing[i].ID
                || string.IsNullOrEmpty(currentServices[i].PairingPort) != string.IsNullOrEmpty(pairing[i].PairingPort))
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Logical device handling

    public bool UpdateDevices(IEnumerable<LogicalDeviceViewModel> other)
    {
        var result = UpdateDevices(UIList, other);
        OnPropertyChanged(nameof(Count));

        UpdateLogicalIp();
        UpdateHistoryNames();

        return result;
    }

    private static bool UpdateDevices(ObservableList<DeviceViewModel> self, IEnumerable<LogicalDeviceViewModel> other)
    {
        bool isCurrentTypeUpdated = false;
        var incomingDevices = other.ToList();
        var incomingIds = incomingDevices.Select(device => device.ID).ToHashSet(StringComparer.Ordinal);

        // First remove all devices that no longer exist
        var devicesToRemove = self.OfType<LogicalDeviceViewModel>()
            .Where(device => !incomingIds.Contains(device.ID))
            .ToList();
        foreach (var item in devicesToRemove)
        {
            // Set status as offline for file op mechanism
            item.SetStatus(DeviceStatus.Offline);
        }
        self.RemoveAll(devicesToRemove);
        devicesToRemove.ForEach(item => item.DetachRuntimeSettings());

        // Then update existing devices' statuses and names
        Dictionary<string, LogicalDeviceViewModel> existingById = new(StringComparer.Ordinal);
        foreach (var device in self.OfType<LogicalDeviceViewModel>())
            existingById.TryAdd(device.ID, device);

        List<LogicalDeviceViewModel> devicesToAdd = [];
        foreach (var item in incomingDevices)
        {
            if (existingById.TryGetValue(item.ID, out var device))
            {
                // Return (at the end of the function) true if current device status has changed
                if (device is not null && device.IsOpen && device.Status != item.Status)
                    isCurrentTypeUpdated = true;

                device.UpdateDevice(item);
                device.InitializeDrives();
            }
            else
            {
                devicesToAdd.Add(item);
            }
        }

        if (devicesToAdd.Count > 0)
        {
            devicesToAdd.ForEach(item => item.InitializeDrives());
            devicesToAdd.ForEach(item => item.AttachRuntimeSettings());
            self.AddRange(devicesToAdd);

            foreach (var item in devicesToAdd.Where(item => item.Status is DeviceStatus.Ok))
            {
                Task.Run(() => ShellCommands.FindCommands(item.ID));
            }
        }

        return isCurrentTypeUpdated;
    }

    public bool DevicesChanged(IEnumerable<LogicalDevice> other)
    {
        if (other is null)
            return false;

        var currentDevices = LogicalDeviceViewModels.OrderBy(device => device.ID).ToList();
        var incomingDevices = other.OrderBy(device => device.ID).ToList();
        if (currentDevices.Count != incomingDevices.Count)
            return true;

        for (int i = 0; i < currentDevices.Count; i++)
        {
            if (currentDevices[i].ID != incomingDevices[i].ID
                || currentDevices[i].Status != incomingDevices[i].Status
                || !DeviceDataEquals(currentDevices[i].DeviceData, incomingDevices[i].DeviceData))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool DeviceDataEquals(DeviceData current, DeviceData incoming)
    {
        if (ReferenceEquals(current, incoming))
            return true;

        if (current is null || incoming is null)
            return false;

        return current.Serial == incoming.Serial
            && current.State == incoming.State
            && current.Model == incoming.Model
            && current.Product == incoming.Product
            && current.Name == incoming.Name
            && (current.Features ?? []).SequenceEqual(incoming.Features ?? [], StringComparer.Ordinal)
            && current.Usb == incoming.Usb
            && current.TransportId == incoming.TransportId
            && current.Message == incoming.Message;
    }

    #endregion

    #region General device handling

    public void UpdateDeviceRoot(string deviceID, bool isRootStateKnown)
    {
        if (isRootStateKnown)
        {
            if (!RootDevices.Contains(deviceID))
                RootDevices.Add(deviceID);
        }
        else
        {
            RootDevices.Remove(deviceID);
        }
    }

    public void UpdateHistoryNames()
    {
        if (UpdateHistoryNames(UIList))
            OnPropertyChanged(nameof(UIList));
    }

    public static bool UpdateHistoryNames(ObservableList<DeviceViewModel> devices)
    {
        var result = false;
        var logicalByIp = devices.OfType<LogicalDeviceViewModel>()
            .Where(device => device.Type is DeviceType.Remote or DeviceType.Service)
            .ToLookup(device => device.IpAddress, StringComparer.Ordinal);

        foreach (var item in devices.OfType<HistoryDeviceViewModel>().Where(d => string.IsNullOrEmpty(d.DeviceName)))
        {
            var logical = logicalByIp[item.IpAddress].FirstOrDefault();
            if (logical is not null)
            {
                item.SetDeviceName(logical.Name);
                result = true;
            }
        }

        if (result)
            StoreHistoryDevices(devices.OfType<HistoryDeviceViewModel>());

        return result;
    }

    public async void UpdateLogicalIp()
    {
        if (Interlocked.Exchange(ref isLogicalIpUpdateRunning, 1) == 1)
            return;

        try
        {
            if (await UpdateLogicalIp(UIList))
                OnPropertyChanged(nameof(UIList));
        }
        finally
        {
            Interlocked.Exchange(ref isLogicalIpUpdateRunning, 0);
        }
    }

    public static async Task<bool> UpdateLogicalIp(ObservableList<DeviceViewModel> devices)
    {
        var result = false;
        var items = devices.OfType<LogicalDeviceViewModel>().Where(d => d.Type is DeviceType.Service or DeviceType.Local && !d.IsIpAddressValid).ToList();
        var servicesById = devices.OfType<ServiceDeviceViewModel>()
            .Where(service => service.IsIpAddressValid && !string.IsNullOrEmpty(service.ID))
            .GroupBy(service => service.ID, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (item.Type is DeviceType.Service)
            {
                if (servicesById.TryGetValue(item.ID, out var service)
                    || !string.IsNullOrEmpty(item.BaseID) && servicesById.TryGetValue(item.BaseID, out service))
                {
                    item.SetIpAddress(service.IpAddress);
                    result = true;
                    continue;
                }
            }

            await Task.Run(() => result |= ADBService.AdbDevice.GetDeviceIp(item));
        }

        return result;
    }

    public bool DevicesAvailable(bool current = false) => AvailableDevices(current).Any();

    private IEnumerable<LogicalDeviceViewModel> AvailableDevices(bool current = false)
    {
        return LogicalDeviceViewModels.Where(
                device => (!current || device.IsOpen)
                && device.Status is DeviceStatus.Ok);
    }

    public bool SetOpenDevice(LogicalDeviceViewModel device)
    {
        if (Data.RuntimeSettings.DeviceToOpen is null
            && Data.RuntimeSettings.CurrentDevice is null
            && device is null)
            return false;

        if (Data.RuntimeSettings.DeviceToOpen?.Equals(device) is not true)
            Data.RuntimeSettings.DeviceToOpen = device;

        if (Data.RuntimeSettings.CurrentDevice?.Equals(device) is not true)
            Data.RuntimeSettings.CurrentDevice = device;

        Data.RuntimeSettings.IsRootActive = device?.Root is RootStatus.Enabled;

        if (device is not null)
        {
            Data.Settings.LastDevice = device.Name;
            Data.Settings.LastDeviceId = device.ID;
            return true;
        }

        return false;
    }

    #endregion
}
