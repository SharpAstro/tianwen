using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using System;

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
            : base(telescopeDevice.DeviceType == AscomDevice.TelescopeType ? telescopeDevice : throw new ArgumentException("Device is not an Ascom telescope", nameof(telescopeDevice)))
        {
        }
    }
}
