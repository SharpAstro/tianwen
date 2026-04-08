using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.Canon;

namespace TianWen.Lib.Extensions;

public static class CanonServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Canon DSLR camera device source and URI factory.
    /// Discovers cameras via USB (LibUsbDotNet) and WiFi (mDNS + PTP/IP).
    /// </summary>
    public static IServiceCollection AddCanon(this IServiceCollection services) => services
        .AddDevicSource<CanonDevice, CanonDeviceSource>(uri => new CanonDevice(uri));
}
