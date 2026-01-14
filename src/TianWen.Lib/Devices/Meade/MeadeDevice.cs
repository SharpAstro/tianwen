using System;
using System.Collections.Specialized;

namespace TianWen.Lib.Devices.Meade;

public record MeadeDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public MeadeDevice(DeviceType deviceType, string deviceId, string displayName, string port)
        : this(new Uri($"{deviceType}://{typeof(MeadeDevice).Name}/{deviceId}?{new NameValueCollection { ["port"] = port }.ToQueryString()}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Mount => new MeadeLX200ProtocolMountDriver(this, external),
        _ => null
    };
}