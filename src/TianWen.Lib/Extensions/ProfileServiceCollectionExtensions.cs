using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class ProfileServiceCollectionExtensions
{
    public static IServiceCollection AddASCOM(this IServiceCollection services) => services.AddSingleton<IDeviceSource<Profile>, ProfileIterator>();
}
