using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.Meade;

namespace TianWen.Lib.Extensions;

public static class MeadeServiceCollectionExtensions
{
    public static IServiceCollection AddMeade(this IServiceCollection services) => services.AddDevicSource<MeadeDevice, MeadeDeviceSource>(uri => new MeadeDevice(uri));
}