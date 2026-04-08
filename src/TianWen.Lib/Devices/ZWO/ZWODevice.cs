using System;
using TianWen.Lib;

namespace TianWen.Lib.Devices.ZWO;

public record class ZWODevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public ZWODevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(ZWODevice).Name}/{deviceId}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Camera => new ZWOCameraDriver(this, sp.External),
        DeviceType.FilterWheel => new ZWOFilterWheelDriver(this, sp.External),
        DeviceType.Focuser => new ZWOFocuserDriver(this, sp.External),
        _ => null
    };
}