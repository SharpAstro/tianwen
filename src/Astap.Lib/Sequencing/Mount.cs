using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public class Mount(DeviceBase device) : ControllableDeviceBase<IMountDriver>(device)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
