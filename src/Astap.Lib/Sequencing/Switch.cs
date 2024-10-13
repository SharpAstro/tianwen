using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public record Switch(DeviceBase Device) : ControllableDeviceBase<ISwitchDriver>(Device)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}