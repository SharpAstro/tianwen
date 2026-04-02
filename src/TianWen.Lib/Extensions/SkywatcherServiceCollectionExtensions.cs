using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.Skywatcher;

namespace TianWen.Lib.Extensions;

public static class SkywatcherServiceCollectionExtensions
{
    public static IServiceCollection AddSkywatcher(this IServiceCollection services) => services.AddDevicSource<SkywatcherDevice, SkywatcherDeviceSource>(uri => new SkywatcherDevice(uri));
}
