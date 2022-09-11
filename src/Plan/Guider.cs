using Astap.Lib.Devices.Guider;

namespace Astap.Lib.Plan
{
    public class Guider : ControllableDeviceBase<GuiderDevice, IGuider>
    {
        public Guider(GuiderDevice device) : base(device)
        {
        }
    }
}
