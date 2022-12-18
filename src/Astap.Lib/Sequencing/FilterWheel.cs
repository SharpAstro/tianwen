using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public class FilterWheel : ControllableDeviceBase<IFilterWheelDriver>
{
    public FilterWheel(DeviceBase device) : base(device) { }

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
