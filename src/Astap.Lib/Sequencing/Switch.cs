using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public record Switch(DeviceBase Device, IExternal External) : ControllableDeviceBase<ISwitchDriver>(Device, External)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}