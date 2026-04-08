using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Focuser(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<IFocuserDriver>(Device, ServiceProvider)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}