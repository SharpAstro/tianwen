using System;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Astrometry")]
public class RiseTransitSetHelperTests
{
    // Sydney Observatory — southern hemisphere, always-visible-ish targets for coverage.
    private const double SydneyLatDeg = -33.8688;
    private const double SydneyLonDeg = 151.2093;

    // Standard mid-northern observer — Greenwich, for easy sanity-checking against
    // published ephemeris (pick a dec that transits well above horizon from London).
    private const double GreenwichLatDeg = 51.4769;
    private const double GreenwichLonDeg = 0.0;

    [Fact]
    public void VegaFromSydneyRisesAndSetsOnTheSameNight()
    {
        // Vega J2000: RA 18h 36m 56s, Dec +38 deg 47'
        var raHours = 18.0 + 36.0 / 60.0 + 56.0 / 3600.0;
        var decDeg = 38.0 + 47.0 / 60.0;

        // Pick a known summer night. From Sydney, Vega grazes the northern horizon.
        var nearUtc = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var ok = RiseTransitSetHelper.TryComputeRiseTransitSet(
            raHours, decDeg, SydneyLatDeg, SydneyLonDeg, nearUtc,
            out var rise, out var transit, out var set,
            out var circumpolar, out var neverRises);

        ok.ShouldBeTrue();
        circumpolar.ShouldBeFalse();
        neverRises.ShouldBeFalse();

        // Rise must precede transit must precede set.
        rise.ShouldBeLessThan(transit);
        transit.ShouldBeLessThan(set);

        // Events should all fall within +/- 24h of the reference.
        Math.Abs((transit - nearUtc).TotalHours).ShouldBeLessThan(24);
    }

    [Fact]
    public void PolarisIsCircumpolarFromGreenwich()
    {
        // Polaris: RA 2h 31m 49s, Dec +89 deg 15' — always above horizon in mid-northern latitudes.
        var raHours = 2.0 + 31.0 / 60.0 + 49.0 / 3600.0;
        var decDeg = 89.0 + 15.0 / 60.0;

        var nearUtc = new DateTimeOffset(2026, 3, 21, 0, 0, 0, TimeSpan.Zero);

        var ok = RiseTransitSetHelper.TryComputeRiseTransitSet(
            raHours, decDeg, GreenwichLatDeg, GreenwichLonDeg, nearUtc,
            out _, out _, out _,
            out var circumpolar, out var neverRises);

        ok.ShouldBeTrue();
        circumpolar.ShouldBeTrue();
        neverRises.ShouldBeFalse();
    }

    [Fact]
    public void AlphaCrucisNeverRisesFromGreenwich()
    {
        // Alpha Crucis (Acrux): Dec ~ -63 deg — never rises from London (lat 51.5 deg N).
        // For the object to rise we need |dec - lat| < 90, i.e. dec > -(90 - 51.5) = -38.5.
        var raHours = 12.45;
        var decDeg = -63.0;

        var nearUtc = new DateTimeOffset(2026, 3, 21, 0, 0, 0, TimeSpan.Zero);

        var ok = RiseTransitSetHelper.TryComputeRiseTransitSet(
            raHours, decDeg, GreenwichLatDeg, GreenwichLonDeg, nearUtc,
            out _, out _, out _,
            out var circumpolar, out var neverRises);

        ok.ShouldBeTrue();
        circumpolar.ShouldBeFalse();
        neverRises.ShouldBeTrue();
    }

    [Fact]
    public void InvalidInputsReturnFalse()
    {
        var ok = RiseTransitSetHelper.TryComputeRiseTransitSet(
            raHours: 0, decDeg: 0,
            siteLatDeg: double.NaN, siteLonDeg: 0,
            nearUtc: DateTimeOffset.UtcNow,
            out _, out _, out _, out _, out _);

        ok.ShouldBeFalse();
    }

    [Fact]
    public void TransitIsWithinHalfDayAndRiseSetAreSymmetric()
    {
        // Transit must be within +/- 12h of the reference (nearest event semantics)
        // and rise/set must be symmetric around transit.
        var raHours = 10.0;
        var decDeg = 20.0;
        var nearUtc = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

        var ok = RiseTransitSetHelper.TryComputeRiseTransitSet(
            raHours, decDeg, GreenwichLatDeg, GreenwichLonDeg, nearUtc,
            out var rise, out var transit, out var set, out _, out _);

        ok.ShouldBeTrue();
        Math.Abs((transit - nearUtc).TotalHours).ShouldBeLessThanOrEqualTo(12.01);

        // Rise and set are symmetric around transit for a fixed-position star.
        var toRise = (transit - rise).TotalHours;
        var toSet = (set - transit).TotalHours;
        Math.Abs(toRise - toSet).ShouldBeLessThan(1e-6);
    }

    [Fact]
    public void TransitSolvesLstEqualsRa()
    {
        // Functional check: at the computed transit time, LST should equal RA to
        // within one sidereal second (formula is 1-second GMST).
        var raHours = 7.5;
        var decDeg = 10.0;
        var nearUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        RiseTransitSetHelper.TryComputeRiseTransitSet(
            raHours, decDeg, GreenwichLatDeg, GreenwichLonDeg, nearUtc,
            out _, out var transit, out _, out _, out _)
            .ShouldBeTrue();

        var lstAtTransit = SiteContext.ComputeLST(transit, GreenwichLonDeg);
        // LST wraps at 24h — compare modulo.
        var diff = Math.Abs(((lstAtTransit - raHours) % 24.0 + 24.0) % 24.0);
        if (diff > 12.0) diff = 24.0 - diff;
        diff.ShouldBeLessThan(0.01); // ~36 sidereal seconds, well under the helper's accuracy budget
    }
}
