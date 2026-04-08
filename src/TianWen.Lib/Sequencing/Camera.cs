using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Camera(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<ICameraDriver>(Device, ServiceProvider)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
