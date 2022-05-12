using System;

namespace Astap.Lib.Devices.Ascom;

public record class AscomDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public AscomDevice(string deviceType, string deviceId, string displayName)
        : this(new Uri($"device://{typeof(AscomDevice).Name}/{deviceId}?displayName={displayName}#{deviceType}"))
    {

    }
}
