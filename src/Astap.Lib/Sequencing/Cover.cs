using Astap.Lib.Devices;


namespace Astap.Lib.Sequencing;

public class Cover : ControllableDeviceBase<ICoverDriver>
{
    public Cover(DeviceBase device)
        : base(device)
    {

    }

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // empty
    }
}
