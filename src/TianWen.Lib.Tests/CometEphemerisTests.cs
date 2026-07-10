using System;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the two-body comet propagator against JPL Horizons geocentric astrometric positions. The
/// reference values are a frozen snapshot fetched from JPL on 2026-07-10 (SBDB osculating elements +
/// Horizons OBSERVER QUANTITIES=1, CENTER=@399, EXTRA_PREC). Each comet is evaluated at ITS OWN
/// element epoch, where the osculating two-body orbit equals the integrated (perturbed) truth by
/// definition -- so agreement is arcsecond-class and any regression (wrong rotation, obliquity, unit,
/// or Kepler branch) shows up as a gross error, far above the 1-arcminute tolerance.
/// </summary>
[Collection("Catalog")]
public class CometEphemerisTests
{
    // 12P/Pons-Brooks -- elliptic (e ~ 0.955). Osculating epoch JD 2460211.5 = 2023-09-24 00:00 TDB.
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

    // C/2023 A3 (Tsuchinshan-ATLAS) -- near-parabolic / barely hyperbolic (e ~ 1.0001), exercises the
    // small-|z| Stumpff branch. Osculating epoch JD 2460448.5 = 2024-05-19 00:00 TDB.
    private static readonly CometElements TsuchinshanAtlas = new(
        Designation: Parse("C/2023 A3"),
        CommonName: "Tsuchinshan-ATLAS",
        PerihelionDistanceAu: 0.3914300307809727,
        Eccentricity: 1.000095309222603,
        InclinationDeg: 139.1121095087364,
        AscendingNodeDeg: 21.55947863619833,
        ArgumentOfPerihelionDeg: 308.4917712569641,
        PerihelionJdTt: 2460581.240840829791,
        EpochJdTt: 2460448.5,
        AbsoluteMagnitudeM1: 8.9,
        SlopeK1: 5.5);

    [Theory]
    // (comet, UTC at element epoch, Horizons astrometric RA deg, Dec deg)
    [InlineData(nameof(PonsBrooks), "2023-09-24T00:00:00Z", 259.674039938, 48.133491870)]
    [InlineData(nameof(TsuchinshanAtlas), "2024-05-19T00:00:00Z", 188.399978926, 1.517802980)]
    public void GivenCometElementsAtEpochWhenPropagatedThenPositionMatchesHorizons(
        string comet, string utc, double expectedRaDeg, double expectedDecDeg)
    {
        var elements = comet == nameof(PonsBrooks) ? PonsBrooks : TsuchinshanAtlas;
        var time = DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

        CometEphemeris.TryGetEquatorialJ2000(elements, time, out var raHours, out var decDeg, out var r, out var delta)
            .ShouldBeTrue();

        r.ShouldBeGreaterThan(0.0);
        delta.ShouldBeGreaterThan(0.0);

        var separationArcsec = AngularSeparationArcsec(raHours * 15.0, decDeg, expectedRaDeg, expectedDecDeg);
        separationArcsec.ShouldBeLessThan(60.0, $"separation was {separationArcsec:F2}\" (RA {raHours * 15.0:F6} vs {expectedRaDeg}, Dec {decDeg:F6} vs {expectedDecDeg})");
    }

    [Fact]
    public void GivenPonsBrooksAtEpochWhenPredictingMagnitudeThenItMatchesHorizonsTMag()
    {
        // Horizons T-mag for 12P at 2023-09-24 = 15.037 (same M1/K1 total-magnitude law).
        var time = new DateTimeOffset(2023, 9, 24, 0, 0, 0, TimeSpan.Zero);

        CometEphemeris.TryGetEquatorialJ2000WithMagnitude(PonsBrooks, time, out _, out _, out var mag)
            .ShouldBeTrue();

        mag.ShouldBe(15.037, 0.2);
    }

    [Fact]
    public void GivenPonsBrooksWhenSamplingMagnitudeCurveThenItBrightensTowardPerihelion()
    {
        // 12P's perihelion is 2024-04-21 (JD 2460421.63). Sampling monthly across it, the predicted
        // magnitude must reach its minimum (brightest) near the perihelion sample -- the whole point of a
        // "realistic(ish)" vmag curve. Window: 2024-01-01 + 30-day steps, 8 samples (spans ~7 months).
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Span<double> mags = stackalloc double[8];
        CometEphemeris.SampleMagnitudeCurve(PonsBrooks, start, TimeSpan.FromDays(30), mags);

        // Every sample is finite (photometry present + solve converges across the whole window).
        var minMag = double.MaxValue;
        var minIdx = -1;
        for (var i = 0; i < mags.Length; i++)
        {
            double.IsNaN(mags[i]).ShouldBeFalse($"sample {i} was NaN");
            if (mags[i] < minMag) { minMag = mags[i]; minIdx = i; }
        }

        // Perihelion (2024-04-21) sits between sample 3 (Apr 10) and 4 (May 10); the brightest sample
        // must be one of those, and it must be materially brighter than the Jan 1 start.
        minIdx.ShouldBeInRange(3, 4);
        minMag.ShouldBeLessThan(mags[0] - 1.0);
    }

    [Fact]
    public void GivenElementsWithoutPhotometryWhenSamplingMagnitudeCurveThenAllSamplesAreNaN()
    {
        var noPhotometry = PonsBrooks with { AbsoluteMagnitudeM1 = double.NaN, SlopeK1 = double.NaN };
        Span<double> mags = stackalloc double[4];
        CometEphemeris.SampleMagnitudeCurve(noPhotometry, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromDays(10), mags);
        foreach (var m in mags)
        {
            double.IsNaN(m).ShouldBeTrue();
        }
    }

    [Fact]
    public void GivenElementsWithoutPhotometryWhenPredictingMagnitudeThenItIsNaN()
    {
        var noPhotometry = PonsBrooks with { AbsoluteMagnitudeM1 = double.NaN, SlopeK1 = double.NaN };
        noPhotometry.HasMagnitudeModel.ShouldBeFalse();

        CometEphemeris.PredictTotalMagnitude(noPhotometry, 1.5, 0.6).ShouldBe(double.NaN);
    }

    private static CometDesignation Parse(string s)
    {
        CometDesignation.TryParse(s, out var d).ShouldBeTrue();
        return d;
    }

    private static double AngularSeparationArcsec(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg)
    {
        const double d2r = Math.PI / 180.0;
        var d1 = dec1Deg * d2r;
        var d2 = dec2Deg * d2r;
        var dRa = (ra1Deg - ra2Deg) * d2r;
        var cosSep = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(dRa);
        cosSep = Math.Clamp(cosSep, -1.0, 1.0);
        return Math.Acos(cosSep) / d2r * 3600.0;
    }
}
