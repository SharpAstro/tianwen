using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public record FilterWheel(DeviceBase Device, IExternal External) : ControllableDeviceBase<IFilterWheelDriver>(Device, External)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}