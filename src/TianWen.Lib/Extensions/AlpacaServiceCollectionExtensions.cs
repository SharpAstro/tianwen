using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Alpaca;

namespace TianWen.Lib.Extensions;

public static class AlpacaServiceCollectionExtensions
{
    public static IServiceCollection AddAlpaca(this IServiceCollection services)
    {
        services.AddHttpClient<AlpacaClient>();
        return services.AddDevicSource<AlpacaDevice, AlpacaDeviceSource>(uri => new AlpacaDevice(uri));
    }
}
