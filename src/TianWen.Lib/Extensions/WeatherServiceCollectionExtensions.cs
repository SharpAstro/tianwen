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

    /// <summary>
    /// Registers the OpenWeatherMap weather device source and URI factory.
    /// Devices are discovered when the <c>OWM_API_KEY</c> environment variable is set.
    /// </summary>
    public static IServiceCollection AddOpenWeatherMap(this IServiceCollection services) => services
        .AddDevicSource<OpenWeatherMapDevice, OpenWeatherMapDeviceSource>(uri => new OpenWeatherMapDevice(uri));
}
