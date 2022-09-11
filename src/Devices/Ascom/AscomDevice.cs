using System;

namespace Astap.Lib.Devices.Ascom;

public record class AscomDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public AscomDevice(string deviceType, string deviceId, string displayName)
        : this(new Uri($"{UriScheme}://{typeof(AscomDevice).Name}/{deviceId}?displayName={displayName}#{deviceType}"))
    {

    }

    protected override object? NewImplementationFromDevice()
        => DeviceType switch
        {
            Camera => new AscomCameraDriver(this),
            CoverCalibrator => new AscomCoverCalibratorDriver(this),
            FilterWheel => new AscomFilterWheelDriver(this),
            Focuser => new AscomFocuserDriver(this),
            Switch => new AscomSwitchDriver(this),
            Telescope => new AscomTelescopeDriver(this),
            _ => null
        };
}
