using System;
using System.Collections.Generic;
using TianWen.Lib.Devices.Weather;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// An hourly forecast looked up by the instant a sample falls in, and the ONE rule for a clear hour: the night
/// verdict and the pinned targets' clear hours both read it here, so they cannot disagree about which hours were
/// clear (docs/plans/night-calendar.md).
/// </summary>
/// <remarks>
/// Every provider states its hours on the UTC hour, each covering the hour after it, so the key is the UTC hour an
/// instant falls in. Only an hour with a KNOWN cloud cover is returned: the far end of Open-Meteo's range is null,
/// read as NaN, and an hour nobody forecast is not a cloudy one.
/// </remarks>
internal sealed class HourlyForecastIndex
{
    private readonly Dictionary<long, HourlyWeatherForecast> _byHour;

    private HourlyForecastIndex(Dictionary<long, HourlyWeatherForecast> byHour) => _byHour = byHour;

    /// <summary>The entries that can matter to <paramref name="start"/> to <paramref name="end"/>, or null when none do.</summary>
    public static HourlyForecastIndex? For(IReadOnlyList<HourlyWeatherForecast>? forecast, DateTimeOffset start,
        DateTimeOffset end)
    {
        if (forecast is not { Count: > 0 })
        {
            return null;
        }

        var from = start.AddHours(-1);
        Dictionary<long, HourlyWeatherForecast>? byHour = null;
        foreach (var entry in forecast)
        {
            if (entry.Time >= from && entry.Time < end)
            {
                (byHour ??= [])[HourKey(entry.Time)] = entry;
            }
        }
        return byHour is null ? null : new HourlyForecastIndex(byHour);
    }

    /// <summary>The forecast hour <paramref name="instant"/> falls in, when one was stated with a cloud cover.</summary>
    public bool TryGetKnown(DateTimeOffset instant, out HourlyWeatherForecast hour)
        => _byHour.TryGetValue(HourKey(instant), out hour) && double.IsFinite(hour.CloudCover);

    /// <summary>The hour's precipitation in mm, an unstated one read as none.</summary>
    public static double Rain(in HourlyWeatherForecast hour)
        => double.IsFinite(hour.Precipitation) ? hour.Precipitation : 0.0;

    /// <summary>Whether a known hour is clear: <see cref="NightSummary.ClearCloudCoverPercent"/> and no rain.</summary>
    public static bool IsClear(in HourlyWeatherForecast hour)
        => hour.CloudCover < NightSummary.ClearCloudCoverPercent && Rain(hour) < NightSummary.NoPrecipitationMmPerHour;

    private static long HourKey(DateTimeOffset instant)
    {
        var ticks = instant.UtcTicks;
        return ticks - (ticks % TimeSpan.TicksPerHour);
    }
}
