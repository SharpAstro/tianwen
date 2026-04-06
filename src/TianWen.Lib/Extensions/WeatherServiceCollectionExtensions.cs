using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.Weather;

namespace TianWen.Lib.Extensions;

public static class WeatherServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Open-Meteo weather device source and URI factory.
    /// </summary>
    public static IServiceCollection AddOpenMeteo(this IServiceCollection services) => services
        .AddDevicSource<OpenMeteoDevice, OpenMeteoDeviceSource>(uri => new OpenMeteoDevice(uri));
}
