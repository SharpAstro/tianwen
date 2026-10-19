using System;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A sequence step's rotation light (#1340): the planet's turn over the step in pixels at the disk's middle, from the ephemeris
/// before any frame, and its colour.
/// </summary>
public class PlanetaryTurnTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Night = new DateTimeOffset(2026, 10, 7, 11, 30, 0, TimeSpan.Zero);

    [Fact]
    public void SaturnAt1780MmTurnsAPixelInAboutTheOwnersFileLength()
    {
        // The owner's rig: 2 um pixels at 1780 mm, 0.2316"/px. Saturn's globe turns once in about 10.6 hours.
        const double scale = 0.2316;
        var oneFile = PlanetaryTurn.TurnPx(CatalogIndex.Saturn, Night, TimeSpan.FromSeconds(158), scale);
        var longest = PlanetaryTurn.LongestGreen(CatalogIndex.Saturn, Night, scale).ShouldNotBeNull();
        output.WriteLine($"158 s turns Saturn's middle {oneFile:F2} px; the longest green step is {longest.TotalSeconds:F0} s");

        oneFile.ShouldBeInRange(0.5, 3.0, "a 10,000-frame file at 63 fps is near a pixel's turn at this scale");
        PlanetaryTurn.TurnPx(CatalogIndex.Saturn, Night, TimeSpan.FromSeconds(316), scale).ShouldBe(oneFile * 2, oneFile * 0.02,
            "twice the time, twice the turn");
    }

    [Theory]
    [InlineData(0.4, PlanetaryTurnLight.Green)]
    [InlineData(1.0, PlanetaryTurnLight.Yellow)]
    [InlineData(4.9, PlanetaryTurnLight.Yellow)]
    [InlineData(5.0, PlanetaryTurnLight.Orange)]
    [InlineData(10.0, PlanetaryTurnLight.Red)]
    [InlineData(double.NaN, PlanetaryTurnLight.Unknown)]
    public void TheLightIsGreenUnderAPixelAndRedPastTheLimit(double turnPx, PlanetaryTurnLight expected)
        => PlanetaryTurn.LightFor(turnPx).ShouldBe(expected);

    [Fact]
    public void ATurnCannotBeSaidWithoutARotationModelAScaleOrASpan()
    {
        double.IsNaN(PlanetaryTurn.TurnPx(CatalogIndex.Venus, Night, TimeSpan.FromMinutes(2), 0.23)).ShouldBeTrue("no rotation the stack de-rotates");
        double.IsNaN(PlanetaryTurn.TurnPx(CatalogIndex.Jupiter, Night, TimeSpan.FromMinutes(2), 0)).ShouldBeTrue("no pixel scale");
        double.IsNaN(PlanetaryTurn.TurnPx(CatalogIndex.Jupiter, Night, TimeSpan.Zero, 0.23)).ShouldBeTrue("no span");
    }
}
