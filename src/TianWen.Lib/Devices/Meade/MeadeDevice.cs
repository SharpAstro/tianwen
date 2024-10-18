using System;

namespace TianWen.Lib.Devices.Meade;

public record MeadeDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public MeadeDevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(MeadeDevice).Name}/{deviceId}#{displayName}"))
    {
        // calls primary constructor
    }
}
