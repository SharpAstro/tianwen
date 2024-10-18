using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Switch(DeviceBase Device, IExternal External) : ControllableDeviceBase<ISwitchDriver>(Device, External)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}