using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Mount(DeviceBase Device, IExternal External) : ControllableDeviceBase<IMountDriver>(Device, External)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}