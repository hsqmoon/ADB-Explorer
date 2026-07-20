using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Resources;
using ADB_Explorer.Services;
using AdvancedSharpAdbClient.Models;

namespace ADB_Explorer.ViewModels;

public class Devices : AbstractDevice
{
    private readonly Dictionary<string, long> appliedDeviceSequences = new(StringComparer.Ordinal);

    #region Full properties

    public ObservableList<DeviceViewModel> UIList { get; } = new();

    public ObservableList<DeviceViewModel> VisibleDevices { get; } = new();

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
        ?? App.RuntimeSettings.DeviceToOpen;

    public int Count => VisibleDevices.Count(device =>
        device is not HistoryDeviceViewModel and not NewDeviceViewModel);

    public ObservableProperty<string> ObservableCount = new();

    #endregion

    public Devices()
    {
        UIList.Add(new NewDeviceViewModel(new()));
        UIList.Add(new WsaPkgDeviceViewModel(new()));

        VisibleDevices.CollectionChanged += VisibleDevices_CollectionChanged;
        PropertyChanged += Devices_PropertyChanged;

        ObservableCount.Value = "0";
    }

    private void Devices_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Count))
            ObservableCount.Value = Count.ToString();
    }

    private void VisibleDevices_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Count));
    }

    #region History device handling

    internal static HistoryDeviceViewModel[] LoadHistoryDevices()
    {
        var value = Storage.RetrieveValue("SavedDevices");
        if (value is null)
            return [];

        var jArray = value.ToString();
        bool legacy = jArray.Contains(typeof(HistoryDevice).FullName);
        var historyType = legacy ? typeof(List<HistoryDevice>) : typeof(List<StorageDevice>);

        var devices = JsonConvert.DeserializeObject(jArray, historyType);
        if (devices is null)
            return [];

        return legacy
            ? ((List<HistoryDevice>)devices).Select(device => new HistoryDeviceViewModel(device)).ToArray()
            : ((List<StorageDevice>)devices).Select(HistoryDeviceViewModel.New).ToArray();
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
        foreach (var item in devicesToRemove)
            self.Remove(item);
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
        foreach (var item in devicesToAdd)
            self.Add(item);
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

    internal bool ApplySnapshot(DeviceDelta delta, IEnumerable<LogicalDeviceViewModel> addedDevices)
    {
        bool isCurrentTypeUpdated = false;
        var existingById = LogicalDeviceViewModels.ToDictionary(device => device.ID, StringComparer.Ordinal);
        var incomingById = delta.Devices.ToDictionary(device => device.ID, StringComparer.Ordinal);
        var devicesToRemove = existingById
            .Where(item => !incomingById.ContainsKey(item.Key))
            .Select(item => item.Value)
            .ToArray();
        foreach (var item in devicesToRemove)
            item.SetStatus(DeviceStatus.Offline);
        foreach (var item in devicesToRemove)
            UIList.Remove(item);
        devicesToRemove.ForEach(item => item.DetachRuntimeSettings());
        foreach (var item in devicesToRemove)
            appliedDeviceSequences.Remove(item.ID);

        foreach (var item in delta.Devices)
        {
            if (existingById.TryGetValue(item.ID, out var device))
            {
                if (delta.DeviceSequences.TryGetValue(item.ID, out long sequence)
                    && appliedDeviceSequences.GetValueOrDefault(item.ID) == sequence)
                {
                    continue;
                }

                if (device.IsOpen && device.Status != item.Status)
                    isCurrentTypeUpdated = true;

                device.UpdateDevice(item);
                if (delta.DeviceSequences.TryGetValue(item.ID, out sequence))
                    appliedDeviceSequences[item.ID] = sequence;
            }
        }

        foreach (var item in addedDevices)
        {
            item.AttachRuntimeSettings();
            UIList.Add(item);
            if (delta.DeviceSequences.TryGetValue(item.ID, out long sequence))
                appliedDeviceSequences[item.ID] = sequence;
        }

        UpdateHistoryNames();
        return isCurrentTypeUpdated;
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
        UpdateHistoryNames(UIList);
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

    public bool DevicesAvailable(bool current = false) => AvailableDevices(current).Any();

    private IEnumerable<LogicalDeviceViewModel> AvailableDevices(bool current = false)
    {
        return LogicalDeviceViewModels.Where(
                device => (!current || device.IsOpen)
                && device.Status is DeviceStatus.Ok);
    }

    public bool SetOpenDevice(LogicalDeviceViewModel device)
    {
        if (App.RuntimeSettings.DeviceToOpen is null
            && App.RuntimeSettings.CurrentDevice is null
            && device is null)
            return false;

        if (App.RuntimeSettings.DeviceToOpen?.Equals(device) is not true)
            App.RuntimeSettings.DeviceToOpen = device;

        if (App.RuntimeSettings.CurrentDevice?.Equals(device) is not true)
            App.RuntimeSettings.CurrentDevice = device;

        App.RuntimeSettings.IsRootActive = device?.Root is RootStatus.Enabled;

        if (device is not null)
        {
            App.Settings.LastDevice = device.Name;
            App.Settings.LastDeviceId = device.ID;
            return true;
        }

        return false;
    }

    #endregion
}
