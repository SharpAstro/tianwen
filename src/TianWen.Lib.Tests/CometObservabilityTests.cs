using System;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Exercises the comet observability planner (the engine behind the MCP <c>catalog.lookup</c> "best time
/// to observe" answer). Uses 12P/Pons-Brooks across its 2024 apparition (perihelion 2024-04-21): the scan
/// must track the ephemeris brightening toward perihelion and keep the Observable flag consistent with the
/// altitude gate. Real-clock-independent -- the scan sets the transform's instant per night from the
/// supplied start, so the injected time provider is never consulted.
/// </summary>
[Collection("Catalog")]
public class CometObservabilityTests
{
    private static readonly CometElements PonsBrooks = new(
        Designation: Parse("12P"),
        CommonName: "Pons-Brooks",
        PerihelionDistanceAu: 0.7808611331423883,
        Eccentricity: 0.9545612442767357,
        InclinationDeg: 74.19091017013747,
        AscendingNodeDeg: 255.8553510995133,
        ArgumentOfPerihelionDeg: 198.9879994677832,
        PerihelionJdTt: 2460421.631159499004,
        EpochJdTt: 2460211.5,
        AbsoluteMagnitudeM1: 5.0,
        SlopeK1: 15.0);

    [Fact]
    public void GivenCometWhenScannedAcrossApparitionThenSamplesTrackBrighteningAndFlagsAreConsistent()
    {
        var transform = SiteTransform(latitude: 30.0, longitude: 0.0);
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var window = CometObservability.Scan(PonsBrooks, transform, start, sampleCount: 30, stepDays: 7.0, minAltitudeDeg: 0.0);

        // Most weeks solve (allow a couple of high-latitude/no-dark drops, though this site has dark nights).
        window.Nights.Length.ShouldBeGreaterThan(25);
        window.BestIndex.ShouldBeInRange(0, window.Nights.Length - 1);

        var minMag = double.MaxValue;
        foreach (var n in window.Nights)
        {
            // The Observable flag is exactly the altitude gate.
            n.Observable.ShouldBe(n.MaxAltitudeDeg >= 0.0);
            // Best time lands inside its own dark window.
            n.BestTimeUtc.ShouldBeGreaterThanOrEqualTo(n.DarkStartUtc);
            n.BestTimeUtc.ShouldBeLessThanOrEqualTo(n.DarkEndUtc);
            if (n.VMag < minMag) minMag = n.VMag;
        }

        // The comet brightens by several magnitudes toward its 2024-04-21 perihelion; the brightest sampled
        // night must be materially brighter than the (far-from-perihelion) Jan 1 start.
        minMag.ShouldBeLessThan(window.Nights[0].VMag - 2.0);
    }

    [Fact]
    public void GivenCometWhenFindingBestThenItReturnsANightInRangeAndNearTheBrightnessPeak()
    {
        var transform = SiteTransform(latitude: 30.0, longitude: 0.0);
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        CometObservability.TryFindBest(PonsBrooks, transform, start, minAltitudeDeg: 0.0,
            out var best, out var samples, coarseWeeks: 26, coarseStepDays: 7.0).ShouldBeTrue();

        samples.Length.ShouldBeGreaterThan(20);

        // The recommended night sits within the scanned span (coarse weeks + the +/-1-week fine refinement).
        best.BestTimeUtc.ShouldBeGreaterThanOrEqualTo(start.AddDays(-8));
        best.BestTimeUtc.ShouldBeLessThanOrEqualTo(start.AddDays(26 * 7 + 8));

        // With no altitude gate (minAlt 0) the pick is brightness-driven: the recommended vmag is at least
        // as bright as the brightest coarse weekly sample (the nightly refine can only improve on it).
        var brightestCoarse = double.MaxValue;
        foreach (var n in samples)
        {
            if (n.VMag < brightestCoarse) brightestCoarse = n.VMag;
        }
        best.VMag.ShouldBeLessThanOrEqualTo(brightestCoarse + 0.01);
    }

    private static Transform SiteTransform(double latitude, double longitude)
        => new(new SystemTimeProvider())
        {
            SiteLatitude = latitude,
            SiteLongitude = longitude,
            SiteElevation = 0.0,
            SiteTemperature = 15.0,
            DateTimeOffset = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

    private static CometDesignation Parse(string s)
    {
        CometDesignation.TryParse(s, out var d).ShouldBeTrue();
        return d;
    }
}
