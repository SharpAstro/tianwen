using FC.SDK;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.Canon;

namespace TianWen.Lib.Extensions;

public static class CanonServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Canon DSLR camera device source, URI factory, and camera factory.
    /// Discovers cameras via WPD (Windows), USB (LibUsbDotNet), and WiFi (mDNS + PTP/IP).
    /// </summary>
    public static IServiceCollection AddCanon(this IServiceCollection services) => services
        .AddSingleton<CanonCameraFactory>()
        .AddDevicSource<CanonDevice, CanonDeviceSource>(uri => new CanonDevice(uri));
}
