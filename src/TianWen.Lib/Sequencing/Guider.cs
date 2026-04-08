using System;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;

namespace TianWen.Lib.Sequencing;

public record Guider(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<IGuider>(Device, ServiceProvider)
{

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
