using System;
using System.Runtime.Versioning;
using TianWen.Lib;

namespace TianWen.Lib.Devices.Ascom;

public record class AscomDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public AscomDevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(AscomDevice).Name}/{deviceId}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) =>
        OperatingSystem.IsWindows() ? InstantiateAscomDriver(sp) : null;

    [SupportedOSPlatform("Windows")]
    private IDeviceDriver? InstantiateAscomDriver(IServiceProvider sp) =>
        DeviceType switch
        {
            DeviceType.Camera => new AscomCameraDriver(this, sp),
            DeviceType.CoverCalibrator => new AscomCoverCalibratorDriver(this, sp),
            DeviceType.FilterWheel => new AscomFilterWheelDriver(this, sp),
            DeviceType.Focuser => new AscomFocuserDriver(this, sp),
            DeviceType.Switch => new AscomSwitchDriver(this, sp),
            DeviceType.Telescope => new AscomTelescopeDriver(this, sp),
            _ => null
        };
}
