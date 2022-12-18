using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public class Mount : ControllableDeviceBase<IMountDriver>
{
    public Mount(DeviceBase device) : base(device) { }

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
