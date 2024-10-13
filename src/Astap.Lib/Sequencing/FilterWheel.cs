using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public record FilterWheel(DeviceBase Device) : ControllableDeviceBase<IFilterWheelDriver>(Device)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}