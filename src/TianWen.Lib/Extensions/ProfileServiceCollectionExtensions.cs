using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class ProfileServiceCollectionExtensions
{
    public static IServiceCollection AddProfiles(this IServiceCollection services) => services.AddSingleton<IDeviceSource<DeviceBase>, ProfileIterator>();
}
