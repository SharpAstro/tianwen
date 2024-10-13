using System;

namespace Astap.Lib.Devices.ZWO;

public record class ZWODevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public ZWODevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(ZWODevice).Name}/{deviceId}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewImplementationFromDevice() => DeviceType switch
    {
        DeviceType.Camera => new ZWOCameraDriver(this),
        DeviceType.FilterWheel => new ZWOFilterWheelDriver(this),
        DeviceType.Focuser => new ZWOFocuserDriver(this),
        _ => null
    };
}