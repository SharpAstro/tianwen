using Astap.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace Astap.Lib.Extensions;

public static class ProfileServiceCollectionExtensions
{
    public static IServiceCollection AddASCOM(this IServiceCollection services) => services.AddSingleton<IDeviceSource<Profile>, ProfileIterator>();
}
