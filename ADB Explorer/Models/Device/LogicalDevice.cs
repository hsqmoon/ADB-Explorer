using ADB_Explorer.Helpers;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;
using AdvancedSharpAdbClient.Models;

namespace ADB_Explorer.Models;

/// <summary>
/// Represents all devices acquired by <code>adb devices</code>
/// </summary>
public class LogicalDevice : Device
{
    #region Full properties

    private string name;
    public string Name
    {
        get => name;
        set => Set(ref name, value);
    }

    private RootStatus root = RootStatus.Unchecked;
    public RootStatus Root
    {
        get => root;
        set => Set(ref root, value);
    }

    private Battery battery;
    public Battery Battery
    {
        get => battery;
        protected set => Set(ref battery, value);
    }

    private ObservableList<DriveViewModel> drives = [];
    public ObservableList<DriveViewModel> Drives
    {
        get => drives;
        set => Set(ref drives, value);
    }

    #endregion

    public DeviceData DeviceData { get; private set; }

    private LogicalDevice(string name, string id)
    {
        Name = name;
        ID = id;

        Battery = new Battery();
    }

    public static LogicalDevice New(Match match)
        => New(match, new(match.Value));

    public static LogicalDevice New(DeviceData device)
        => New(device.Serial, device.State.ToString(), device.Model, device.Name, device);

    private static LogicalDevice New(Match match, DeviceData deviceData)
        => New(
            match.Groups["id"].Value,
            match.Groups["status"].Value,
            match.Groups["model"].Value,
            match.Groups["device"].Value,
            deviceData);

    private static LogicalDevice New(string id, string status, string model, string deviceName, DeviceData deviceData)
    {
        status = status.ToLowerInvariant();
        var name = DeviceHelper.ParseDeviceName(model ?? "", deviceName ?? "");

        var deviceType = DeviceHelper.GetType(id, status);
        var deviceStatus = DeviceHelper.GetStatus(status);
        var ip = deviceType is DeviceType.Remote ? id.Split(':')[0] : "";
        var rootStatus = deviceType is DeviceType.Recovery
            ? RootStatus.Enabled
            : RootStatus.Unchecked;

        if (deviceType is DeviceType.WSA && name.Contains("subsystem", StringComparison.InvariantCultureIgnoreCase))
            name = Strings.Resources.S_TYPE_WSA;

        return new LogicalDevice(name, id)
        {
            Type = deviceType,
            Status = deviceStatus,
            Root = rootStatus,
            IpAddress = ip,
            DeviceData = deviceData
        };
    }

    public RootStatus ChangeRoot(bool enable, CancellationToken cancellationToken = default)
    {
        return enable
            ? ADBService.Root(this, cancellationToken) ? RootStatus.Enabled : RootStatus.Forbidden
            : ADBService.Unroot(this, cancellationToken) ? RootStatus.Disabled : RootStatus.Unchecked;
    }

    public void RefreshConnection(LogicalDevice other)
    {
        if (other is null)
            return;

        var nextIpAddress = !string.IsNullOrWhiteSpace(other.IpAddress)
            ? other.IpAddress
            : IpAddress;

        if (!string.IsNullOrWhiteSpace(other.Name))
            Name = other.Name;
        Type = other.Type;
        IpAddress = nextIpAddress;
        DeviceData = other.DeviceData;
    }

    public void ApplyBatteryInfo(Dictionary<string, string> batteryInfo) => Battery.Update(batteryInfo);

    #region Drive handling

    internal void InitializeDrives()
    {
        if (Drives.Count > 0)
            return;

        Drives.Add(new LogicalDriveViewModel(new(path: AdbExplorerConst.DRIVE_TYPES.First(d => d.Value is AbstractDrive.DriveType.Root).Key)));
        Drives.Add(new LogicalDriveViewModel(new(path: AdbExplorerConst.DRIVE_TYPES.First(d => d.Value is AbstractDrive.DriveType.Internal).Key)));

        Drives.Add(new VirtualDriveViewModel(new(path: AdbLocation.StringFromLocation(Navigation.SpecialLocation.RecycleBin), -1)));
        Drives.Add(new VirtualDriveViewModel(new(path: AdbExplorerConst.TEMP_PATH)));
        Drives.Add(new VirtualDriveViewModel(new(path: AdbLocation.StringFromLocation(Navigation.SpecialLocation.PackageDrive))));
    }

    /// <summary>
    /// Update drive parameters, add new drives, remove non-existent drives
    /// </summary>
    /// <param name="drives"></param>
    /// <returns><see langword="true"/> if drives have been added or removed</returns>
    public bool UpdateDrives(IEnumerable<Drive> drives)
    {
        if (drives is null)
            return false;

        bool added = false;

        foreach (var other in drives)
        {
            // Accommodate for changing the path to /sdcard
            var selfQ = Drives.Where(d => d.Path == other.Path || (other.Type is AbstractDrive.DriveType.Internal && d.Type is AbstractDrive.DriveType.Internal));
            if (selfQ.Any())
            {
                // Update the drive if it exists
                var self = selfQ.First();

                switch (self)
                {
                    case LogicalDriveViewModel logical:
                        logical.UpdateDrive((LogicalDrive)other);
                        if (other.Type is not AbstractDrive.DriveType.Unknown)
                            logical.SetType(other.Type);
                        break;
                    case VirtualDriveViewModel virt:
                        virt.SetItemsCount(((VirtualDrive)other).ItemsCount);
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }
            // Create a new drive if it doesn't exist
            else if (other is LogicalDrive logical)
            {
                Drives.Add(new LogicalDriveViewModel(logical));
                added = true;
            }
            else if (other is VirtualDrive virt && !Drives.Any(d => d.Type == virt.Type))
            {
                Drives.Add(new VirtualDriveViewModel(virt));
                added = true;
            }
            else
                throw new NotSupportedException();
        }

        // Remove all drives that were not discovered in the last update
        var removedDrives = Drives.OfType<LogicalDriveViewModel>()
            .Where(self => !drives.Any(other => other.Path == self.Path
                || other.Type is AbstractDrive.DriveType.Internal && self.Type is AbstractDrive.DriveType.Internal))
            .ToList();
        var removed = removedDrives.Count > 0;
        Drives.RemoveAll(removedDrives);

        return added || removed;
    }

    #endregion

    public override string ToString() => Name;
}
