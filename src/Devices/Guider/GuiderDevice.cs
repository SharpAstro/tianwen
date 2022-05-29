using System;

namespace Astap.Lib.Devices.Guider
{
    public record class GuiderDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
    {
        public GuiderDevice(string deviceType, string deviceId, string displayName)
            : this(new Uri($"{UriScheme}://{typeof(GuiderDevice).Name}/{deviceId}?displayName={displayName}#{deviceType}"))
        {

        }
    }
}
