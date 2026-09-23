using System;

namespace TianWen.Lib.Devices.ToupTek;

public record class ToupTekDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public ToupTekDevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(ToupTekDevice).Name}/{deviceId}#{displayName}"))
    {
        // calls primary constructor
    }

    /// <summary>
    /// Cameras only for now. The same SDK also drives ToupTek's own filter wheels and focusers
    /// (<c>TOUPCAM_FLAG_FILTERWHEEL</c> and the AAF calls), through the camera handle rather than a
    /// second library, so they are further branches here once one is on the bench to verify against.
    /// </summary>
    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Camera => new ToupTekCameraDriver(this, sp),
        _ => null
    };
}
