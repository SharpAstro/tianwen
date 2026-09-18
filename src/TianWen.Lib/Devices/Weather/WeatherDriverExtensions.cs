using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Weather;

public static class WeatherDriverExtensions
{
    extension(IWeatherDriver driver)
    {
        /// <summary>
        /// The driver's hourly forecast, with the upper-air winds filled in from Open-Meteo where the provider has
        /// no upper-air field, so the planner's seeing estimate is there whichever forecast the profile uses
        /// (docs/plans/seeing-forecast.md). The provider's own numbers always win; only its missing winds are
        /// filled (<see cref="WeatherForecastMerge.FillUpperAirWinds"/>).
        /// </summary>
        /// <remarks>
        /// OpenWeatherMap is the provider this is for: it has no upper-air field at all, where Open-Meteo is
        /// keyless and already carries all three levels. Other drivers pass through untouched: Open-Meteo has the
        /// winds itself, and the fake and hardware drivers have no forecast worth supplementing (a test must not
        /// reach the network through one). Open-Meteo's own file cache keeps this to one request per hour. A
        /// supplement that fails returns nothing, which leaves the forecast as the provider gave it.
        /// </remarks>
        public async Task<IReadOnlyList<HourlyWeatherForecast>> GetHourlyForecastWithUpperAirAsync(
            IServiceProvider serviceProvider,
            double latitude, double longitude,
            DateTimeOffset start, DateTimeOffset end,
            CancellationToken cancellationToken = default)
        {
            var forecast = await driver.GetHourlyForecastAsync(latitude, longitude, start, end, cancellationToken);
            if (driver is not OpenWeatherMapDriver || forecast.Count == 0)
            {
                return forecast;
            }

            using var openMeteo = new OpenMeteoDriver(new OpenMeteoDevice(), serviceProvider);
            var upperAir = await openMeteo.GetHourlyForecastAsync(latitude, longitude, start, end, cancellationToken);
            return upperAir.Count > 0 ? WeatherForecastMerge.FillUpperAirWinds(forecast, upperAir) : forecast;
        }
    }
}
