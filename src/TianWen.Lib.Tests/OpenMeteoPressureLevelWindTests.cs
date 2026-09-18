using System;
using System.Collections.Generic;
using System.Text.Json;
using Shouldly;
using TianWen.Lib.Devices.Weather;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Open-Meteo's hourly pressure-level winds, the seeing forecast's input (docs/plans/seeing-forecast.md):
/// read from the response in km/h, carried in m/s like the surface wind, and a null for an hour past a level's
/// horizon read as "no value" rather than failing the whole response.
/// </summary>
public class OpenMeteoPressureLevelWindTests
{
    // The shape of a real response for three hours, trimmed to the fields the parse reads. The 250 hPa values
    // are the first three Munich hours measured on 2026-09-17; the 500 hPa array ends in a null.
    private const string Response = """
        {
          "hourly": {
            "time": ["2026-09-17T18:00", "2026-09-17T19:00", "2026-09-17T20:00"],
            "cloud_cover": [10, 20, 30],
            "precipitation": [0, 0, 0],
            "precipitation_probability": [0, 5, 10],
            "temperature_2m": [15.0, 14.0, 13.0],
            "relative_humidity_2m": [60, 65, 70],
            "dew_point_2m": [7.0, 7.5, 7.8],
            "wind_speed_10m": [7.2, 3.6, 0],
            "wind_gusts_10m": [14.4, 10.8, 7.2],
            "wind_direction_10m": [270, 260, 250],
            "visibility": [24000, 24000, 20000],
            "weather_code": [0, 1, 2],
            "wind_speed_250hPa": [141.0, 153.0, 156.0],
            "wind_speed_500hPa": [72.0, 64.8, null],
            "wind_speed_850hPa": [18.0, 21.6, 25.2]
          }
        }
        """;

    private static List<HourlyWeatherForecast> Parse()
    {
        var response = JsonSerializer.Deserialize(Response, OpenMeteoJsonContext.Default.OpenMeteoResponse)
            .ShouldNotBeNull("a null inside a pressure-level array must not fail the response");
        return OpenMeteoDriver.ParseHourlyData(response.Hourly,
            new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ThePressureLevelWindsAreReadInMetresPerSecond()
    {
        var hours = Parse();

        hours.Count.ShouldBe(3);
        hours[0].WindSpeed250hPa.ShouldBe(141.0 / 3.6, 1e-9);
        hours[1].WindSpeed500hPa.ShouldBe(64.8 / 3.6, 1e-9);
        hours[2].WindSpeed850hPa.ShouldBe(25.2 / 3.6, 1e-9);
        hours[0].WindSpeed.ShouldBe(2.0, 1e-9, "the surface wind was, and stays, m/s too");
    }

    [Fact]
    public void ANullForAnHourIsNoValue()
        => double.IsNaN(Parse()[2].WindSpeed500hPa).ShouldBeTrue();

    /// <summary>
    /// A forecast cache written before the winds were requested has no such fields; it must read back with
    /// them absent (NaN), not as zero wind, which the seeing forecast would read as a perfect night.
    /// </summary>
    [Fact]
    public void ACacheFromBeforeTheWindsReadsThemAsMissingNotAsCalm()
    {
        const string oldCache = """
            [{"time":"2026-09-17T18:00:00+00:00","cloud_cover":10,"precipitation":0,"temperature":15,"humidity":60,
              "dew_point":7,"wind_speed":2,"wind_gust":4,"wind_direction":270,"visibility":24000,"weather_code":0,
              "precipitation_probability":0}]
            """;

        var hour = JsonSerializer.Deserialize(oldCache, OpenMeteoJsonContext.Default.ListHourlyWeatherForecast)
            .ShouldNotBeNull().ShouldHaveSingleItem();

        hour.CloudCover.ShouldBe(10);
        double.IsNaN(hour.WindSpeed250hPa).ShouldBeTrue($"read back as {hour.WindSpeed250hPa}");
        double.IsNaN(hour.WindSpeed500hPa).ShouldBeTrue($"read back as {hour.WindSpeed500hPa}");
        double.IsNaN(hour.WindSpeed850hPa).ShouldBeTrue($"read back as {hour.WindSpeed850hPa}");
    }

    [Fact]
    public void TheWindsSurviveTheCacheRoundTrip()
    {
        var hours = Parse();

        var json = JsonSerializer.Serialize(hours, OpenMeteoJsonContext.Default.ListHourlyWeatherForecast);
        var back = JsonSerializer.Deserialize(json, OpenMeteoJsonContext.Default.ListHourlyWeatherForecast).ShouldNotBeNull();

        back[0].WindSpeed250hPa.ShouldBe(hours[0].WindSpeed250hPa);
        double.IsNaN(back[2].WindSpeed500hPa).ShouldBeTrue("NaN is written as a named literal and read back as NaN");
    }
}
