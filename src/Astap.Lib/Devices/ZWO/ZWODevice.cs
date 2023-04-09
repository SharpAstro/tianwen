using System;

namespace Astap.Lib.Devices.ZWO;

public record class ZWODevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public ZWODevice(string deviceType, string deviceId, string displayName)
        : this(new Uri($"{UriScheme}://{typeof(ZWODevice).Name}/{deviceId}?displayName={displayName}#{deviceType}"))
    {

    }

    protected override object? NewImplementationFromDevice()
        => DeviceType switch
        {
            Camera => new ZWOCameraDriver(this),
          //  CoverCalibrator => new ZWOCoverCalibratorDriver(this),
          //  FilterWheel => new ZWOFilterWheelDriver(this),
          //  Focuser => new ZWOFocuserDriver(this),
            _ => null
        };
}