using System;

namespace Astap.Lib.Devices.Ascom;

public record class AscomDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public static Uri CreateUri(string deviceType, string deviceId, string displayName)
        => new($"device://{typeof(AscomDevice).Name}/{deviceId}?displayName={displayName}#{deviceType}");
}
