using System.Diagnostics.CodeAnalysis;

namespace Astap.Lib.Devices.Guider
{
    public static class GuiderDeviceDriverFactory
    {
        public static bool TryInstatiateDriver(GuiderDevice device, [NotNullWhen(true)] out IGuider? guider)
        {
            guider = device.DeviceType switch
            {
                "PHD2" => new PHD2GuiderDriver(device),
                _ => null
            };

            return guider is not null;
        }
    }
}
