using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public class Camera : ControllableDeviceBase<ICameraDriver>
{
    public Camera(DeviceBase device) : base(device) { }

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
