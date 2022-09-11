using Astap.Lib.Devices;

namespace Astap.Lib.Plan
{
    public abstract class FocuserBase<TDevice, TDriver> : ControllableDeviceBase<TDevice, TDriver>
        where TDevice : DeviceBase
        where TDriver : IDeviceDriver
    {
        public FocuserBase(TDevice device)
            : base(device)
        {

        }
    }
}
