using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Cover(DeviceBase Device, IExternal External) : ControllableDeviceBase<ICoverDriver>(Device, External)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // empty
    }
}