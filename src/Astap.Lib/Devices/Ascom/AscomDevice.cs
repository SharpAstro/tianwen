using System;

namespace Astap.Lib.Devices.Ascom;

public record class AscomDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public AscomDevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(AscomDevice).Name}/{deviceId}#{displayName}"))
    {

    }

    protected override object? NewImplementationFromDevice()
        => DeviceType switch
        {
            DeviceType.Camera => new AscomCameraDriver(this),
            DeviceType.CoverCalibrator => new AscomCoverCalibratorDriver(this),
            DeviceType.FilterWheel => new AscomFilterWheelDriver(this),
            DeviceType.Focuser => new AscomFocuserDriver(this),
            DeviceType.Switch => new AscomSwitchDriver(this),
            DeviceType.Telescope => new AscomTelescopeDriver(this),
            _ => null
        };
}
