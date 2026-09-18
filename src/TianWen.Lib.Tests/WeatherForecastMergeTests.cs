using System;
using System.Collections.Generic;
using Shouldly;
using TianWen.Lib.Devices.Weather;
using Xunit;

namespace TianWen.Lib.Tests;

public class WeatherForecastMergeTests
{
    private static HourlyWeatherForecast Hour(int utcHour, double humidity) =>
        new HourlyWeatherForecast(
            Time: new DateTimeOffset(2026, 6, 15, utcHour, 0, 0, TimeSpan.Zero),
            CloudCover: 50, Precipitation: 0, Temperature: 10, Humidity: humidity,
            DewPoint: 5, WindSpeed: 2, WindGust: 4, WindDirection: 180, Visibility: 10000,
            WeatherCode: 2, PrecipitationProbability: 0);

    [Fact]
    public void Merge_NoCache_ReturnsFreshUnchanged()
    {
        var fresh = new List<HourlyWeatherForecast> { Hour(12, 80), Hour(13, 79) };

        var merged = WeatherForecastMerge.Merge(null, fresh);

        merged.ShouldBe(fresh);
    }

    [Fact]
    public void Merge_EmptyCache_ReturnsFreshUnchanged()
    {
        var fresh = new List<HourlyWeatherForecast> { Hour(12, 80) };

        var merged = WeatherForecastMerge.Merge(new List<HourlyWeatherForecast>(), fresh);

        merged.ShouldBe(fresh);
    }

    [Fact]
    public void Merge_RetainsEarlierHoursTheRefetchNoLongerCovers()
    {
        // An afternoon session captured 08:00-11:00 UTC; the evening refetch is future-only and
        // returns 12:00+ only. The merge must keep the early hours so the band has no gap.
        var cached = new List<HourlyWeatherForecast> { Hour(8, 90), Hour(9, 88), Hour(10, 85), Hour(11, 83) };
        var fresh = new List<HourlyWeatherForecast> { Hour(12, 81), Hour(13, 80), Hour(14, 79) };

        var merged = WeatherForecastMerge.Merge(cached, fresh);

        merged.Count.ShouldBe(7); // 4 cached + 3 fresh, all distinct hours
        merged[0].Time.Hour.ShouldBe(8);
        merged[^1].Time.Hour.ShouldBe(14);
        for (var i = 1; i < merged.Count; i++)
        {
            merged[i].Time.ShouldBeGreaterThan(merged[i - 1].Time); // ascending, no duplicates
        }
    }

    [Fact]
    public void Merge_FreshOverridesCachedForSameHour()
    {
        var cached = new List<HourlyWeatherForecast> { Hour(12, 90) }; // stale value for 12:00
        var fresh = new List<HourlyWeatherForecast> { Hour(12, 81) };  // refreshed value for 12:00

        var merged = WeatherForecastMerge.Merge(cached, fresh);

        merged.Count.ShouldBe(1);
        merged[0].Humidity.ShouldBe(81); // fresh wins on conflict
    }

    [Fact]
    public void Merge_SameInstantDifferentOffset_IsDeduped()
    {
        // 22:00+10:00 and 12:00 UTC are the same instant; DateTimeOffset keying is instant-based,
        // so they must collapse to a single entry (fresh wins) rather than appear twice.
        var cached = new List<HourlyWeatherForecast>
        {
            new HourlyWeatherForecast(
                new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.FromHours(10)),
                50, 0, 10, 90, 5, 2, 4, 180, 10000, 2, 0)
        };
        var fresh = new List<HourlyWeatherForecast> { Hour(12, 81) };

        var merged = WeatherForecastMerge.Merge(cached, fresh);

        merged.Count.ShouldBe(1);
        merged[0].Humidity.ShouldBe(81);
    }

    // --- the upper-air winds merge per FIELD (the seeing forecast's input) ---

    [Fact]
    public void Merge_FreshWithoutUpperAirWinds_KeepsTheCachedWinds()
    {
        // The cache holds winds for 12:00; a refetch whose pressure-level array ran out (or a cache/driver
        // from before the winds were requested) brings the hour back without them.
        var cached = new List<HourlyWeatherForecast> { Hour(12, 90) with { WindSpeed250hPa = 40, WindSpeed500hPa = 20, WindSpeed850hPa = 8 } };
        var fresh = new List<HourlyWeatherForecast> { Hour(12, 81) };

        var merged = WeatherForecastMerge.Merge(cached, fresh).ShouldHaveSingleItem();

        merged.Humidity.ShouldBe(81, "the surface fields still take the fresh value");
        merged.WindSpeed250hPa.ShouldBe(40);
        merged.WindSpeed500hPa.ShouldBe(20);
        merged.WindSpeed850hPa.ShouldBe(8);
    }

    [Fact]
    public void Merge_FreshUpperAirWindsStillWin()
    {
        var cached = new List<HourlyWeatherForecast> { Hour(12, 90) with { WindSpeed250hPa = 40, WindSpeed500hPa = 20, WindSpeed850hPa = 8 } };
        var fresh = new List<HourlyWeatherForecast> { Hour(12, 81) with { WindSpeed250hPa = 44, WindSpeed500hPa = double.NaN, WindSpeed850hPa = 9 } };

        var merged = WeatherForecastMerge.Merge(cached, fresh).ShouldHaveSingleItem();

        merged.WindSpeed250hPa.ShouldBe(44);
        merged.WindSpeed500hPa.ShouldBe(20, "only the MISSING field falls back to the cache");
        merged.WindSpeed850hPa.ShouldBe(9);
    }
}
