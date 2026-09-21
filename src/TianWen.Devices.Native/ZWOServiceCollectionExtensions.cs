using TianWen.Lib.Devices;
using TianWen.Lib.Devices.ZWO;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class ZWOServiceCollectionExtensions
{
    public static IServiceCollection AddZWO(this IServiceCollection services) => services.AddDevicSource<ZWODevice, ZWODeviceSource>(uri => new ZWODevice(uri));
}