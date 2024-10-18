using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Sequencing;

public record Camera(DeviceBase Device, IExternal External) : ControllableDeviceBase<ICameraDriver>(Device, External)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
