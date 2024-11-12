using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class DeviceManagerServiceCollectionExtensions
{
    public static IServiceCollection AddDeviceManager(this IServiceCollection services) => services.AddSingleton<ICombinedDeviceManager, CombinedDeviceManager>();
}