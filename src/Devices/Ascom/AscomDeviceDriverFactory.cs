using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Astap.Lib.Devices.Ascom
{
    public static class AscomDeviceDriverFactory
    {
        public static bool TryInstatiateDriver(AscomDevice device, [NotNullWhen(true)] out AscomDeviceDriverBase? driver)
        {
            driver = device.DeviceType switch
            {
                "Camera" => new AscomCameraDriver(device),
                "CoverCalibrator" => new AscomCoverCalibratorDriver(device),
                "Focuser" => new AscomFocuserDriver(device),
                "Switch" => new AscomSwitchDriver(device),
                "Telescope" => new AscomTelescopeDriver(device),
                _ => null
            };

            return driver is not null;
        }
    }
}
