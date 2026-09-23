using TianWen.Lib.Devices;
using TianWen.Lib.Devices.ToupTek;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class ToupTekServiceCollectionExtensions
{
    public static IServiceCollection AddToupTek(this IServiceCollection services)
        => services.AddDevicSource<ToupTekDevice, ToupTekDeviceSource>(uri => new ToupTekDevice(uri));
}
