using System;

namespace Astap.Lib.Devices.Ascom;

public record class AscomDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public AscomDevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(AscomDevice).Name}/{deviceId}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Camera => new AscomCameraDriver(this, external),
        DeviceType.CoverCalibrator => new AscomCoverCalibratorDriver(this, external),
        DeviceType.FilterWheel => new AscomFilterWheelDriver(this, external),
        DeviceType.Focuser => new AscomFocuserDriver(this, external),
        DeviceType.Switch => new AscomSwitchDriver(this, external),
        DeviceType.Telescope => new AscomTelescopeDriver(this, external),
        _ => null
    };
}
