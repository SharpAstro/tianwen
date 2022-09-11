using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using System;
using static Astap.Lib.Devices.Ascom.AscomDeviceDriverFactory;

namespace Astap.Lib.Plan
{
    public abstract class MountBase<T>
        where T : DeviceBase
    {
        public T Device { get; }

        public MountBase(T device)
        {
            Device = device;
        }
    }

    public class AscomMount : MountBase<AscomDevice>
    {
        public AscomMount(AscomDevice telescopeDevice)
            : base(telescopeDevice.DeviceType == TelescopeType ? telescopeDevice : throw new ArgumentException("Device is not an Ascom telescope", nameof(telescopeDevice)))
        {
        }
    }
}
