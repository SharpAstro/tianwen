using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record FilterWheel(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<IFilterWheelDriver>(Device, ServiceProvider)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}