using System;
using AscomDeviceType = ASCOM.Common.DeviceTypes;

namespace TianWen.Lib.Devices.Ascom;

internal static class AscomDeviceTypeExtensions
{
    public static DeviceType ToDeviceType(this AscomDeviceType deviceType) => deviceType switch
    {
        AscomDeviceType.Camera => DeviceType.Camera,
        AscomDeviceType.CoverCalibrator => DeviceType.CoverCalibrator,
        AscomDeviceType.FilterWheel => DeviceType.FilterWheel,
        AscomDeviceType.Focuser => DeviceType.Focuser,
        AscomDeviceType.Switch => DeviceType.Switch,
        AscomDeviceType.Telescope => DeviceType.Telescope,
        _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, null)
    };
}
