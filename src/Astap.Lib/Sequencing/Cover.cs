using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public record Cover(DeviceBase Device, IExternal External) : ControllableDeviceBase<ICoverDriver>(Device, External)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // empty
    }
}