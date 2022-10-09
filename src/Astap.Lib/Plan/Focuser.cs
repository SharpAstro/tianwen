using Astap.Lib.Devices;

namespace Astap.Lib.Plan;

public class Focuser : ControllableDeviceBase<IFocuserDriver>
{
    public Focuser(DeviceBase device)
        : base(device)
    {

    }

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
