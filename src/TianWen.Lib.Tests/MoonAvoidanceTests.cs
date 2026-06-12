using System;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Moon-avoidance scoring: a bright, above-horizon Moon close to a target should reduce
/// that target's altitude score (illumination x quadratic proximity), while a faint/absent
/// Moon, a below-horizon Moon, or a far-away target must leave the score untouched.
/// </summary>
[Collection("Scheduling")]
public sealed class MoonAvoidanceTests
{
    // Vienna, Austria - matches ObservationSchedulerTests.
    private const double SiteLatitude = 48.2;
    private const double SiteLongitude = 16.4;
    private const byte MinHeight = 20;

    // Winter full Moon: illumination 1.0, Moon above the horizon the whole night and riding
    // to ~69 deg altitude (RA ~4.7h, Dec ~27 deg). The one season where a full Moon is high
    // from a mid-northern site, so a target glued to it is also well above the horizon.
    private static readonly DateTimeOffset WinterFullMoonEvening =
        new DateTimeOffset(2025, 12, 4, 20, 0, 0, TimeSpan.FromHours(1));

    // Summer night used for the white-box (synthetic-grid) tests, where M13 rides high.
    private static readonly DateTimeOffset SummerEvening =
        new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.FromHours(2));

    // M13 - Hercules cluster, high in summer from Vienna.
    private static readonly Target M13 = new Target(16.695, 36.46, "M13", null);

    private static Transform CreateTransform(DateTimeOffset when)
        => new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = SiteLatitude,
            SiteLongitude = SiteLongitude,
            SiteElevation = 200,
            SiteTemperature = 5,
            DateTimeOffset = when
        };

    /// <summary>Moon RA/Dec/illumination at the grid midpoint for the given night.</summary>
    private static (double RaHours, double DecDeg, double Illumination, int Bins) MoonAtMidnight(
        Transform transform, DateTimeOffset astroDark, DateTimeOffset astroTwilight)
    {
        var (astroms, times) = ObservationScheduler.PrecomputeAstromGrid(
            astroDark, astroTwilight, transform.SiteLatitude, transform.SiteLongitude, transform.SiteElevation);
        var grid = ObservationScheduler.PrecomputeMoonGrid(times, astroms, ObservationScheduler.DefaultMoonAvoidanceRadiusDeg);
        var mid = times.Length / 2;
        return (grid.RaHours[mid], grid.DecDeg[mid], grid.Illumination, times.Length);
    }

    /// <summary>Synthetic grid placing the Moon at a fixed (ra, dec) for every bin.</summary>
    private static ObservationScheduler.MoonGrid SyntheticMoonGrid(
        int bins, double raHours, double decDeg, bool aboveHorizon, double illumination, double radiusDeg = 30.0)
    {
        var ra = new double[bins];
        var dec = new double[bins];
        var above = new bool[bins];
        for (var i = 0; i < bins; i++)
        {
            ra[i] = raHours;
            dec[i] = decDeg;
            above[i] = aboveHorizon;
        }
        return new ObservationScheduler.MoonGrid(ra, dec, above, illumination, radiusDeg);
    }

    // ---- Black-box: real winter-full-Moon night ----------------------------------------

    [Fact]
    public void ScoreTarget_TargetNearBrightMoon_ScoreHeavilyReduced()
    {
        var transform = CreateTransform(WinterFullMoonEvening);
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);
        var (moonRa, moonDec, illumination, _) = MoonAtMidnight(transform, astroDark, astroTwilight);
        illumination.ShouldBeGreaterThan(0.9, "Dec 4 2025 should be a (near-)full Moon");

        // Target sitting right on top of the Moon's midnight position; it stays within a few
        // degrees of the Moon all night (Moon drifts ~4 deg over a 12h window).
        var near = new Target(moonRa, moonDec, "near-moon", null);

        var withMoon = ObservationScheduler.ScoreTarget(near, transform, astroDark, astroTwilight, MinHeight,
            ObjectType.Unknown, moonAvoidanceRadiusDeg: 30.0);
        var withoutMoon = ObservationScheduler.ScoreTarget(near, transform, astroDark, astroTwilight, MinHeight,
            ObjectType.Unknown, moonAvoidanceRadiusDeg: 0.0);

        ((double)withoutMoon.TotalScore).ShouldBeGreaterThan(0.0, "the target itself is well above the horizon");
        ((double)withMoon.TotalScore).ShouldBeGreaterThan(0.0, "penalty reduces but does not zero a high target");
        ((double)withMoon.TotalScore).ShouldBeLessThan(0.3 * (double)withoutMoon.TotalScore,
            "a full Moon sitting on the target should slash its score");

        // The closest-approach metric is surfaced and small (target tracks the Moon).
        withMoon.MinMoonSeparationDeg.ShouldBeLessThan(10.0);
    }

    [Fact]
    public void ScoreTarget_TargetFarFromMoon_ScoreUnchanged()
    {
        var transform = CreateTransform(WinterFullMoonEvening);
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);
        var (moonRa, moonDec, _, _) = MoonAtMidnight(transform, astroDark, astroTwilight);

        // High-declination target far from the Moon (Dec +65, RA half a day away): always
        // well outside the 30 deg avoidance radius.
        var farRa = (moonRa + 12.0) % 24.0;
        var far = new Target(farRa, 65.0, "far-from-moon", null);

        var withMoon = ObservationScheduler.ScoreTarget(far, transform, astroDark, astroTwilight, MinHeight,
            ObjectType.Unknown, moonAvoidanceRadiusDeg: 30.0);
        var withoutMoon = ObservationScheduler.ScoreTarget(far, transform, astroDark, astroTwilight, MinHeight,
            ObjectType.Unknown, moonAvoidanceRadiusDeg: 0.0);

        withMoon.TotalScore.ShouldBe(withoutMoon.TotalScore, "a target far from the Moon must not be penalised");
        // The Moon is up all night, so closest-approach is recorded (not NaN) but well beyond the radius.
        withMoon.MinMoonSeparationDeg.ShouldBeGreaterThan(30.0);
    }

    [Fact]
    public void Schedule_TwoEqualTargetsOneNearMoon_FarTargetWinsSlot()
    {
        var transform = CreateTransform(WinterFullMoonEvening);
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);
        var (moonRa, moonDec, _, _) = MoonAtMidnight(transform, astroDark, astroTwilight);

        var nearMoon = new Target(moonRa, moonDec, "near-moon", null);
        var farFromMoon = new Target((moonRa + 9.0) % 24.0, 65.0, "far-from-moon", null);

        // Equal priority -> the scheduler orders by score, so the unpenalised far target
        // should claim the first (best) slot ahead of the Moon-washed target.
        var proposals = new[]
        {
            new ProposedObservation(nearMoon, Priority: ObservationPriority.High),
            new ProposedObservation(farFromMoon, Priority: ObservationPriority.High),
        };

        var tree = ObservationScheduler.Schedule(
            proposals, transform, astroDark, astroTwilight, MinHeight,
            defaultGain: 100, defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(60));

        tree.Count.ShouldBeGreaterThanOrEqualTo(1);
        tree[0].Target.ShouldBe(farFromMoon, "the dark-sky target should win the contested prime slot");
    }

    [Fact]
    public void PrecomputeMoonGrid_KnownDate_MatchesReduceJ2000()
    {
        var transform = CreateTransform(WinterFullMoonEvening);
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);
        var (astroms, times) = ObservationScheduler.PrecomputeAstromGrid(
            astroDark, astroTwilight, transform.SiteLatitude, transform.SiteLongitude, transform.SiteElevation);
        var grid = ObservationScheduler.PrecomputeMoonGrid(times, astroms, 30.0);

        // Spot-check three bins against a direct geocentric-J2000 ephemeris call.
        foreach (var i in new[] { 0, times.Length / 2, times.Length - 1 })
        {
            VSOP87a.ReduceJ2000(CatalogIndex.Moon, times[i], out var ra, out var dec, out _).ShouldBeTrue();
            grid.RaHours[i].ShouldBe(ra, 1e-6);
            grid.DecDeg[i].ShouldBe(dec, 1e-6);
        }

        grid.Illumination.ShouldBeGreaterThan(0.9);
        grid.AvoidanceRadiusDeg.ShouldBe(30.0);
    }

    // ---- White-box: synthetic grids isolate each gate ----------------------------------

    [Fact]
    public void ScoreTarget_SyntheticMoon_IsolatesIlluminationAndHorizonGates()
    {
        var transform = CreateTransform(SummerEvening);
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);
        var (astroms, times) = ObservationScheduler.PrecomputeAstromGrid(
            astroDark, astroTwilight, transform.SiteLatitude, transform.SiteLongitude, transform.SiteElevation);

        double Score(ObservationScheduler.MoonGrid? grid) => (double)ObservationScheduler.ScoreTarget(
            M13, astroms, times, astroDark, astroTwilight, MinHeight, SiteLongitude,
            ObjectType.Unknown, moonGrid: grid).TotalScore;

        var baseline = Score(null);
        baseline.ShouldBeGreaterThan(0.0);

        // Bright Moon, above horizon, exactly on the target -> heavily penalised.
        var brightOnTarget = Score(SyntheticMoonGrid(times.Length, M13.RA, M13.Dec, aboveHorizon: true, illumination: 1.0));
        brightOnTarget.ShouldBeLessThan(0.1 * baseline, "full Moon at zero separation zeroes each bin");

        // Same geometry but illumination 0 (new Moon) -> illumination gate disables the penalty.
        var darkOnTarget = Score(SyntheticMoonGrid(times.Length, M13.RA, M13.Dec, aboveHorizon: true, illumination: 0.0));
        darkOnTarget.ShouldBe(baseline, 1e-9, "illumination 0 must leave the score untouched");

        // Bright Moon on the target but BELOW the horizon every bin -> horizon gate disables it.
        var belowHorizon = Score(SyntheticMoonGrid(times.Length, M13.RA, M13.Dec, aboveHorizon: false, illumination: 1.0));
        belowHorizon.ShouldBe(baseline, 1e-9, "a below-horizon Moon must not penalise");
    }

    [Fact]
    public void ScoreTarget_SyntheticMoon_ProximityFalloffIsQuadratic()
    {
        var transform = CreateTransform(SummerEvening);
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);
        var (astroms, times) = ObservationScheduler.PrecomputeAstromGrid(
            astroDark, astroTwilight, transform.SiteLatitude, transform.SiteLongitude, transform.SiteElevation);

        double Score(ObservationScheduler.MoonGrid? grid) => (double)ObservationScheduler.ScoreTarget(
            M13, astroms, times, astroDark, astroTwilight, MinHeight, SiteLongitude,
            ObjectType.Unknown, moonGrid: grid).TotalScore;

        var baseline = Score(null);

        // Place the Moon at increasing RA offsets from M13 (same Dec) so separation grows.
        // 30 deg ~ 2h of RA at Dec +36.5 is not linear, so offset just enough to land inside vs
        // outside the radius and assert ordering + the edge no-op.
        var near = Score(SyntheticMoonGrid(times.Length, M13.RA + 0.5, M13.Dec, aboveHorizon: true, illumination: 1.0));   // ~6 deg
        var mid = Score(SyntheticMoonGrid(times.Length, M13.RA + 1.5, M13.Dec, aboveHorizon: true, illumination: 1.0));    // ~18 deg
        var beyond = Score(SyntheticMoonGrid(times.Length, M13.RA + 4.0, M13.Dec, aboveHorizon: true, illumination: 1.0)); // > 30 deg

        near.ShouldBeLessThan(mid, "closer Moon penalises more");
        mid.ShouldBeLessThan(baseline, "a Moon within the radius still penalises");
        beyond.ShouldBe(baseline, 1e-9, "beyond the avoidance radius there is no penalty");
    }
}
