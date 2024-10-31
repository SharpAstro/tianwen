using System;
using System.Collections.Specialized;

namespace TianWen.Lib.Devices.Meade;

public record MeadeDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public MeadeDevice(DeviceType deviceType, string deviceId, string displayName, string address)
        : this(new Uri($"{deviceType}://{typeof(MeadeDevice).Name}/{deviceId}#{displayName}?{new NameValueCollection { ["address"] = address }.ToQueryString()}"))
    {
        // calls primary constructor
    }

    public override string? Address => Query["address"];

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Mount => new MeadeLX200ProtocolMountDriver(this, external),
        _ => null
    };
}