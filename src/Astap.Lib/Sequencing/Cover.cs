using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public record Cover(DeviceBase Device) : ControllableDeviceBase<ICoverDriver>(Device)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // empty
    }
}