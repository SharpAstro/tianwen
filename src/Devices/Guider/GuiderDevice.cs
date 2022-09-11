using System;

namespace Astap.Lib.Devices.Guider
{
    public record class GuiderDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
    {
        public GuiderDevice(string deviceType, string deviceId, string displayName)
            : this(new Uri($"{UriScheme}://{typeof(GuiderDevice).Name}/{deviceId}?displayName={displayName}#{deviceType}"))
        {

        }

        protected override object? NewImplementationFromDevice()
            => DeviceType switch
            {
                PHD2GuiderDriver.PHD2 => new PHD2GuiderDriver(this),
                _ => null
            };
    }
}
