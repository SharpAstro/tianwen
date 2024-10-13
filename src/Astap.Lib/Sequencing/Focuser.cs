using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public record Focuser(DeviceBase Device) : ControllableDeviceBase<IFocuserDriver>(Device)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}