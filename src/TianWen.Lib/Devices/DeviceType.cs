using System;

namespace TianWen.Lib.Devices;

public enum DeviceType
{
    /// <summary>
    /// Unknown device.
    /// </summary>
    Unknown,

    /// <summary>
    /// Profile virtual device, contains a list of device descriptors,
    /// is used for <see cref="Devices.Profile"/>.
    /// </summary>
    Profile,

    /// <summary>
    /// Null device, which is different from <see cref="Unknown"/>,
    /// is used for <see cref="NoneDevice"/>.
    /// </summary>
    None,
    
    /// <summary>
    /// A device that implements <see cref="ICameraDriver"/>.
    /// </summary>
    Camera,

    /// <summary>
    /// A cover/calibrator combined device that implements <see cref="ICoverDriver"/>.
    /// </summary>
    CoverCalibrator,

    /// <summary>
    /// A device that implements implements <see cref="IMountDriver"/>.
    /// </summary>
    Mount,

    /// <summary>
    /// A telescope in the sense of the actual mount (inherited from ASCOM terminology), alias of <see cref="Mount"/>.
    /// </summary>
    Telescope = Mount,

    /// <summary>
    /// A robotic focuser device that implements <see cref="IFocuserDriver"/>.
    /// </summary>
    Focuser,

    /// <summary>
    /// An electronic filter wheel device that implements <see cref="IFilterWheelDriver"/>.
    /// </summary>
    FilterWheel,

    /// <summary>
    /// A switch device (like turning on/off power etc. that implements <see cref="ISwitchDriver"/>.
    /// </summary>
    Switch,

    /// <summary>
    /// A software device that supports the <see cref="Guider.IGuider"/> interface, e.g. Open PHD2 Guiding via JSON-RPC over TCP.
    /// </summary>
    DedicatedGuiderSoftware,

    /// <summary>
    /// Short-hand for the most-commonly used (and so far only supported guider)
    /// </summary>
    PHD2 = DedicatedGuiderSoftware
}

public static class DeviceTypeHelper
{
    public static DeviceType TryParseDeviceType(string input)
    {
        return Enum.TryParse<DeviceType>(input, true, out var deviceType)
                ? deviceType
                : DeviceType.Unknown;
    }
}