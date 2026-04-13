using System;
using System.Collections.Specialized;
using TianWen.Lib;

namespace TianWen.Lib.Devices.Meade;

public record MeadeDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public MeadeDevice(DeviceType deviceType, string deviceId, string displayName, string port)
        : this(new Uri($"{deviceType}://{typeof(MeadeDevice).Name}/{deviceId}?{new NameValueCollection { ["port"] = port }.ToQueryString()}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Mount => new MeadeLX200ProtocolMountDriver(this, sp),
        _ => null
    };
}