using Astap.Lib.Devices;

namespace Astap.Lib.Plan
{
    public abstract class CameraBase<TDevice, TDriver> : ControllableDeviceBase<TDevice, TDriver>
        where TDevice : DeviceBase
        where TDriver : IDeviceDriver
    {
        public CameraBase(TDevice device) : base(device) { }

        public abstract bool? HasCooler { get; }

        public abstract double? PixelSizeX { get; }

        public abstract double? PixelSizeY { get; }
    }
}
