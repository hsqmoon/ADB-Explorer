using ADB_Explorer.Helpers;
using ADB_Explorer.Models;

namespace ADB_Explorer.ViewModels;

public abstract class ServiceDeviceViewModel : PairingDeviceViewModel
{
    #region Full properties

    private ServiceDevice device;
    protected new ServiceDevice Device
    {
        get => device;
        set => Set(ref device, value);
    }

    private string uiPairingCode;
    public string UIPairingCode
    {
        get => uiPairingCode;
        set
        {
            if (Set(ref uiPairingCode, value))
                SetPairingCode(uiPairingCode?.Replace("-", ""));
        }
    }

    #endregion

    #region Read only properties

    public ServiceDevice.ServiceType MdnsType => Device.MdnsType;

    public override string Tooltip
    {
        get
        {
            var type = MdnsType is ServiceDevice.ServiceType.QrCode
                ? Strings.Resources.S_DEVICE_QR
                : Strings.Resources.S_DEVICE_READY_PAIR;

            return $"{Strings.Resources.S_TYPE_SERVICE} - {type}";
        }
    }

    #endregion

    public DeviceAction PairCommand { get; }

    public ServiceDeviceViewModel(ServiceDevice service, bool subscribeRuntimeSettings = true)
        : base(service, subscribeRuntimeSettings)
    {
        Device = service;

        PairCommand = new(() => IsPairingCodeValid && device.Status is DeviceStatus.Unauthorized,
                          () => _ = DeviceHelper.PairService(this));
    }

    /// <summary>
    /// Updates the pairing port with the pairing port of the given service.
    /// </summary>
    /// <param name="other">The service with the new port.</param>
    public bool UpdateService(ServiceDeviceViewModel other)
    {
        return SetPairingPort(other.PairingPort);
    }

    public static ServiceDeviceViewModel New(ServiceDevice device, bool subscribeRuntimeSettings = true)
    {
        return device switch
        {
            PairingService => new PairingServiceViewModel(device, subscribeRuntimeSettings),
            ConnectService => new ConnectServiceViewModel(device, subscribeRuntimeSettings),
            _ => throw new NotImplementedException(),
        };
    }
}

public class PairingServiceViewModel : ServiceDeviceViewModel
{
    public PairingServiceViewModel(ServiceDevice service, bool subscribeRuntimeSettings = true)
        : base(service, subscribeRuntimeSettings)
    { }
}

public class ConnectServiceViewModel : ServiceDeviceViewModel
{
    public ConnectServiceViewModel(ServiceDevice service, bool subscribeRuntimeSettings = true)
        : base(service, subscribeRuntimeSettings)
    { }
}
