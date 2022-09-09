using System.Diagnostics.CodeAnalysis;

namespace Astap.Lib.Devices.Guider
{
    public static class GuiderDeviceDriverFactory
    {
        public static bool TryInstantiateDriver(GuiderDevice device, [NotNullWhen(true)] out IGuider? guider)
            => TryInstantiate(device, out guider);

        public static bool TryInstantiateDeviceSource(GuiderDevice device, [NotNullWhen(true)] out IDeviceSource<GuiderDevice>? deviceSource)
            => TryInstantiate(device, out deviceSource);

        private static bool TryInstantiate<T>(GuiderDevice device, [NotNullWhen(true)] out T? iface)
        {
            var driver = device.DeviceType switch
            {
                PHD2GuiderDriver.PHD2 => new PHD2GuiderDriver(device),
                _ => null
            };

            if (driver is T asT)
            {
                iface = asT;
                return true;
            }
            else
            {
                iface = default;
                return false;
            }
        }
    }
}
