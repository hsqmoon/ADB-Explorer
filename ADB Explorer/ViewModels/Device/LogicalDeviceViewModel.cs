using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services;
using AdvancedSharpAdbClient.Models;

namespace ADB_Explorer.ViewModels;

public class LogicalDeviceViewModel : DeviceViewModel
{
    private bool runtimeSettingsSubscribed;

    #region Full properties

    private LogicalDevice device;
    protected new LogicalDevice Device
    {
        get => device;
        set => Set(ref device, value);
    }

    private bool isOpen;
    /// <summary>
    /// Device is open for browsing
    /// </summary>
    public bool IsOpen
    {
        get => isOpen;
        private set
        {
            if (Set(ref isOpen, value) && Root is RootStatus.Enabled)
            {
                if (!value && Status is DeviceStatus.Ok && Data.Settings.UnrootOnDisconnect is true)
                {
                    var currentDevice = Device;
                    _ = Task.Run(() => ADBService.Unroot(currentDevice));
                }

                if (value)
                    Data.RuntimeSettings.IsRootActive = true;
            }
        }
    }

    public DeviceData DeviceData => Device.DeviceData;

    private byte? androidVersion;
    public byte? AndroidVersion
    {
        get => androidVersion;
        private set => Set(ref androidVersion, value);
    }

    private bool useIdForName;
    public bool UseIdForName
    {
        get => useIdForName;
        set => Set(ref useIdForName, value);
    }

    #endregion

    #region Read only properties

    public string Name
    {
        get
        {
            // to prevent displaying [Service] for offline connect services acquired via adb devices -l upon first contact
            if (string.IsNullOrEmpty(Device.Name) && DiscoverTime - DateTime.Now < AdbExplorerConst.SERVICE_DISPLAY_DELAY)
                return " ";

            return UseIdForName ? Device.ID : Device.Name;
        }
    }

    public string BaseID => Type is DeviceType.Service ? ID.Split('.')[0] : ID;

    public string LogicalID => Type is DeviceType.Service && ID.Count(c => c is '-') > 1 ? ID.Split('-')[1] : ID;

    public RootStatus Root => Device.Root;

    public string RootString => Root switch
    {
        RootStatus.Unchecked => Strings.Resources.S_STAT_ROOT_UNCHECKED,
        RootStatus.Forbidden => Strings.Resources.S_STAT_ROOT_FORBIDDEN,
        RootStatus.Disabled => Strings.Resources.S_DISABLED,
        RootStatus.Enabled => Strings.Resources.S_ENABLED,
        _ => throw new NotSupportedException(),
    };

    public bool AndroidVersionIncompatible => AndroidVersion is not null && AndroidVersion < AdbExplorerConst.MIN_SUPPORTED_ANDROID_VER;

    public override string Tooltip
    {
        get
        {
            var type = Device.Type switch
            {
                DeviceType.Local => Strings.Resources.S_TYPE_USB,
                DeviceType.Remote => Strings.Resources.S_TYPE_WIFI,
                DeviceType.Emulator => Strings.Resources.S_TYPE_EMULATOR,
                DeviceType.WSA => Strings.Resources.S_TYPE_WSA,
                DeviceType.Service => Strings.Resources.S_TYPE_SERVICE,
                DeviceType.Recovery => $"{Strings.Resources.S_TYPE_USB} ({Strings.Resources.S_RECOVERY_MODE})",
                DeviceType.Sideload => $"{Strings.Resources.S_TYPE_USB} ({Strings.Resources.S_REBOOT_SIDELOAD})",
                _ => throw new NotSupportedException(),
            };

            var status = Device.Status switch
            {
                DeviceStatus.Ok => "{0}",
                DeviceStatus.Offline => Strings.Resources.S_STATUS_OFFLINE,
                DeviceStatus.Unauthorized => Strings.Resources.S_STATUS_UNAUTH,
                _ => throw new NotSupportedException(),
            };

            return string.Format(status, type);
        }
    }

    public Battery Battery => Device.Battery;

    public ObservableList<DriveViewModel> Drives => Device.Drives;

    #endregion

    public DateTime DiscoverTime { get; }

    #region Commands

