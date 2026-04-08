using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Mount(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<IMountDriver>(Device, ServiceProvider)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}