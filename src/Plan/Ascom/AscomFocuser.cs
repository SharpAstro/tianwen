using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using System;

namespace Astap.Lib.Plan.Ascom
{
    public class AscomFocuser : FocuserBase<AscomDevice, AscomFocuserDriver>
    {
        public AscomFocuser(AscomDevice device)
            : base(device.DeviceType == DeviceBase.FocuserType ? device : throw new ArgumentException("Device is not an ASCOM focuser", nameof(device)))
        {
        }
    }
}
