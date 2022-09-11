using System.Diagnostics.CodeAnalysis;

namespace Astap.Lib.Devices.Ascom
{
    public static class AscomDeviceDriverFactory
    {
        const string Camera = nameof(Camera);
        const string CoverCalibrator = nameof(CoverCalibrator);
        const string Telescope = nameof(Telescope);
        const string Focuser = nameof(Focuser);
        const string Switch = nameof(Switch);

        public static readonly string CameraType = Camera;
        public static readonly string CoverCalibratorType = CoverCalibrator;
        public static readonly string TelescopeType = Telescope;
        public static readonly string FocuserType = Focuser;
        public static readonly string SwitchType = Switch;

        public static bool TryInstantiateDriver(this AscomDevice device, [NotNullWhen(true)] out AscomDeviceDriverBase? driver)
        {
            driver = device.DeviceType switch
            {
                Camera => new AscomCameraDriver(device),
                CoverCalibrator => new AscomCoverCalibratorDriver(device),
                Focuser => new AscomFocuserDriver(device),
                Switch => new AscomSwitchDriver(device),
                Telescope => new AscomTelescopeDriver(device),
                _ => null
            };

            return driver is not null;
        }
    }
}
