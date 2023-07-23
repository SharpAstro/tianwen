using System.Web;
using System;

namespace Astap.Lib.Devices;

public enum DeviceType
{
    Unknown,

    /// <summary>
    /// Null device
    /// </summary>
    None,
    
    /// <summary>
    /// A device that implements <see cref="ICameraDriver"/>.
    /// </summary>
    Camera,

    CoverCalibrator,
    Telescope,
    Focuser,
    FilterWheel,
    Switch,

    /// <summary>
    /// A device that supports the PHD2 JSON-RPC based communication protocol over TCP.
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

