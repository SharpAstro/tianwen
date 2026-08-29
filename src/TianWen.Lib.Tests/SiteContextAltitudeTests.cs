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
}
