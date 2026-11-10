using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary><see cref="SkyCovariates"/> (#1974): the frame's pointing and Moon from its header.</summary>
public class SkyCovariatesTests
{
    private static ImageMeta Meta(DateTimeOffset when, double lat, double lon, double raHours, double decDeg)
        => new ImageMeta() with { ExposureStartTime = when, Latitude = (float)lat, Longitude = (float)lon, TargetRA = raHours, TargetDec = decDeg };

    [Theory]
    [InlineData(-37.876, 145.178, 3.0)]
    [InlineData(-37.876, 145.178, -2.0)]
    [InlineData(50.9, 6.0, 3.0)]
    [InlineData(50.9, 6.0, -2.0)]
    public void TheHourAngleSideAndGeometryMatchTheSite(double lat, double lon, double hourAngle)
    {
        // The header stores the site as a float, so the expectation is made on the same rounded site.
        lat = (float)lat;
        lon = (float)lon;
        var when = new DateTimeOffset(2026, 7, 6, 15, 30, 0, TimeSpan.Zero);
        var lst = SiteContext.ComputeLST(when, lon);
        // A target hourAngle hours past the meridian has RA = LST - HA, hand-set rather than derived by the code under test.
        var ra = ((lst - hourAngle) % 24.0 + 24.0) % 24.0;
        var dec = lat > 0 ? 20.0 : -30.0;

        var c = SkyCovariates.From(Meta(when, lat, lon, ra, dec));

        c.HourAngleHours.ShouldBe(hourAngle, 1e-6);
        c.West.ShouldBe(hourAngle > 0);
        c.AltitudeDeg.ShouldBe(SiteContext.AltitudeDegrees(lat, hourAngle, dec), 1e-4);
        c.AzimuthDeg.ShouldBe(SiteContext.AzimuthDegrees(lat, hourAngle, dec), 1e-4);
        c.Airmass.ShouldBe(SiteContext.AirmassFromAltitude(c.AltitudeDeg), 1e-9);
        c.AltitudeDeg.ShouldBeLessThan(90);
    }

    [Theory]
    [InlineData(-37.876)]
    [InlineData(50.9)]
    public void ATargetOnTheMeridianAtTheLatitudeIsAtTheZenith(double lat)
    {
        var when = new DateTimeOffset(2026, 7, 6, 15, 30, 0, TimeSpan.Zero);
        var lst = SiteContext.ComputeLST(when, 10.0);

        var c = SkyCovariates.From(Meta(when, lat, 10.0, lst, lat));

        c.AltitudeDeg.ShouldBe(90.0, 1e-3);
        c.Airmass.ShouldBe(1.0, 1e-3);
        c.West.ShouldBeFalse();
    }

    [Fact]
    public void HoursFromLocalMidnightFollowTheLongitude()
    {
        // Longitude 0: UTC is local mean time. Longitude 145.178 is 9.679 h ahead, so 14:00 UTC is 23:41 local, 19 minutes before midnight.
        SkyCovariates.From(Meta(new DateTimeOffset(2026, 7, 6, 0, 30, 0, TimeSpan.Zero), 0, 0, 1, 0)).HoursFromLocalMidnight.ShouldBe(0.5, 1e-9);
        SkyCovariates.From(Meta(new DateTimeOffset(2026, 7, 6, 14, 0, 0, TimeSpan.Zero), -37.876, 145.178, 1, 0)).HoursFromLocalMidnight
            .ShouldBe(14.0 + (float)145.178 / 15.0 - 24.0, 1e-6);
        SkyCovariates.From(Meta(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero), 0, 0, 1, 0)).HoursFromLocalMidnight.ShouldBe(-12.0, 1e-6);
    }

    [Fact]
    public void TheMoonIsReadFromTheEpochAndSite()
    {
        // The full Moon of 2026-07-29 is up around local midnight; a new Moon (2026-07-14) is dim.
        var full = SkyCovariates.From(Meta(new DateTimeOffset(2026, 7, 29, 14, 0, 0, TimeSpan.Zero), -37.876, 145.178, 20.0, -20.0));
        var fresh = SkyCovariates.From(Meta(new DateTimeOffset(2026, 7, 14, 14, 0, 0, TimeSpan.Zero), -37.876, 145.178, 20.0, -20.0));

        full.MoonIllumination.ShouldBeGreaterThan(0.9);
        fresh.MoonIllumination.ShouldBeLessThan(0.1);
        full.MoonSeparationDeg.ShouldBeInRange(0.0, 180.0);
        full.MoonAltitudeDeg.ShouldBeInRange(-90.0, 90.0);
        full.MoonUp.ShouldBe(full.MoonAltitudeDeg > 0);
    }

    [Fact]
    public void AHeaderWithoutTimeSiteOrPointingGivesNaN()
    {
        var noTime = SkyCovariates.From(Meta(default, -37.876, 145.178, 3.0, -30.0));
        noTime.AltitudeDeg.ShouldBe(double.NaN);
        noTime.HoursFromLocalMidnight.ShouldBe(double.NaN);
        noTime.MoonAltitudeDeg.ShouldBe(double.NaN);

        var noSite = SkyCovariates.From(Meta(new DateTimeOffset(2026, 7, 6, 15, 30, 0, TimeSpan.Zero), double.NaN, double.NaN, 3.0, -30.0));
        noSite.AltitudeDeg.ShouldBe(double.NaN);
        noSite.Airmass.ShouldBe(double.NaN);
        noSite.MoonIllumination.ShouldBe(double.NaN);

        var noPointing = SkyCovariates.From(Meta(new DateTimeOffset(2026, 7, 6, 15, 30, 0, TimeSpan.Zero), -37.876, 145.178, double.NaN, double.NaN));
        noPointing.AltitudeDeg.ShouldBe(double.NaN);
        noPointing.MoonSeparationDeg.ShouldBe(double.NaN);
        noPointing.MoonIllumination.ShouldBeGreaterThanOrEqualTo(0.0);
    }
}
