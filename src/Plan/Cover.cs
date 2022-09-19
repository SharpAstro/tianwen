using Astap.Lib.Devices;

namespace Astap.Lib.Plan;

public abstract class Cover : ControllableDeviceBase<IDeviceDriver>
{
    public Cover(DeviceBase device)
        : base(device)
    {

    }

    public abstract bool? IsOpen { get; }
}
