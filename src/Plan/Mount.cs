using Astap.Lib.Devices;

namespace Astap.Lib.Plan;

public class Mount : ControllableDeviceBase<IDeviceDriver>
{
    public Mount(DeviceBase device) : base(device) { }

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
