using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.Skywatcher;

namespace TianWen.Lib.Extensions;

public static class SkywatcherServiceCollectionExtensions
{
    public static IServiceCollection AddSkywatcher(this IServiceCollection services) => services
        .AddDevicSource<SkywatcherDevice, SkywatcherDeviceSource>(uri => new SkywatcherDevice(uri))
        // Skywatcher probes both baud rates (USB-integrated 115200, legacy 9600).
        // Two probes, same handshake, different baud — see SkywatcherSerialProbe.cs.
        .AddSerialProbe<SkywatcherSerialProbeUsb>()
        .AddSerialProbe<SkywatcherSerialProbeLegacy>();
}
