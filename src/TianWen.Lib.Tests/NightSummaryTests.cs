using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// One night at a site, summarised for choosing WHICH night to image, and the one verdict read off it
/// (docs/plans/night-calendar.md). The site is Glen Waverley, the rig the calendar was asked for.
/// </summary>
public class NightSummaryTests(ITestOutputHelper output)
{
    private const double Latitude = -37.877;
    private const double Longitude = 145.178;

    private static Transform SiteTransform() => new Transform(SystemTimeProvider.Instance)
    {
        SiteLatitude = Latitude,
        SiteLongitude = Longitude,
        SiteElevation = 120,
        SiteTemperature = 15,
        DateTimeOffset = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.FromHours(10)),
    };

    /// <summary>Hourly entries on the UTC hour from an hour before <paramref name="from"/> to an hour after
    /// <paramref name="to"/>, each stating what <paramref name="hour"/> says for its instant.</summary>
    private static List<HourlyWeatherForecast> Hours(DateTimeOffset from, DateTimeOffset to,
        Func<DateTimeOffset, (double Cloud, double Rain)> hour)
    {
        var first = new DateTimeOffset(from.UtcDateTime.Date.AddHours(from.UtcDateTime.Hour), TimeSpan.Zero).AddHours(-1);
        var hours = new List<HourlyWeatherForecast>();
        for (var t = first; t <= to.AddHours(1); t = t.AddHours(1))
        {
            var (cloud, rain) = hour(t);
            hours.Add(new HourlyWeatherForecast(t, CloudCover: cloud, Precipitation: rain, Temperature: 10,
                Humidity: 60, DewPoint: 3, WindSpeed: 2, WindGust: 4, WindDirection: 270, Visibility: 20000,
                WeatherCode: 0, WindSpeed250hPa: 10));
        }
        return hours;
    }

    /// <summary>September 2026's nights, and the ones the Moon decides: the fullest and the darkest.</summary>
    private (NightSummary Full, NightSummary New) FullAndNewMoonNights()
    {
        var transform = SiteTransform();
        var sw = Stopwatch.StartNew();
        var nights = Enumerable.Range(0, 30)
            .Select(i => NightSummary.Compute(transform, new DateOnly(2026, 9, 1).AddDays(i)))
            .ToList();
        output.WriteLine($"30 nights summarised in {sw.ElapsedMilliseconds} ms");
        return (nights.MaxBy(n => n.MoonIllumination), nights.MinBy(n => n.MoonIllumination));
    }

    [Fact]
    public void ANightIsNamedByTheEveningItBeginsOn()
    {
        var night = NightSummary.Compute(SiteTransform(), new DateOnly(2026, 9, 19));

        DateOnly.FromDateTime(night.DarkStart.DateTime).ShouldBe(new DateOnly(2026, 9, 19), "dark falls on the evening named");
        night.DarkStart.Hour.ShouldBeGreaterThanOrEqualTo(18, "site-local evening");
        DateOnly.FromDateTime(night.DarkEnd.DateTime).ShouldBe(new DateOnly(2026, 9, 20), "and ends the next morning");
        night.DarkBoundary.ShouldBe(EventType.AmateurAstronomicalTwilight, "a Melbourne spring night reaches the first boundary tried");

        // The planner's own night for that date, the way its recompute asks for a pinned date: at local noon.
        var planner = SiteTransform();
        planner.DateTimeOffset = new DateTimeOffset(2026, 9, 19, 12, 0, 0, planner.SiteTimeZone);
        var (dark, twilight) = ObservationScheduler.CalculateNightWindow(planner);
        night.DarkStart.ShouldBe(dark, "the calendar's night is the planner's night");
        night.DarkEnd.ShouldBe(twilight);
    }

    [Fact]
    public void TheMoonDecidesADarkNightFromAMoonlitOneWithNoForecast()
    {
        var (full, dark) = FullAndNewMoonNights();

        full.MoonIllumination.ShouldBeGreaterThan(0.95);
        full.MoonFreeDark.TotalHours.ShouldBeLessThan(0.25 * full.Dark.TotalHours, "a full Moon is up all night");
        NightVerdict.For(full).Outlook.ShouldBe(NightOutlook.Moonlit);

        dark.MoonIllumination.ShouldBeLessThan(0.05);
        dark.MoonFreeDark.TotalHours.ShouldBeGreaterThan(0.75 * dark.Dark.TotalHours, "a new Moon sets with the Sun");
        NightVerdict.For(dark).Outlook.ShouldBe(NightOutlook.Dark);

        full.Forecast.ShouldBeNull("no forecast was given");
    }

    [Fact]
    public void AClearMoonlessNightIsAGoAndAnOvercastOneANoGo()
    {
        var (_, newMoon) = FullAndNewMoonNights();
        var transform = SiteTransform();

        var clear = NightSummary.Compute(transform, newMoon.EveningDate,
            Hours(newMoon.DarkStart, newMoon.DarkEnd, _ => (5, 0)));
        clear.Forecast.ShouldNotBeNull().ClearDark.ShouldBe(clear.Dark, TimeSpan.FromSeconds(1));
        NightVerdict.For(clear).Outlook.ShouldBe(NightOutlook.Go);

        var overcast = NightSummary.Compute(transform, newMoon.EveningDate,
            Hours(newMoon.DarkStart, newMoon.DarkEnd, _ => (90, 0)));
        overcast.Forecast.ShouldNotBeNull().ClearDark.ShouldBe(TimeSpan.Zero);
        overcast.Forecast.Value.MeanCloudCover.ShouldBe(90, 1e-9);
        NightVerdict.For(overcast).Outlook.ShouldBe(NightOutlook.NoGo);
    }

    [Fact]
    public void TheForecastIsReadHourByHourAcrossTheNight()
    {
        var (_, newMoon) = FullAndNewMoonNights();

        // Clear until an hour in the middle of the night, overcast after it.
        var split = newMoon.DarkStart + (newMoon.Dark / 2);
        split = new DateTimeOffset(split.UtcDateTime.Date.AddHours(split.UtcDateTime.Hour), TimeSpan.Zero);
        var night = NightSummary.Compute(SiteTransform(), newMoon.EveningDate,
            Hours(newMoon.DarkStart, newMoon.DarkEnd, t => t < split ? (5, 0) : (90, 0)));

        night.Forecast.ShouldNotBeNull().ClearDark.ShouldBe(split - newMoon.DarkStart, NightSummary.Step,
            "clear up to the hour the cloud arrives, to the resolution of one sample");
    }

    [Fact]
    public void RainMakesACloudlessHourNotClear()
    {
        var (_, newMoon) = FullAndNewMoonNights();

        var night = NightSummary.Compute(SiteTransform(), newMoon.EveningDate,
            Hours(newMoon.DarkStart, newMoon.DarkEnd, _ => (5, 0.5)));

        night.Forecast.ShouldNotBeNull().ClearDark.ShouldBe(TimeSpan.Zero);
        night.Forecast.Value.Precipitation.ShouldBe(0.5 * night.Dark.TotalHours, 0.01);
    }

    [Fact]
    public void AForecastThatStopsEarlyGivesTheMoonAloneNotAWeatherVerdict()
    {
        var (_, newMoon) = FullAndNewMoonNights();

        // Two clear hours at the start of the night, then the horizon.
        var night = NightSummary.Compute(SiteTransform(), newMoon.EveningDate,
            Hours(newMoon.DarkStart, newMoon.DarkStart.AddHours(1), _ => (5, 0)));

        night.Forecast.ShouldNotBeNull().Covered.ShouldBeLessThan(newMoon.Dark * NightVerdict.MinForecastCoverage);
        var verdict = NightVerdict.For(night);
        verdict.IsForecast.ShouldBeFalse("a forecast for a quarter of the night says nothing about the rest");
        verdict.Outlook.ShouldBe(NightOutlook.Dark);
    }

    private static NightSummary Night(double darkHours, double illumination, double moonFreeHours,
        NightForecast? forecast = null)
    {
        var start = new DateTimeOffset(2026, 9, 19, 20, 0, 0, TimeSpan.FromHours(10));
        return new NightSummary(new DateOnly(2026, 9, 19), start, start.AddHours(darkHours),
            EventType.AmateurAstronomicalTwilight, illumination, true, TimeSpan.FromHours(moonFreeHours), forecast);
    }

    private static NightForecast Forecast(double coveredHours, double clearHours, double clearMoonFreeHours,
        SeeingClass seeing = SeeingClass.Good)
        => new NightForecast(TimeSpan.FromHours(coveredHours), TimeSpan.FromHours(clearHours),
            TimeSpan.FromHours(clearMoonFreeHours), 10, 0, seeing);

    [Fact]
    public void AShortNightIsAGoOnMostOfItsDarknessNotOnThreeHours()
    {
        var verdict = NightVerdict.For(Night(2, 0, 2, Forecast(2, 1.3, 1.3)));

        verdict.Outlook.ShouldBe(NightOutlook.Go, "1.3 of 2 hours is most of a high-latitude summer night");
    }

    [Fact]
    public void BadSeeingTakesAGoToMarginal()
    {
        NightVerdict.For(Night(9, 0, 9, Forecast(9, 8, 8, SeeingClass.Poor))).Outlook.ShouldBe(NightOutlook.Go);
        NightVerdict.For(Night(9, 0, 9, Forecast(9, 8, 8, SeeingClass.Bad))).Outlook.ShouldBe(NightOutlook.Marginal);
    }

    [Fact]
    public void AClearNightUnderAFullMoonIsMarginalNotNoGo()
    {
        var verdict = NightVerdict.For(Night(10, 1.0, 0, Forecast(10, 10, 0)));

        verdict.Outlook.ShouldBe(NightOutlook.Marginal, "narrowband and the Moon's own targets still work under it");
        verdict.UsefulDark.ShouldBe(TimeSpan.FromHours(10 * NightVerdict.MoonlitFloor));
    }

    [Fact]
    public void AThinCrescentCostsAlmostNothing()
    {
        var verdict = NightVerdict.For(Night(9, 0.05, 0, Forecast(9, 9, 0)));

        verdict.Outlook.ShouldBe(NightOutlook.Go);
        verdict.UsefulDark.TotalHours.ShouldBe(9 * 0.95, 1e-9);
    }
}
