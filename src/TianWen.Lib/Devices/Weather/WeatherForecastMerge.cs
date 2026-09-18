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
    /// keeps the cached number (<see cref="KeepKnownWinds"/>); cached hours the fresh fetch no longer covers
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
            byHour[entry.Time] = byHour.TryGetValue(entry.Time, out var earlier) ? KeepKnownWinds(entry, earlier) : entry;
        }

        var merged = new List<HourlyWeatherForecast>(byHour.Values);
        merged.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return merged;
    }

    /// <summary>
    /// The fresh entry for an hour, except that an upper-air wind it lacks (NaN) keeps the cached number:
    /// merging per FIELD, not per entry. A cache written before the pressure-level winds were requested, or a
    /// refetch whose array ran out before this hour, must not blank a value the seeing forecast already had.
    /// The surface fields are left as they were: fresh wins outright, as it always has.
    /// </summary>
    private static HourlyWeatherForecast KeepKnownWinds(HourlyWeatherForecast fresh, HourlyWeatherForecast cached)
        => fresh with
        {
            WindSpeed250hPa = double.IsNaN(fresh.WindSpeed250hPa) ? cached.WindSpeed250hPa : fresh.WindSpeed250hPa,
            WindSpeed500hPa = double.IsNaN(fresh.WindSpeed500hPa) ? cached.WindSpeed500hPa : fresh.WindSpeed500hPa,
            WindSpeed850hPa = double.IsNaN(fresh.WindSpeed850hPa) ? cached.WindSpeed850hPa : fresh.WindSpeed850hPa,
        };
}
