using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.Meade;

namespace TianWen.Lib.Extensions;

public static class MeadeServiceCollectionExtensions
{
    public static IServiceCollection AddMeade(this IServiceCollection services) => services.AddSingleton<IDeviceSource<DeviceBase>, MeadeDeviceSource>();
}
