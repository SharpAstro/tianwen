using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// An OpenWeatherMap profile has no upper-air wind, so without a supplement the planner's seeing row never
/// appears for it (the user's own rig, 2026-09-19). <see cref="WeatherDriverExtensions"/> fills the winds, and the
/// boundary-layer (mixing) height beside them, from Open-Meteo. Driven through the REAL drivers, each answering from a fresh file cache, so the wiring is what is
/// tested and nothing reaches the network.
/// </summary>
public class WeatherUpperAirSupplementTests(ITestOutputHelper output)
{
    private const double Latitude = -37.88;
    private const double Longitude = 145.18;

    // 18:00 to 06:00 at the site (UTC+10), the planner's window for one night.
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(12);

    private static HourlyWeatherForecast Hour(int offsetHours, double cloudCover, double jetMs, double mixingHeight = double.NaN) =>
        new HourlyWeatherForecast(Start.AddHours(offsetHours), CloudCover: cloudCover, Precipitation: 0, Temperature: 10,
            Humidity: 60, DewPoint: 3, WindSpeed: 2, WindGust: 4, WindDirection: 270, Visibility: 20000, WeatherCode: 0,
            WindSpeed250hPa: jetMs, BoundaryLayerHeight: mixingHeight);

    /// <summary>
    /// Writes a forecast where the driver will look for it, stamped at the fake clock's now so it reads as FRESH:
    /// a stale one would send the driver to the network. The file name is the drivers' own, culture and all.
    /// </summary>
    private static void WriteFreshCache(IExternal external, string prefix, List<HourlyWeatherForecast> hours)
    {
        var dir = external.CreateSubDirectoryInAppDataFolder("Weather");
        var startDate = Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDate = End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Path.Combine(dir.FullName, $"{prefix}{Latitude:F2}_{Longitude:F2}_{startDate}_{endDate}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(hours, OpenMeteoJsonContext.Default.ListHourlyWeatherForecast));
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
    }

    [Fact]
    public async Task AnOpenWeatherMapForecastGainsTheJetWindFromOpenMeteo()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: Now);
        var sp = external.BuildServiceProvider();

        // OpenWeatherMap's night: its own cloud cover, no upper-air wind. Open-Meteo's: a different cloud cover
        // and the jet wind, which is the only thing that may cross.
        WriteFreshCache(external, "owm_", [.. Enumerable.Range(0, 3).Select(h => Hour(h, cloudCover: 20, jetMs: double.NaN))]);
        WriteFreshCache(external, "", [.. Enumerable.Range(0, 3).Select(h => Hour(h, cloudCover: 90, jetMs: 30 + h, mixingHeight: 300 - 10 * h))]);

        using var owm = new OpenWeatherMapDriver(new OpenWeatherMapDevice(), sp, apiKey: "unused");
        var forecast = await owm.GetHourlyForecastWithUpperAirAsync(sp, Latitude, Longitude, Start, End, ct);

