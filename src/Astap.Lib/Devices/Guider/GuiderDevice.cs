using System;

namespace Astap.Lib.Devices.Guider
{
    public record class GuiderDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
    {
        public GuiderDevice(DeviceType deviceType, string deviceId, string displayName)
            : this(new Uri($"{UriScheme}://{typeof(GuiderDevice).Name}/{deviceId}?displayName={displayName}#{deviceType}"))
        {

        }

        protected override object? NewImplementationFromDevice()
            => DeviceType switch
            {
                DeviceType.PHD2 => new PHD2GuiderDriver(this),
                _ => null
            };
    }
}
