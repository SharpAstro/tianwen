using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Sequencing;

public record Camera(DeviceBase Device) : ControllableDeviceBase<ICameraDriver>(Device)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
