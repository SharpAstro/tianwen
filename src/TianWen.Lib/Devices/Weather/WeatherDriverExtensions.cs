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
        /// The driver's hourly forecast, with the upper-air fields (the pressure-level winds and the boundary-layer
        /// height) filled in from Open-Meteo where the provider has none, so the planner's seeing estimate is there
        /// whichever forecast the profile uses (docs/plans/seeing-forecast.md). The provider's own numbers always
        /// win; only its missing fields are filled (<see cref="WeatherForecastMerge.FillUpperAir"/>).
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
            return upperAir.Count > 0 ? WeatherForecastMerge.FillUpperAir(forecast, upperAir) : forecast;
        }

        /// <summary>
        /// The forecast behind the night calendar: the whole of <see cref="ExtendedForecast.RangeFor"/> in one
        /// request per provider, with the profile's own provider winning every hour it covers and Open-Meteo
        /// supplying the hours after it (<see cref="WeatherForecastMerge.Extend"/>), upper-air fields included.
        /// </summary>
        /// <remarks>
        /// An Open-Meteo profile needs nothing else: it already covers the range. OpenWeatherMap stops at 48 hours,
        /// so days 3 to 16 come from Open-Meteo, and the result says from when. Any other driver (the fake, a
        /// hardware station) passes through untouched, so a test never reaches the network through one.
        /// </remarks>
        public async Task<ExtendedForecast> GetExtendedHourlyForecastAsync(
            IServiceProvider serviceProvider,
            double latitude, double longitude,
            DateTimeOffset start, DateTimeOffset end,
            CancellationToken cancellationToken = default)
        {
            var forecast = await driver.GetHourlyForecastAsync(latitude, longitude, start, end, cancellationToken);
            if (driver is not OpenWeatherMapDriver)
            {
                return new ExtendedForecast(forecast, driver.Name, null, null);
            }

            using var openMeteo = new OpenMeteoDriver(new OpenMeteoDevice(), serviceProvider);
            var supplement = await openMeteo.GetHourlyForecastAsync(latitude, longitude, start, end, cancellationToken);
            if (supplement.Count == 0)
            {
                return new ExtendedForecast(forecast, driver.Name, null, null);
            }

            var extended = WeatherForecastMerge.Extend(forecast, supplement, out var supplementFrom);
            return forecast.Count == 0
                ? new ExtendedForecast(extended, openMeteo.Name, null, null)
                : new ExtendedForecast(extended, driver.Name, supplementFrom, supplementFrom is null ? null : openMeteo.Name);
        }
    }
}

/// <summary>
/// A multi-day hourly forecast and where its hours came from: <see cref="Provider"/> up to
/// <see cref="SupplementedFrom"/>, and <see cref="SupplementProvider"/> from then on.
/// </summary>
/// <param name="Hours">The hourly entries, ascending.</param>
/// <param name="Provider">Who stated the hours before <see cref="SupplementedFrom"/> (all of them, when null).</param>
/// <param name="SupplementedFrom">The first hour taken from <see cref="SupplementProvider"/>, or null.</param>
/// <param name="SupplementProvider">Who stated the hours from <see cref="SupplementedFrom"/> on, or null.</param>
public sealed record ExtendedForecast(
    IReadOnlyList<HourlyWeatherForecast> Hours,
    string Provider,
    DateTimeOffset? SupplementedFrom,
    string? SupplementProvider)
{
    /// <summary>
    /// Open-Meteo answers at most this many days after the current UTC date; one day more is a 400 ("out of
    /// allowed range"), measured 2026-09-19. It is the horizon of every forecast the calendar can show.
    /// </summary>
    public const int ForecastDaysAhead = 15;

    /// <summary>
    /// The range to ask for: from the start of yesterday (UTC), so tonight's evening is covered in every time zone,
    /// to the end of the last day Open-Meteo accepts. Both ends are UTC, because the drivers write the request's
    /// dates from the offsets they are given.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) RangeFor(DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return (today.AddDays(-1), today.AddDays(ForecastDaysAhead + 1).AddHours(-1));
    }

    /// <summary>Who stated the forecast for the hours from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public string SourceFor(DateTimeOffset from, DateTimeOffset to)
        => SupplementedFrom is not { } split || SupplementProvider is not { } other || to <= split ? Provider
            : from >= split ? other
            : $"{Provider}, then {other}";
}
