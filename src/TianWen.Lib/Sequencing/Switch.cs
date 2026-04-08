using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Switch(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<ISwitchDriver>(Device, ServiceProvider)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}