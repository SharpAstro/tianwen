using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Ascom;
using Microsoft.Extensions.DependencyInjection;
namespace TianWen.Lib.Extensions;

public static class AscomServiceCollectionExtensions
{
    public static IServiceCollection AddAscom(this IServiceCollection services) => services.AddSingleton<IDeviceSource<AscomDevice>, AscomDeviceIterator>();
}