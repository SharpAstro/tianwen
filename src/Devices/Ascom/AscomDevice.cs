using Astap.Lib.Plan;
using System;
using System.Diagnostics;

namespace Astap.Lib.Devices.Ascom;

public record class AscomDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public AscomDevice(string deviceType, string deviceId, string displayName)
        : this(new Uri($"{UriScheme}://{typeof(AscomDevice).Name}/{deviceId}?displayName={displayName}#{deviceType}"))
    {

    }

    const string Profile = nameof(Profile);
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

    protected override object? NewImplementationFromDevice()
        => DeviceType switch
        {
            Profile => new AscomProfile(),
            Camera => new AscomCameraDriver(this),
            CoverCalibrator => new AscomCoverCalibratorDriver(this),
            Focuser => new AscomFocuserDriver(this),
            Switch => new AscomSwitchDriver(this),
            Telescope => new AscomTelescopeDriver(this),
            _ => null
        };
}
