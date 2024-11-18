using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics.CodeAnalysis;
using TianWen.Lib.Devices;

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
        .AddSingleton<IDeviceUriRegistry, DeviceUriRegistry>()
        .AddSingleton<ICombinedDeviceManager, CombinedDeviceManager>();

    internal static IServiceCollection AddDeviceType<TDevice>(this IServiceCollection services, Func<Uri, TDevice> func) where TDevice : DeviceBase => services
        .AddKeyedSingleton<Func<Uri, DeviceBase>>(typeof(TDevice).Name.ToLowerInvariant(), func);

    internal static IServiceCollection AddDevicSource<TDevice, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        this IServiceCollection services,
        Func<Uri, TDevice> func
    ) where TDevice : DeviceBase where TImplementation : class, IDeviceSource<TDevice> => services
        .AddSingleton<IDeviceSource<DeviceBase>, TImplementation>()
        .AddDeviceType(func);
}