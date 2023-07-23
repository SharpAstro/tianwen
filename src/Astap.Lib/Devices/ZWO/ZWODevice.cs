using System;

namespace Astap.Lib.Devices.ZWO;

public record class ZWODevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public ZWODevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(ZWODevice).Name}/{deviceId}#{displayName}"))
    {

    }

    protected override object? NewImplementationFromDevice()
        => DeviceType switch
        {
            DeviceType.Camera => new ZWOCameraDriver(this),
          //  CoverCalibrator => new ZWOCoverCalibratorDriver(this),
          //  FilterWheel => new ZWOFilterWheelDriver(this),
          //  Focuser => new ZWOFocuserDriver(this),
            _ => null
        };
}