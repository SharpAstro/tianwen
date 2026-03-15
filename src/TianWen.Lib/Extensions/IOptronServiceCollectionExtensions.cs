using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.IOptron;

namespace TianWen.Lib.Extensions;

public static class IOptronServiceCollectionExtensions
{
    public static IServiceCollection AddIOptron(this IServiceCollection services) => services.AddDevicSource<IOptronDevice, IOptronDeviceSource>(uri => new IOptronDevice(uri));
}