    public DeviceAction BrowseCommand { get; }
    public DeviceAction RemoveCommand { get; }
    public DeviceAction ToggleRootCommand { get; }
    public DeviceAction SideloadCommand { get; }
    public List<object> RebootCommands { get; } = [];

    #endregion

    public LogicalDeviceViewModel(LogicalDevice device, bool subscribeRuntimeSettings = true) : base(device, subscribeRuntimeSettings)
    {
        DiscoverTime = DateTime.Now;

        Device = device;
        if (Device.Type is DeviceType.Emulator)
            UseIdForName = true;

        BrowseCommand = new(() => !IsOpen && device.Status is DeviceStatus.Ok && device.Type is not DeviceType.Sideload,
                            () => DeviceHelper.BrowseDeviceAction(this));

        RemoveCommand = DeviceHelper.RemoveDeviceCommand(this);

        ToggleRootCommand = DeviceHelper.ToggleRootDeviceCommand(this);

        foreach (RebootCommand.RebootType item in Enum.GetValues<RebootCommand.RebootType>())
        {
            RebootCommands.Add(new RebootCommand(this, item));

            if (item is RebootCommand.RebootType.Title)
                RebootCommands.Add(new Separator() { Margin = new(-11, 0, -11, 0)});
        }

        SideloadCommand = new(() => Device.Type is DeviceType.Sideload or DeviceType.Recovery && device.Status is DeviceStatus.Ok,
                              () => DeviceHelper.SideloadDeviceAction(this));

        if (subscribeRuntimeSettings)
        {
            InitializeDrives();
            AttachRuntimeSettings();
        }
    }

    internal void InitializeDrives() => Device.InitializeDrives();

    internal void AttachRuntimeSettings()
    {
        if (runtimeSettingsSubscribed)
            return;

        AttachBaseRuntimeSettings();
        Drives.ForEach(drive => drive.AttachRuntimeSettings());
        Data.RuntimeSettings.PropertyChanged += RuntimeSettings_PropertyChanged;
        runtimeSettingsSubscribed = true;
        IsOpen = Data.RuntimeSettings.DeviceToOpen?.ID == ID;
    }

    internal void DetachRuntimeSettings()
    {
        if (!runtimeSettingsSubscribed)
            return;

        Data.RuntimeSettings.PropertyChanged -= RuntimeSettings_PropertyChanged;
        runtimeSettingsSubscribed = false;
        DetachBaseRuntimeSettings();
        Drives.ForEach(drive => drive.DetachRuntimeSettings());
        IsOpen = false;
    }

    private void RuntimeSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppRuntimeSettings.DeviceToOpen))
        {
            IsOpen = Data.RuntimeSettings.DeviceToOpen is not null
                && Data.RuntimeSettings.DeviceToOpen.ID == ID;
        }
    }

    #region Setter functions

    public void SetAndroidVersion(string version)
    {
        if (!IsOpen)
            return;

        if (byte.TryParse(version.Split('.')[0], out byte ver))
            AndroidVersion = ver;
    }

    public void UpdateDevice(LogicalDeviceViewModel other) => UpdateDevice(other.Device);

    public void UpdateDevice(LogicalDevice other)
    {
        Device.RefreshConnection(other);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(TypeIcon));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(IpAddress));
        SetStatus(other.Status);
    }

    public void EnableRoot(bool enable)
    {
        Device.EnableRoot(enable);
        OnPropertyChanged(nameof(Root));
    }

    public bool SetRootStatus(RootStatus status)
    {
        if (Root != status)
        {
            Device.Root = status;
            OnPropertyChanged(nameof(Root));
            OnPropertyChanged(nameof(RootString));

            if (IsOpen)
                Data.RuntimeSettings.IsRootActive = status is RootStatus.Enabled;

            return true;
        }

        return false;
    }

    public void UpdateBattery() => Device.UpdateBattery();

    public Task<bool> UpdateDrives(IEnumerable<Drive> drives, Dispatcher dispatcher, bool asyncClassify = false) => Device.UpdateDrives(drives, dispatcher, asyncClassify);

    public Task<bool> UpdateDrives(LogicalDeviceViewModel other, Dispatcher dispatcher, bool asyncClassify = false)
        => UpdateDrives(other.Device.Drives.Select(d => d.Drive), dispatcher, asyncClassify);

    #endregion
}
