using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using System;

namespace Astap.Lib.Plan.Ascom
{
    public class AscomMount : MountBase<AscomDevice, AscomTelescopeDriver>
    {
        public AscomMount(AscomDevice device)
            : base(device.DeviceType == DeviceBase.TelescopeType ? device : throw new ArgumentException("Device is not an ASCOM telescope", nameof(device)))
        {
            // calls base
        }
    }
}
