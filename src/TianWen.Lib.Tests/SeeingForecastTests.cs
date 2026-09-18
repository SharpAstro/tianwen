using System;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Devices.Weather;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The seeing ESTIMATE from the wind aloft (docs/plans/seeing-forecast.md, P1): astrophoto.app's heuristic to
/// start, a class 1..5 per hour, shown in the planner's weather band and tooltip and never taken as a
/// measurement.
/// </summary>
public class SeeingForecastTests
{
    private static readonly DateTimeOffset NightStart = new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset NightEnd = new DateTimeOffset(2026, 6, 22, 4, 0, 0, TimeSpan.FromHours(2));

    // Winds given in km/h, as the heuristic's thresholds are; the record carries m/s.
    private static HourlyWeatherForecast Hour(DateTimeOffset time, double jetKmh, double surfaceKmh = 0) =>
        new HourlyWeatherForecast(time, CloudCover: 0, Precipitation: 0, Temperature: 12, Humidity: 60, DewPoint: 4,
            WindSpeed: surfaceKmh / 3.6, WindGust: surfaceKmh / 3.6, WindDirection: 270, Visibility: 20000, WeatherCode: 0,
            WindSpeed250hPa: jetKmh / 3.6);

    [Theory]
    [InlineData(29.9, SeeingClass.Excellent)]
    [InlineData(30.0, SeeingClass.Good)]
    [InlineData(59.9, SeeingClass.Good)]
    [InlineData(60.0, SeeingClass.Average)]
    [InlineData(89.9, SeeingClass.Average)]
    [InlineData(90.0, SeeingClass.Poor)]
    [InlineData(129.9, SeeingClass.Poor)]
    [InlineData(130.0, SeeingClass.Bad)]
    [InlineData(156.0, SeeingClass.Bad)] // Munich's 250 hPa forecast on 2026-09-17, as the plan quotes it
    public void TheJetWindSetsTheClassAtTheHeuristicsThresholds(double jetKmh, SeeingClass expected)
        => SeeingForecast.For(Hour(NightStart, jetKmh)).Class.ShouldBe(expected);

    [Fact]
    public void TwiceTheSurfaceWindWinsWhenItIsWorse()
    {
        // A calm jet (20 km/h) over a 40 km/h surface wind: 2 x 40 = 80 km/h is the worse, so Average.
        var seeing = SeeingForecast.For(Hour(NightStart, jetKmh: 20, surfaceKmh: 40));

        seeing.Class.ShouldBe(SeeingClass.Average);
        seeing.WindKmh.ShouldBe(80, 1e-9);
    }

    /// <summary>
    /// With no 250 hPa wind there is no estimate. astrophoto.app substitutes three times the surface wind; this
    /// does not, because the class claims to come from the air aloft.
    /// </summary>
    [Fact]
    public void WithNoJetWindThereIsNoEstimate()
    {
        var seeing = SeeingForecast.For(Hour(NightStart, jetKmh: double.NaN, surfaceKmh: 10));

        seeing.Class.ShouldBe(SeeingClass.Unknown);
        double.IsNaN(seeing.WindKmh).ShouldBeTrue();
    }

    [Fact]
    public void TheTooltipNamesTheClassAndSaysItIsAnEstimate()
    {
        var lines = AltitudeChartRenderer.BuildWeatherTooltipLines(Hour(NightStart, jetKmh: 100), TimeSpan.FromHours(2));

        lines.ShouldContain(l => l.StartsWith("Seeing: Poor (2/5), estimated from wind", StringComparison.Ordinal));
    }

    [Fact]
    public void AnHourWithNoJetWindHasNoSeeingLine()
        => AltitudeChartRenderer.BuildWeatherTooltipLines(Hour(NightStart, jetKmh: double.NaN), TimeSpan.FromHours(2))
            .ShouldNotContain(l => l.StartsWith("Seeing", StringComparison.Ordinal));

    // --- the planner's weather band ---

    private static PlannerState State(bool withJet) => new PlannerState
    {
        AstroDark = NightStart,
        AstroTwilight = NightEnd,
        CivilSet = NightStart - TimeSpan.FromMinutes(60),
        NauticalSet = NightStart - TimeSpan.FromMinutes(30),
        NauticalRise = NightEnd + TimeSpan.FromMinutes(30),
        CivilRise = NightEnd + TimeSpan.FromMinutes(60),
        SiteLatitude = -37.8,
        SiteLongitude = 145.0,
        SiteTimeZone = TimeSpan.FromHours(10),
        MinHeightAboveHorizon = 20,
        WeatherForecast = [.. Enumerable.Range(0, 7).Select(h => Hour(NightStart.AddHours(h), withJet ? 100 : double.NaN))],
    };

    [Fact]
    public void TheBandDrawsASeeingRowOnlyWhenThereIsAJetWind()
    {
        using var withJet = new TextCapturingRenderer(800, 600);
        AltitudeChartRenderer.Render(withJet, State(withJet: true), FontResolver.ResolveSystemFont(), 0, 0, 800, 600);
        using var withoutJet = new TextCapturingRenderer(800, 600);
        AltitudeChartRenderer.Render(withoutJet, State(withJet: false), FontResolver.ResolveSystemFont(), 0, 0, 800, 600);

        withJet.Texts.ShouldContain(t => t.Text == "Seeing");
        withJet.Texts.Count(t => t.Text == "2").ShouldBeGreaterThan(0, "each hour draws its class, Poor at 100 km/h");
        withoutJet.Texts.ShouldNotContain(t => t.Text == "Seeing", "a provider with no upper-air wind gets no empty row");
    }

    /// <summary>
    /// A night that walks through every class draws every class's number, and leaves the chart in the test output
    /// directory, so the row can be LOOKED at (tianwen's profile may use a provider with no upper-air wind, where
    /// the live planner, correctly, shows no row at all).
    /// </summary>
    [Fact]
    public void ANightThroughEveryClassDrawsEachClass()
    {
        double[] jetKmh = [20, 45, 75, 110, 150, 110, 45];
        var state = State(withJet: true);
        state.WeatherForecast = [.. jetKmh.Select((jet, h) => Hour(NightStart.AddHours(h), jet))];
        using var renderer = new TextCapturingRenderer(1000, 600);

        AltitudeChartRenderer.Render(renderer, state, FontResolver.ResolveSystemFont(), 0, 0, 1000, 600);

        foreach (var digit in new[] { "1", "2", "3", "4", "5" })
        {
            renderer.Texts.ShouldContain(t => t.Text == digit, $"class {digit} is drawn");
        }
        var path = System.IO.Path.Combine(SharedTestData.CreateTempTestOutputDir(), "seeing-row.png");
        System.IO.File.WriteAllBytes(path, SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, 1000, 600));
    }

    [Fact]
    public void TheSeeingRowIsPartOfTheHoverableBand()
    {
        var seeingBand = AltitudeChartRenderer.GetWeatherBandLayout(State(withJet: true), 0, 0, 800, 600).ShouldNotBeNull();
        var plainBand = AltitudeChartRenderer.GetWeatherBandLayout(State(withJet: false), 0, 0, 800, 600).ShouldNotBeNull();

        seeingBand.BandH.ShouldBeGreaterThan(plainBand.BandH);
    }
}
