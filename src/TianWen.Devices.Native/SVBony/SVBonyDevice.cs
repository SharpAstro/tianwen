using System;

namespace TianWen.Lib.Devices.SVBony;

public record class SVBonyDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public SVBonyDevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(SVBonyDevice).Name}/{deviceId}#{displayName}"))
    {
        // calls primary constructor
    }

    /// <summary>
    /// Cameras only: the SVBCameraSDK drives SVBONY's cameras and nothing else. SVBONY's newer ToupTek-based
    /// line (the <c>svbonycam</c> library) is a ToupTek brand instead, and goes through <c>ToupTekDevice</c>.
    /// </summary>
    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Camera => new SVBonyCameraDriver(this, sp),
        _ => null
    };
}
