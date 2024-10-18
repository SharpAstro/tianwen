using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using Microsoft.Extensions.DependencyInjection;
namespace Astap.Lib.Extensions;

public static class AscomServiceCollectionExtensions
{
    public static IServiceCollection AddAscom(this IServiceCollection services) => services.AddSingleton<IDeviceSource<AscomDevice>, AscomProfile>();
}
