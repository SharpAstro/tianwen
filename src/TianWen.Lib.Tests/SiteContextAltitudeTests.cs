using Shouldly;
using System;
using TianWen.Lib.Astrometry.SOFA;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="SiteContext.AltitudeDegrees"/>, the geometric altitude the mechanical horizon
/// limit is evaluated against.
/// </summary>
/// <remarks>
/// Geometric, not refracted, and that is the whole reason this exists rather than reusing a
/// <c>Transform</c>: refraction lifts a body by up to ~34 arcmin at the horizon, so a refracted
/// altitude reports the tube higher than it is and a limit keyed on it fires late -- in exactly the
/// regime where late is the failure.
/// </remarks>
[Collection("Astrometry")]
public class SiteContextAltitudeTests
{
    private const double Lat = 48.2;
    private const double Lon = 16.3;

    private static readonly DateTimeOffset Epoch = new(2026, 6, 15, 22, 0, 0, TimeSpan.Zero);

    private static SiteContext At(double latitude) => SiteContext.Create(latitude, Lon, Epoch);

    [Fact]
    public void AtUpperTransitAltitudeIsNinetyMinusTheZenithDistance()
    {
        // HA = 0 puts the object on the meridian, where alt = 90 - |lat - dec|.
        At(Lat).AltitudeDegrees(0.0, Lat).ShouldBe(90.0, tolerance: 1e-9);
        At(Lat).AltitudeDegrees(0.0, Lat - 30.0).ShouldBe(60.0, tolerance: 1e-9);
    }

    [Fact]
    public void AtLowerTransitTheObjectIsAsLowAsItGets()
    {
        // HA = 12h is lower transit: alt = |lat| + dec - 90 for a northern site.
        At(Lat).AltitudeDegrees(12.0, Lat).ShouldBe(2.0 * Lat - 90.0, tolerance: 1e-9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(-3.0)]
    [InlineData(11.5)]
    public void TheCelestialPoleSitsAtTheSiteLatitudeWhateverTheHourAngle(double hourAngle)
    {
        // Dec = +90 is the pole, whose altitude equals the latitude and does not move. A good
        // shape check: it exercises the cos(HA) term while the answer must stay constant.
        At(Lat).AltitudeDegrees(hourAngle, 90.0).ShouldBe(Lat, tolerance: 1e-9);
    }

    [Fact]
    public void TheSouthernHemisphereIsNotAMirrorOfTheNorthernOne()
    {
        // Same declination, opposite latitude: the pole altitude follows the site, so a southern
        // site sees the NORTH pole below its horizon. Guards against a stray Math.Abs on latitude.
        At(-37.5).AltitudeDegrees(0.0, 90.0).ShouldBe(-37.5, tolerance: 1e-9);
    }

    [Fact]
    public void AnUnknownSiteOrPointingIsUnknownAndNotZero()
    {
        // "Unknown" must never read as "at the horizon" -- MountLimits treats NaN as not-evaluable
        // precisely so a failed driver read cannot park the mount mid-target.
        SiteContext.Create(double.NaN, Lon, Epoch).AltitudeDegrees(0.0, 45.0).ShouldBe(double.NaN);
        At(Lat).AltitudeDegrees(double.NaN, 45.0).ShouldBe(double.NaN);
        At(Lat).AltitudeDegrees(0.0, double.NaN).ShouldBe(double.NaN);
    }

    [Fact]
    public void TheResultStaysInRangeAtTheClampBoundary()
    {
        // sin(alt) can exceed 1 by a rounding crumb when the object is exactly overhead; without
        // the clamp Math.Asin returns NaN, which the limit would then read as "not evaluable" and
        // silently disable itself at the one pointing it is most sure about.
        var alt = At(90.0).AltitudeDegrees(0.0, 90.0);
        double.IsNaN(alt).ShouldBeFalse();
        alt.ShouldBe(90.0, tolerance: 1e-9);
    }

    // SiteContext.Airmass is the one air-mass computation the dataset reports use, per master
    // (gradient report) and per sub (the archive's FWHM-against-airmass measurement), so its
    // geometry is pinned here beside the altitude it is derived from.

    [Fact]
    public void AirmassAtTheZenithIsOneAndAtSixtyDegreesZenithDistanceIsTwo()
    {
        // A target on the meridian (RA = LST) at the site's own declination is overhead; sixty
        // degrees south of that on the meridian sits at altitude 30, where sec z is exactly 2.
        var lst = SiteContext.ComputeLST(Epoch, Lon);
        SiteContext.Airmass(Epoch, Lat, Lon, lst, Lat).ShouldBe(1.0, tolerance: 1e-6);
        SiteContext.Airmass(Epoch, Lat, Lon, lst, Lat - 60.0).ShouldBe(2.0, tolerance: 1e-6);
    }

    [Fact]
    public void AirmassBelowTheHorizonIsUnknownNotClamped()
    {
        // The clamp at five degrees is for a target barely up; a target ten degrees UNDER the horizon
        // has no air mass, and reporting the clamped 11.5 there would read as a legitimate, very deep
        // observation in a per-sub table.
        var lst = SiteContext.ComputeLST(Epoch, Lon);
        SiteContext.Airmass(Epoch, Lat, Lon, lst, Lat - 100.0).ShouldBe(double.NaN);
        SiteContext.AirmassFromAltitude(2.0).ShouldBe(SiteContext.AirmassFromAltitude(5.0));
        SiteContext.AirmassFromAltitude(5.0).ShouldBe(1.0 / Math.Sin(5.0 * Math.PI / 180.0), tolerance: 1e-12);
    }

    [Fact]
    public void AirmassWithAnyUnknownInputIsUnknown()
    {
        var lst = SiteContext.ComputeLST(Epoch, Lon);
        SiteContext.Airmass(Epoch, double.NaN, Lon, lst, Lat).ShouldBe(double.NaN);
        SiteContext.Airmass(Epoch, Lat, double.NaN, lst, Lat).ShouldBe(double.NaN);
        SiteContext.Airmass(Epoch, Lat, Lon, double.NaN, Lat).ShouldBe(double.NaN);
        SiteContext.Airmass(Epoch, Lat, Lon, lst, double.NaN).ShouldBe(double.NaN);
        // A header with no DATE-OBS parses to an epoch far in the past; that is "unknown", not year 1.
        SiteContext.Airmass(new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero), Lat, Lon, lst, Lat).ShouldBe(double.NaN);
    }
}
