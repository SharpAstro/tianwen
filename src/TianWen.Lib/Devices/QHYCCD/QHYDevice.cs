using System;
using TianWen.Lib;

namespace TianWen.Lib.Devices.QHYCCD;

public record class QHYDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public QHYDevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(QHYDevice).Name}/{deviceId}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Camera => new QHYCameraDriver(this, sp.External),
        // Serial port in URI → standalone USB CFW; otherwise → camera-cable CFW
        DeviceType.FilterWheel => Query.QueryValue(DeviceQueryKey.Port) is { Length: > 0 }
            ? new QHYSerialControlledFilterWheelDriver(this, sp.External)
            : new QHYCameraControlledFilterWheelDriver(this, sp.External),
        // QFOC focuser — always serial
        DeviceType.Focuser => new QHYFocuserDriver(this, sp.External),
        _ => null
    };
}
