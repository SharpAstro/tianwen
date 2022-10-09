using Astap.Lib.Devices;
using Astap.Lib.Devices.Guider;

namespace Astap.Lib.Plan;

public class Guider : ControllableDeviceBase<IGuider>
{
    public Guider(GuiderDevice device) : base(device)
    {
    }

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
