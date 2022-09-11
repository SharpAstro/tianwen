using Astap.Lib.Devices;

namespace Astap.Lib.Plan
{
    public abstract class MountBase<TDevice, TDriver> : ControllableDeviceBase<TDevice, TDriver>
        where TDevice : DeviceBase
        where TDriver : IDeviceDriver
    {
        public MountBase(TDevice device) : base(device) { }
    }
}
