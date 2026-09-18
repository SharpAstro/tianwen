using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// Merges a freshly fetched hourly forecast with the previously cached one. Shared by every
/// <see cref="IWeatherDriver"/> that caches forecasts to disk, so the merge behaviour is identical
/// across providers.
/// </summary>
/// <remarks>
/// Forecast providers only ever return FUTURE hours, and the worst offender is OpenWeatherMap's free
/// 2.5 tier: its 3-hour blocks mean a refetch late in the evening returns a window that starts hours
/// after the observation window does (e.g. a 19:45-local fetch first covers 22:00 local). Without
/// merging, the cache overwrite would discard the early-evening hours an earlier (afternoon) session
/// captured while they were still in the future, leaving a gap at the start of the planner's weather
/// band. Merging keeps those already-captured hours so the band stays populated for the whole night
/// across refetches.
/// </remarks>
internal static class WeatherForecastMerge
{
    /// <summary>
    /// Hour-keyed union of <paramref name="cached"/> and <paramref name="fresh"/>. Fresh entries
    /// override cached entries for the same <see cref="HourlyWeatherForecast.Time"/> (an instant, so
    /// the dedup is timezone-offset agnostic), except for an upper-air wind the fresh entry lacks, which
    /// keeps the cached number (<see cref="WithUpperAirWindsFrom"/>); cached hours the fresh fetch no longer covers
    /// are retained. The result is sorted ascending by time.
    /// </summary>
    public static List<HourlyWeatherForecast> Merge(
        IReadOnlyList<HourlyWeatherForecast>? cached,
        IReadOnlyList<HourlyWeatherForecast> fresh)
    {
        // Nothing to preserve -- return the fresh set as-is (reuse the list when we own it).
        if (cached is not { Count: > 0 })
        {
            return fresh as List<HourlyWeatherForecast> ?? new List<HourlyWeatherForecast>(fresh);
        }

        // DateTimeOffset equality/hash is instant-based, so the same hour from two fetches collides
        // regardless of the stored offset. Seed with the cached hours, then let fresh win on conflict.
        var byHour = new Dictionary<DateTimeOffset, HourlyWeatherForecast>(cached.Count + fresh.Count);
        foreach (var entry in cached)
        {
            byHour[entry.Time] = entry;
        }
        foreach (var entry in fresh)
        {
            // A cache written before the pressure-level winds were requested, or a refetch whose array ran out
            // before this hour, must not blank a value the seeing forecast already had.
            byHour[entry.Time] = byHour.TryGetValue(entry.Time, out var earlier) ? WithUpperAirWindsFrom(entry, earlier) : entry;
        }

        var merged = new List<HourlyWeatherForecast>(byHour.Values);
        merged.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return merged;
    }

    /// <summary>
    /// <paramref name="primary"/> with each hour's missing upper-air winds taken from <paramref name="upperAir"/>
    /// at the same instant: a provider with no upper-air field (OpenWeatherMap) keeps every number it has and
    /// gains the seeing forecast's input from one that does (Open-Meteo). Merging per FIELD, the rule
    /// docs/plans/seeing-forecast.md states, so a NaN from either side never overwrites a number from the other.
    /// </summary>
    /// <remarks>
    /// Only the primary's hours are returned. An hour only the supplement covers is NOT added: its cloud cover
    /// would come from a different provider than the rest of the band, which is the night calendar's decision to
    /// make with provenance attached (docs/plans/night-calendar.md), not this fill's. Both providers stamp their
    /// hours on the UTC hour, so the same instant is the match.
    /// </remarks>
    public static List<HourlyWeatherForecast> FillUpperAirWinds(
        IReadOnlyList<HourlyWeatherForecast> primary,
        IReadOnlyList<HourlyWeatherForecast> upperAir)
    {
        var byHour = new Dictionary<DateTimeOffset, HourlyWeatherForecast>(upperAir.Count);
        foreach (var entry in upperAir)
        {
            byHour[entry.Time] = entry;
        }

        var filled = new List<HourlyWeatherForecast>(primary.Count);
        foreach (var entry in primary)
        {
            filled.Add(byHour.TryGetValue(entry.Time, out var other) ? WithUpperAirWindsFrom(entry, other) : entry);
        }
        return filled;
    }

    /// <summary>
    /// <paramref name="entry"/>, except that an upper-air wind it lacks (NaN) takes <paramref name="other"/>'s
    /// number: merging per FIELD, not per entry. The surface fields are left as they were.
    /// </summary>
    private static HourlyWeatherForecast WithUpperAirWindsFrom(HourlyWeatherForecast entry, HourlyWeatherForecast other)
        => entry with
        {
            WindSpeed250hPa = double.IsNaN(entry.WindSpeed250hPa) ? other.WindSpeed250hPa : entry.WindSpeed250hPa,
            WindSpeed500hPa = double.IsNaN(entry.WindSpeed500hPa) ? other.WindSpeed500hPa : entry.WindSpeed500hPa,
            WindSpeed850hPa = double.IsNaN(entry.WindSpeed850hPa) ? other.WindSpeed850hPa : entry.WindSpeed850hPa,
        };
}
