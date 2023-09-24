using System;

namespace Astap.Lib.Devices;

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
    /// A telescope in the sense of the actual mount (inherited from ASCOM terminology), implements <see cref="IMountDriver"/>.
    /// </summary>
    Telescope,

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
    /// A device that supports the PHD2 JSON-RPC based communication protocol over TCP, implementing <see cref="Guider.IGuider"/>
    /// </summary>
    PHD2
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

