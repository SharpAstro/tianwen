using Astap.Lib.Devices;
using Astap.Lib.Devices.ZWO;
using Microsoft.Extensions.DependencyInjection;

namespace Astap.Lib.Extensions;

public static class ZWOServiceCollectionExtensions
{
    public static IServiceCollection AddZWO(this IServiceCollection services) => services.AddSingleton<IDeviceSource<ZWODevice>, ZWODeviceSource>();
}