        forecast.Count.ShouldBe(3);
        forecast.Select(h => h.WindSpeed250hPa).ShouldBe([30.0, 31.0, 32.0]);
        forecast.Select(h => h.BoundaryLayerHeight).ShouldBe([300.0, 290.0, 280.0], "the mixing height crosses with the winds");
        forecast.ShouldAllBe(h => h.CloudCover == 20, "OpenWeatherMap's own cloud cover stays");
        SeeingForecast.For(forecast[0]).Class.ShouldNotBe(SeeingClass.Unknown, "the seeing row now has an input");
    }

    [Fact]
    public async Task AnOpenMeteoForecastPassesThroughUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: Now);
        var sp = external.BuildServiceProvider();
        WriteFreshCache(external, "", [.. Enumerable.Range(0, 3).Select(h => Hour(h, cloudCover: 90, jetMs: 30 + h))]);

        using var openMeteo = new OpenMeteoDriver(new OpenMeteoDevice(), sp);
        var forecast = await openMeteo.GetHourlyForecastWithUpperAirAsync(sp, Latitude, Longitude, Start, End, ct);

        forecast.Select(h => h.WindSpeed250hPa).ShouldBe([30.0, 31.0, 32.0]);
        forecast.ShouldAllBe(h => h.CloudCover == 90);
    }

    /// <summary>The night calendar's request: the whole range at once, cached under the range's own dates.</summary>
    private static void WriteFreshRangeCache(IExternal external, string prefix, IEnumerable<HourlyWeatherForecast> hours)
    {
        var (start, end) = ExtendedForecast.RangeFor(Now);
        var dir = external.CreateSubDirectoryInAppDataFolder("Weather");
        var startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Path.Combine(dir.FullName, $"{prefix}{Latitude:F2}_{Longitude:F2}_{startDate}_{endDate}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(hours.ToList(), OpenMeteoJsonContext.Default.ListHourlyWeatherForecast));
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
    }

    [Fact]
    public async Task AnOpenWeatherMapCalendarRunsOnIntoOpenMeteoPastItsFortyEightHours()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: Now);
        var sp = external.BuildServiceProvider();
        var (start, end) = ExtendedForecast.RangeFor(Now);
        var hours = (int)(end - start).TotalHours + 1;

        HourlyWeatherForecast At(int h, double cloud, double jet) =>
            new HourlyWeatherForecast(start.AddHours(h), CloudCover: cloud, Precipitation: 0, Temperature: 10,
                Humidity: 60, DewPoint: 3, WindSpeed: 2, WindGust: 4, WindDirection: 270, Visibility: 20000,
                WeatherCode: 0, WindSpeed250hPa: jet);

        // OpenWeatherMap: two days from now, its own cloud, no jet. Open-Meteo: the whole range.
        var owmFirst = (int)(Now - start).TotalHours;
        WriteFreshRangeCache(external, "owm_", Enumerable.Range(owmFirst, 48).Select(h => At(h, cloud: 20, jet: double.NaN)));
        WriteFreshRangeCache(external, "", Enumerable.Range(0, hours).Select(h => At(h, cloud: 90, jet: 30)));

        using var owm = new OpenWeatherMapDriver(new OpenWeatherMapDevice(), sp, apiKey: "unused");
        var forecast = await owm.GetExtendedHourlyForecastAsync(sp, Latitude, Longitude, start, end, ct);

        forecast.Provider.ShouldBe("OpenWeatherMap");
        forecast.SupplementProvider.ShouldBe("Open-Meteo");
        forecast.SupplementedFrom.ShouldBe(start.AddHours(owmFirst + 48), "Open-Meteo takes over after OpenWeatherMap's last hour");
        forecast.Hours.Where(h => h.Time < forecast.SupplementedFrom).ShouldAllBe(h => h.CloudCover == 20 && h.WindSpeed250hPa == 30);
        forecast.Hours.Where(h => h.Time >= forecast.SupplementedFrom).ShouldAllBe(h => h.CloudCover == 90);
        forecast.Hours[^1].Time.ShouldBe(end, "to the last hour Open-Meteo answers");
        forecast.Hours.ShouldNotContain(h => h.Time < start.AddHours(owmFirst),
            "an hour before OpenWeatherMap's first is its to have dropped, not Open-Meteo's to fill");
    }

    [Fact]
    public async Task AnOpenMeteoCalendarIsOneProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: Now);
        var sp = external.BuildServiceProvider();
        var (start, end) = ExtendedForecast.RangeFor(Now);
        WriteFreshRangeCache(external, "", Enumerable.Range(0, 24).Select(h => Hour(h, cloudCover: 50, jetMs: 30)));

        using var openMeteo = new OpenMeteoDriver(new OpenMeteoDevice(), sp);
        var forecast = await openMeteo.GetExtendedHourlyForecastAsync(sp, Latitude, Longitude, start, end, ct);

        forecast.Provider.ShouldBe("Open-Meteo");
        forecast.SupplementedFrom.ShouldBeNull();
        forecast.Hours.Count.ShouldBe(24);
    }
}
