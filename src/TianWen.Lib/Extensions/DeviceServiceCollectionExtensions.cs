using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics.CodeAnalysis;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Extensions;

public static class DeviceServiceCollectionExtensions
{
    /// <summary>
    /// Add a combined device manager and a device registry.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddDevices(this IServiceCollection services) => services
        .AddDeviceType(uri => new NoneDevice(uri))
        .AddSingleton<IDeviceHub, DeviceHub>()
        .AddSingleton<ISerialProbeService, SerialProbeService>()
        .AddSingleton<IDeviceDiscovery, DeviceDiscovery>();

    internal static IServiceCollection AddDeviceType<TDevice>(this IServiceCollection services, Func<Uri, TDevice> func) where TDevice : DeviceBase => services
        .AddKeyedSingleton<Func<Uri, DeviceBase>>(typeof(TDevice).Name.ToLowerInvariant(), func);

    internal static IServiceCollection AddDevicSource<TDevice, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        this IServiceCollection services,
        Func<Uri, TDevice> func
    ) where TDevice : DeviceBase where TImplementation : class, IDeviceSource<TDevice> => services
        .AddSingleton<IDeviceSource<DeviceBase>, TImplementation>()
        .AddDeviceType(func);

    /// <summary>
    /// Register an <see cref="ISerialProbe"/> implementation. Probes are discovered by
    /// <see cref="ISerialProbeService"/> via <c>IEnumerable&lt;ISerialProbe&gt;</c> injection
    /// and run once per port × baud group during <see cref="IDeviceDiscovery.DiscoverAsync"/>.
    /// </summary>
    public static IServiceCollection AddSerialProbe<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProbe>(
        this IServiceCollection services
    ) where TProbe : class, ISerialProbe => services
        .AddSingleton<ISerialProbe, TProbe>();
}