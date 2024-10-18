using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Focuser(DeviceBase Device, IExternal External) : ControllableDeviceBase<IFocuserDriver>(Device, External)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}