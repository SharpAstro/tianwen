using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// How old an element set is, in REVOLUTIONS, which is the unit that predicts whether a two-body
/// propagation from it still lands on the sky where the comet is. 10P's real published record is the
/// worked example: 1.8 revolutions old, and 9.3 degrees out (measured in <c>CometEphemerisTests</c>).
///
/// <para>The magnitude is deliberately NOT covered by this, and the last test says why: it agrees with
/// JPL Horizons to 0.03 mag, so the faint answer near perihelion is JPL's own prediction, not a stale
/// model and not our arithmetic.</para>
/// </summary>
public class CometElementStalenessTests
{
    // JD(TT) for 2026-08-06, near enough for a test whose unit is REVOLUTIONS.
    private const double Jd2026Aug = 2461259.0;

    private static CometElements Comet(double q, double e, double perihelionJd, double epochJd)
    {
        CometDesignation.TryParse("10P", out var designation).ShouldBeTrue();
        return new CometElements(designation, "Tempel", q, e, 12.03, 117.8, 195.54, perihelionJd, epochJd, 13.7, 6.5);
    }

    /// <summary>10P/Tempel 2 exactly as JPL SBDB serves it: an epoch-2016 solution.</summary>
    private static CometElements RealTenP => Comet(q: 1.418, e: 0.5374, perihelionJd: 2457340.74, epochJd: 2457650.5);

    [Fact]
    public void TheOrbitalPeriodComesOutOfTheElements()
    {
        // a = q / (1 - e) = 3.065 AU, so P = a^1.5 = 5.37 years, which is 10P's published period.
        RealTenP.OrbitalPeriodYears.ShouldBe(5.37, tolerance: 0.02);
    }

    [Fact]
    public void TenPsPublishedRecordIsTwoRevolutionsBehindTheApparitionBeingWatched()
    {
        var revolutions = RealTenP.RevolutionsSinceEpoch(Jd2026Aug);

        revolutions.ShouldBe(1.84, tolerance: 0.05);
        RealTenP.IsElementSetStale(Jd2026Aug).ShouldBeTrue();
    }

    [Fact]
    public void AFreshElementSetIsNotFlagged()
    {
        // Same comet, same orbit, an epoch from a few months ago instead of ten years.
        var fresh = Comet(q: 1.418, e: 0.5374, perihelionJd: 2461261.2, epochJd: Jd2026Aug - 120);

        fresh.RevolutionsSinceEpoch(Jd2026Aug).ShouldBeLessThan(1.0);
        fresh.IsElementSetStale(Jd2026Aug).ShouldBeFalse();
    }

    [Fact]
    public void ANonPeriodicCometIsNeverFlagged()
    {
        // A hyperbolic comet has no period, so "how many revolutions old" is not a question. It must
        // answer NaN and NOT be reported as stale -- NaN >= 1.0 is false, which is the behaviour
        // wanted here and is easy to invert by accident.
        var hyperbolic = Comet(q: 0.8, e: 1.2, perihelionJd: Jd2026Aug, epochJd: 2451545.0);

        double.IsNaN(hyperbolic.OrbitalPeriodYears).ShouldBeTrue();
        double.IsNaN(hyperbolic.RevolutionsSinceEpoch(Jd2026Aug)).ShouldBeTrue();
        hyperbolic.IsElementSetStale(Jd2026Aug).ShouldBeFalse();
    }

    [Fact]
    public void TheMagnitudeMatchesHorizons_SoItIsNotTheThingThatIsWrong()
    {
        // Checked against the JPL Horizons API on 2026-08-06: T-mag 12.776 at r = 1.418334 AU,
        // delta = 0.414855 AU, from solution JPL#K265/43 (soln.date 2026-Jul-28, 6,347 observations
        // through 2026) -- which carries the SAME M1 = 13.7 / K1 = 6.5 we read from SBDB. So a comet
        // reading ~12.8 two days before perihelion is JPL's own answer, and the only bug in the
        // neighbourhood is the POSITION.
        //
        // Pinned so nobody "fixes" the faint answer by editing the law. Two plausible edits are both
        // wrong: the nuclear parameters are not a substitute (SBDB and Horizons both report M2/K2 as
        // n.a. for 10P), and there is nothing to gain from a fresher photometric fit that Horizons
        // does not already have.
        var m = CometEphemeris.PredictTotalMagnitude(RealTenP, heliocentricDistanceAu: 1.418334, geocentricDistanceAu: 0.414855);

        m.ShouldBe(12.776, tolerance: 0.05);
    }
}
