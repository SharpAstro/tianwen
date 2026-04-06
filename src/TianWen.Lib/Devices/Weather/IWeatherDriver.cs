using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// Driver interface for weather/observing conditions devices.
/// Property semantics match ASCOM IObservingConditions where applicable.
/// Properties return <see cref="double.NaN"/> when not supported by the hardware/service.
/// </summary>
public interface IWeatherDriver : IDeviceDriver
{
    /// <summary>Cloud cover percentage (0–100).</summary>
    double CloudCover { get; }

    /// <summary>Ambient temperature in °C.</summary>
    double Temperature { get; }

    /// <summary>Relative humidity percentage (0–100).</summary>
    double Humidity { get; }

    /// <summary>Atmospheric dew point in °C.</summary>
    double DewPoint { get; }

    /// <summary>Atmospheric pressure in hPa.</summary>
    double Pressure { get; }

    /// <summary>Wind speed in m/s.</summary>
    double WindSpeed { get; }

    /// <summary>Peak 3-second wind gust over the last 2 minutes, in m/s.</summary>
    double WindGust { get; }

    /// <summary>Wind direction in degrees (0=N, 90=E, 180=S, 270=W).</summary>
    double WindDirection { get; }

    /// <summary>Rain rate in mm/h.</summary>
    double RainRate { get; }

    /// <summary>Sky quality in mag/arcsec² (NaN if unsupported).</summary>
    double SkyQuality { get; }

    /// <summary>Sky IR temperature in °C (NaN if unsupported).</summary>
    double SkyTemperature { get; }

    /// <summary>Seeing measured as star FWHM in arcsec (NaN if unsupported).</summary>
    double StarFWHM { get; }

    /// <summary>
    /// Fetches an hourly weather forecast for the given location and time window.
    /// Used by the observation planner to overlay weather conditions on the altitude chart.
    /// </summary>
    /// <param name="latitude">Site latitude in degrees.</param>
    /// <param name="longitude">Site longitude in degrees.</param>
    /// <param name="start">Start of the forecast window.</param>
    /// <param name="end">End of the forecast window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hourly forecast entries covering the requested window.</returns>
    Task<IReadOnlyList<HourlyWeatherForecast>> GetHourlyForecastAsync(
        double latitude, double longitude,
        DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken = default);
}
