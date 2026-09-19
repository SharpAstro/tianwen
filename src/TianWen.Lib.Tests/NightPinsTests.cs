using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The pinned pointings on one night (docs/plans/night-calendar.md, P3): each one's window and hours, and the
/// union the night's strip draws. Glen Waverley again, where the calendar was asked for.
/// </summary>
public class NightPinsTests
{
    private const double Latitude = -37.877;
    private const double Longitude = 145.178;
    private const double Elevation = 120;
    private const byte MinAltitude = 20;

    // Circumpolar from the site: at declination -80 it never drops below 28 degrees.
    private static readonly Target South = new Target(1.0, -80.0, "South", null);

    // Never rises there: at +60 its best altitude is below the horizon.
    private static readonly Target North = new Target(12.0, 60.0, "North", null);

    private static NightSummary Night(DateOnly evening) => NightSummary.Compute(new Transform(SystemTimeProvider.Instance)
    {
        SiteLatitude = Latitude,
        SiteLongitude = Longitude,
        SiteElevation = Elevation,
        SiteTemperature = 15,
        DateTimeOffset = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.FromHours(10)),
    }, evening);

    private static NightPins Pins(NightSummary night, IReadOnlyList<Target> pointings,
        IReadOnlyList<HourlyWeatherForecast>? forecast = null)
        => NightPins.Compute(Latitude, Longitude, Elevation, night, pointings, MinAltitude, forecast);

    private static List<HourlyWeatherForecast> Hours(NightSummary night, Func<DateTimeOffset, double> cloud)
    {
        var first = new DateTimeOffset(night.DarkStart.UtcDateTime.Date.AddHours(night.DarkStart.UtcDateTime.Hour), TimeSpan.Zero);
        var hours = new List<HourlyWeatherForecast>();
        for (var t = first.AddHours(-1); t <= night.DarkEnd.AddHours(1); t = t.AddHours(1))
        {
            hours.Add(new HourlyWeatherForecast(t, CloudCover: cloud(t), Precipitation: 0, Temperature: 10,
                Humidity: 60, DewPoint: 3, WindSpeed: 2, WindGust: 4, WindDirection: 270, Visibility: 20000,
                WeatherCode: 0));
        }
        return hours;
    }

    [Fact]
    public void ACircumpolarPinIsUsableTheWholeDarkWindow()
    {
        var night = Night(new DateOnly(2026, 9, 19));

        var pins = Pins(night, [South]);

        var south = pins.Pointings.ShouldHaveSingleItem();
        south.Located.ShouldBeTrue();
        south.From.ShouldBe(night.DarkStart);
        south.To.ShouldBe(night.DarkEnd);
        south.Up.ShouldBe(night.Dark);
        pins.Usable.ShouldBe(night.Dark);
        pins.Timeline.ShouldAllBe(c => c == PinCoverage.Unforecast, "no forecast was given");
    }

    [Fact]
    public void APinThatNeverRisesHasNoWindowAndLeavesTheNightEmpty()
    {
        var pins = Pins(Night(new DateOnly(2026, 9, 19)), [North]);

        var north = pins.Pointings.ShouldHaveSingleItem();
        north.From.ShouldBeNull();
        north.Up.ShouldBe(TimeSpan.Zero);
        pins.Usable.ShouldBe(TimeSpan.Zero);
        pins.Timeline.ShouldAllBe(c => c == PinCoverage.None);
    }

    [Fact]
    public void TheNightsUsableTimeIsTheUnionNotTheSum()
    {
        var night = Night(new DateOnly(2026, 9, 19));

        var pins = Pins(night, [South, South, North]);

        pins.Pointings.Length.ShouldBe(3);
        pins.Usable.ShouldBe(night.Dark, "one mount: two pins up at once is still one night's worth");
    }

    [Fact]
    public void TheForecastSplitsAPinsHoursIntoClearAndCloudy()
    {
        var night = Night(new DateOnly(2026, 9, 19));
        var split = night.DarkStart + (night.Dark / 2);
        split = new DateTimeOffset(split.UtcDateTime.Date.AddHours(split.UtcDateTime.Hour), TimeSpan.Zero);

        var pins = Pins(night, [South], Hours(night, t => t < split ? 5 : 90));

        var south = pins.Pointings.ShouldHaveSingleItem();
        south.Forecast.ShouldBe(night.Dark);
        south.Clear.ShouldBe(split - night.DarkStart, NightPins.SampleStep, "clear until the cloud arrives, to a sample");
        pins.Timeline.First().ShouldBe(PinCoverage.Clear);
        pins.Timeline.Last().ShouldBe(PinCoverage.Cloudy);
        pins.UsableClear.ShouldBe(south.Clear);
    }

    [Fact]
    public void APinnedPlanetIsPlacedOnEachNightNotFromItsStoredPosition()
    {
        // Saturn is near opposition in early October 2026. The object database stores a planet at NaN, which is
        // exactly what a pin restored from it carries.
        var saturn = new Target(double.NaN, double.NaN, "Saturn", CatalogIndex.Saturn);

        var pins = Pins(Night(new DateOnly(2026, 9, 25)), [saturn]);

        var placed = pins.Pointings.ShouldHaveSingleItem();
        placed.Located.ShouldBeTrue();
        placed.Up.ShouldBeGreaterThan(TimeSpan.FromHours(4), "an opposition planet is up most of the night");
    }

    [Fact]
    public void APinnedCometWithNoRepositoryIsNotPlaced()
    {
        CometDesignation.TryParse("10P", out var designation).ShouldBeTrue();
        designation.TryToCatalogIndex(out var index).ShouldBeTrue();
        var comet = new Target(double.NaN, double.NaN, "10P/Tempel", index);

        var pins = Pins(Night(new DateOnly(2026, 9, 19)), [comet]);

        pins.Pointings.ShouldHaveSingleItem().Located.ShouldBeFalse();
        pins.Usable.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void TheMoonsClosestApproachIsMeasuredWhileBothAreUp()
    {
        // A pin placed where the Moon is in the middle of a nearly full night.
        var night = Night(new DateOnly(2026, 9, 26));
        var middle = night.DarkStart + (night.Dark / 2);
        VSOP87a.ReduceJ2000(CatalogIndex.Moon, middle, out var ra, out var dec, out _).ShouldBeTrue();

        var pins = Pins(night, [new Target(ra, dec, "Beside the Moon", null), South]);

        pins.Pointings[0].MinMoonSeparationDeg.ShouldBeLessThan(5, "the Moon passes over its position");
        pins.Pointings[1].MinMoonSeparationDeg.ShouldBeGreaterThan(40, "the far south is nowhere near it");
    }
}
