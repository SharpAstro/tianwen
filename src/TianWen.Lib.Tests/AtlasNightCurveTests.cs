using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The atlas night curve's model (#2059): one planner score yields the samples, what counts of them, the window and the
/// rating. Fixed site and date throughout (Vienna, the night of 2025-12-04, a full Moon riding high), no wall clock.
/// </summary>
[Collection("Scheduling")]
public sealed class AtlasNightCurveTests
{
    private const double SiteLatitude = 48.2;
    private const double SiteLongitude = 16.4;
    private const byte MinHeight = 20;

    // The full-Moon night MoonAvoidanceTests uses: the Moon is up all night, so a target glued to it is washed out.
    private static readonly DateTimeOffset WinterFullMoonEvening =
        new DateTimeOffset(2025, 12, 4, 20, 0, 0, TimeSpan.FromHours(1));

    private static Transform CreateTransform()
        => new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = SiteLatitude,
            SiteLongitude = SiteLongitude,
            SiteElevation = 200,
            SiteTemperature = 5,
            DateTimeOffset = WinterFullMoonEvening
        };

    private static (Transform Transform, DateTimeOffset Dark, DateTimeOffset Twilight, double MoonRa, double MoonDec) TheNight()
    {
        var transform = CreateTransform();
        var (dark, twilight) = ObservationScheduler.CalculateNightWindow(transform);
        var (astroms, times) = ObservationScheduler.PrecomputeAstromGrid(
            dark, twilight, transform.SiteLatitude, transform.SiteLongitude, transform.SiteElevation);
        var grid = ObservationScheduler.PrecomputeMoonGrid(times, astroms, ObservationScheduler.DefaultMoonAvoidanceRadiusDeg);
        var mid = times.Length / 2;
        return (transform, dark, twilight, grid.RaHours[mid], grid.DecDeg[mid]);
    }

    /// <summary>High and far from the Moon all night: up the whole of the dark (Dec +65 from 48 degrees never drops under 23).</summary>
    private static Target FarFromTheMoon(double moonRa) => new((moonRa + 12.0) % 24.0, 65.0, "far-from-moon", null);

    [Fact]
    public void ScoredTargetKeepsItsPositionalShape()
    {
        var (transform, dark, twilight, moonRa, _) = TheNight();
        var scored = ObservationScheduler.ScoreTarget(FarFromTheMoon(moonRa), transform, dark, twilight, MinHeight);

        // Nine positional members: a tenth would be a binary break for package consumers.
        var (_, _, _, _, _, _, _, _, _) = scored;
    }

    [Fact]
    public void TheScoreIsTheSameWithOrWithoutWeights()
    {
        var (transform, dark, twilight, moonRa, moonDec) = TheNight();
        var near = new Target(moonRa, moonDec, "near-moon", null);

        var plain = ObservationScheduler.ScoreTarget(near, transform, dark, twilight, MinHeight);
        var weighted = ObservationScheduler.ScoreTargetWithWeights(near, transform, dark, twilight, MinHeight);

        weighted.Scored.TotalScore.ShouldBe(plain.TotalScore);
        weighted.Scored.OptimalStart.ShouldBe(plain.OptimalStart);
        weighted.Scored.OptimalDuration.ShouldBe(plain.OptimalDuration);
        weighted.Scored.MinMoonSeparationDeg.ShouldBe(plain.MinMoonSeparationDeg);
    }

    [Fact]
    public void WeightsTimesAltitudeAboveTheMinimumSumBackToTheScore()
    {
        var (transform, dark, twilight, moonRa, moonDec) = TheNight();
        // A moon-washed target and a clear one: both must rebuild their score from the weights alone.
        foreach (var target in new[] { new Target(moonRa, moonDec, "near-moon", null), FarFromTheMoon(moonRa) })
        {
            var weighted = ObservationScheduler.ScoreTargetWithWeights(target, transform, dark, twilight, MinHeight);
            var profile = weighted.Scored.ElevationProfile;

            var sum = 0d;
            for (var i = 0; i < weighted.Weights.Length; i++)
            {
                var alt = profile[RaDecEventTime.Balance + i].Alt;
                sum += weighted.Weights[i] * Math.Max(0, alt - MinHeight);
            }

            // The planner halves a score whose transit falls outside the dark window; the weights are the part before that.
            var meridian = profile[RaDecEventTime.Meridian].Time;
            var expected = meridian < dark || meridian > twilight ? sum * 0.5 : sum;

            sum.ShouldBeGreaterThan(0, target.Name);
            // TotalScore is a Half (about 3 significant digits).
            ((double)weighted.Scored.TotalScore).ShouldBe(expected, expected * 2e-3, target.Name);
        }
    }

    [Fact]
    public void AMoonWashedSampleWeighsUnderOneAndAMoonlessNightWeighsOne()
    {
        var (transform, dark, twilight, moonRa, moonDec) = TheNight();
        var near = new Target(moonRa, moonDec, "near-moon", null);

        var washed = ObservationScheduler.ScoreTargetWithWeights(near, transform, dark, twilight, MinHeight);
        var counting = washed.Weights.Where(w => w > 0).ToArray();
        counting.ShouldNotBeEmpty();
        counting.ShouldAllBe(w => w < 1f, "a full Moon sitting on the target washes every counting sample");
        counting.Min().ShouldBeLessThan(0.5f);

        var moonless = ObservationScheduler.ScoreTargetWithWeights(near, transform, dark, twilight, MinHeight, moonAvoidanceRadiusDeg: 0);
        var profile = moonless.Scored.ElevationProfile;
        for (var i = 0; i < moonless.Weights.Length; i++)
        {
            var above = profile[RaDecEventTime.Balance + i].Alt > MinHeight;
            moonless.Weights[i].ShouldBe(above ? 1f : 0f, $"sample {i}");
        }
        moonless.Weights.Any(w => w == 1f).ShouldBeTrue();
    }

    [Fact]
    public void ASampleAtOrBelowTheMinimumWeighsNothing()
    {
        var (transform, dark, twilight, moonRa, _) = TheNight();
        // Dec +20 from 48 degrees culminates at 62 and sets: part of the night is under the minimum.
        var setting = new Target((moonRa + 6.0) % 24.0, 20.0, "crossing-the-minimum", null);

        var curve = AtlasNightCurve.Create(setting, transform, dark, twilight, MinHeight, bestCombinedScore: 1000);

        curve.Samples.ShouldContain(s => s.Altitude <= MinHeight, "the case needs a stretch under the minimum");
        curve.Samples.ShouldContain(s => s.Altitude > MinHeight, "the case needs a stretch above it");
        foreach (var s in curve.Samples)
        {
            (s.Weight > 0).ShouldBe(s.Altitude > MinHeight);
        }
    }

    [Fact]
    public void TheSamplesSpanTheNightOnThePlannersGrid()
    {
        var (transform, dark, twilight, moonRa, _) = TheNight();

        var curve = AtlasNightCurve.Create(FarFromTheMoon(moonRa), transform, dark, twilight, MinHeight, bestCombinedScore: 1000);

        curve.Samples[0].Time.ShouldBe(dark);
        // The planner grid is computed in floating point, so the last sample can sit a sub-tick off the twilight instant.
        curve.Samples[^1].Time.ShouldBe(twilight, TimeSpan.FromSeconds(1));
        curve.Step.ShouldBeInRange(TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
        curve.AstroDark.ShouldBe(dark);
        curve.AstroTwilight.ShouldBe(twilight);
        curve.MinAltitude.ShouldBe(MinHeight);
    }

    [Fact]
    public void UsableTimeAndWindowComeFromTheCountingSamples()
    {
        var (transform, dark, twilight, moonRa, _) = TheNight();
        var target = FarFromTheMoon(moonRa);

        var weighted = ObservationScheduler.ScoreTargetWithWeights(target, transform, dark, twilight, MinHeight);
        var curve = AtlasNightCurve.From(weighted.Scored, weighted.Weights, dark, twilight, MinHeight, bestCombinedScore: 1000);

        // Up for the whole of the dark: every sample counts, and the window is the planner's own.
        curve.UsableTime.ShouldBe(curve.Step * curve.Samples.Length);
        curve.WindowStart.ShouldBe(weighted.Scored.OptimalStart);
        curve.WindowEnd.ShouldBe(weighted.Scored.OptimalStart + weighted.Scored.OptimalDuration);
        curve.Shaded.ShouldBeTrue();
    }

    [Fact]
    public void ThePartialNightWindowIsTheFirstToTheLastCountingSample()
    {
        var (transform, dark, twilight, moonRa, _) = TheNight();
        var setting = new Target((moonRa + 6.0) % 24.0, 20.0, "crossing-the-minimum", null);

        var weighted = ObservationScheduler.ScoreTargetWithWeights(setting, transform, dark, twilight, MinHeight);
        var curve = AtlasNightCurve.From(weighted.Scored, weighted.Weights, dark, twilight, MinHeight, bestCombinedScore: 1000);

        var counting = curve.Samples.Count(s => s.Altitude > MinHeight);
        counting.ShouldBeGreaterThan(0);
        counting.ShouldBeLessThan(curve.Samples.Length);
        curve.UsableTime.ShouldBe(curve.Step * counting);
        curve.WindowStart.ShouldBe(weighted.Scored.OptimalStart);
        curve.WindowEnd.ShouldBe(weighted.Scored.OptimalStart + weighted.Scored.OptimalDuration);
    }

    [Fact]
    public void TheRatingIsThePlannersAgainstTheGivenBest()
    {
        var (transform, dark, twilight, moonRa, _) = TheNight();
        var target = FarFromTheMoon(moonRa);
        var weighted = ObservationScheduler.ScoreTargetWithWeights(target, transform, dark, twilight, MinHeight);
        var own = weighted.Scored.CombinedScore;

        foreach (var best in new[] { own, own * 2, own * 10 })
        {
            var curve = AtlasNightCurve.From(weighted.Scored, weighted.Weights, dark, twilight, MinHeight, best);
            curve.Rating.ShouldBe(PlannerActions.ScoreToRating(own, best));
        }

        AtlasNightCurve.From(weighted.Scored, weighted.Weights, dark, twilight, MinHeight, own).Rating.ShouldBe(5.0f, 1e-4f);
        AtlasNightCurve.From(weighted.Scored, weighted.Weights, dark, twilight, MinHeight, own * 10).Rating
            .ShouldBeLessThan(AtlasNightCurve.From(weighted.Scored, weighted.Weights, dark, twilight, MinHeight, own).Rating);
    }

    [Fact]
    public void TheObjectBonusIsInTheRatingAsInThePlannersRow()
    {
        var (transform, dark, twilight, moonRa, _) = TheNight();
        var target = FarFromTheMoon(moonRa);
        var plain = ObservationScheduler.ScoreTarget(target, transform, dark, twilight, MinHeight);
        var best = plain.CombinedScore * 4;

        var curve = AtlasNightCurve.Create(target, transform, dark, twilight, MinHeight, best, objectBonus: 2.0);

        var bonused = plain with { ObjectBonus = (Half)2.0 };
        curve.Rating.ShouldBe(PlannerActions.ScoreToRating(bonused.CombinedScore, best));
    }

    [Fact]
    public void TheMoonItselfGetsNoShade()
    {
        var (transform, dark, twilight, moonRa, moonDec) = TheNight();
        var moon = new Target(moonRa, moonDec, "Moon", CatalogIndex.Moon);

        var curve = AtlasNightCurve.Create(moon, transform, dark, twilight, MinHeight, bestCombinedScore: 1000);

        curve.Shaded.ShouldBeFalse();
        curve.Samples.ShouldNotBeEmpty("its altitude curve is still drawn");
        curve.Samples.ShouldContain(s => s.Altitude > MinHeight, "the full Moon is high this night");
        curve.Samples.ShouldAllBe(s => s.Weight == 0f);
        curve.UsableTime.ShouldBe(TimeSpan.Zero);
        curve.WindowStart.ShouldBeNull();
        curve.WindowEnd.ShouldBeNull();
        ObjectInfoPanel.NightLine(curve, TimeSpan.FromHours(1)).ShouldBeNull();
    }

    [Fact]
    public void AnObjectThatNeverGetsAboveTheMinimumHasNothingToShade()
    {
        var (transform, dark, twilight, moonRa, _) = TheNight();
        // Dec -60 from 48 degrees north never rises above -2.
        var south = new Target(moonRa, -60.0, "far-south", null);

        var curve = AtlasNightCurve.Create(south, transform, dark, twilight, MinHeight, bestCombinedScore: 1000);

        curve.Shaded.ShouldBeTrue();
        curve.UsableTime.ShouldBe(TimeSpan.Zero);
        curve.WindowStart.ShouldBeNull();
        curve.WindowEnd.ShouldBeNull();
        curve.Samples.ShouldAllBe(s => s.Weight == 0f);
        ObjectInfoPanel.NightLine(curve, TimeSpan.FromHours(1)).ShouldBe("Not above 20° in the dark");
    }

    [Fact]
    public void ACurveHasOneWeightPerSampleAndRefusesAMismatchedScore()
    {
        var (transform, dark, twilight, moonRa, _) = TheNight();
        var weighted = ObservationScheduler.ScoreTargetWithWeights(FarFromTheMoon(moonRa), transform, dark, twilight, MinHeight);

        var curve = AtlasNightCurve.From(weighted.Scored, weighted.Weights, dark, twilight, MinHeight, 1000);
        curve.Samples.Length.ShouldBe(weighted.Weights.Length);

        var tooMany = weighted.Weights.Add(1f);
        Should.Throw<ArgumentException>(() => AtlasNightCurve.From(weighted.Scored, tooMany, dark, twilight, MinHeight, 1000));
    }

    // Four samples a step apart, hand-made so every number below is the test's own: the altitude climbs through the 20 degree
    // minimum between samples 1 and 2 (a counting sample has weight > 0, a sample at or below the minimum has 0), and the last
    // two counting samples are moon-washed to a different weight.
    private static AtlasNightCurve ShadeEdgeCurve()
    {
        var dark = new DateTimeOffset(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);
        var step = TimeSpan.FromMinutes(15);
        return new AtlasNightCurve(
            [
                new NightSample(dark, 10d, 0f),
                new NightSample(dark + step, 18d, 0f),
                new NightSample(dark + step * 2, 30d, 0.8f),
                new NightSample(dark + step * 3, 40d, 0.4f),
            ],
            step, dark, dark + step * 3, MinHeight, Shaded: true, step * 2, dark + step * 2, dark + step * 3, 3.0f);
    }

    /// <summary>
    /// Between two counting samples the shade's weight is interpolated; at the samples it is theirs. Pins the painter's own rule
    /// (review note on #2059), which is what makes the shade fade by the Moon's step rather than jump.
    /// </summary>
    [Fact]
    public void TheShadeWeightIsInterpolatedBetweenCountingSamples()
    {
        var curve = ShadeEdgeCurve();

        curve.ShadeAt(2d).Weight.ShouldBe(0.8f, 1e-6f);
        curve.ShadeAt(3d).Weight.ShouldBe(0.4f, 1e-6f);
        curve.ShadeAt(2.5d).Weight.ShouldBe(0.6f, 1e-6f);
        curve.ShadeAt(2.25d).Weight.ShouldBe(0.7f, 1e-6f);
        curve.ShadeAt(2.5d).Altitude.ShouldBe(35d, 1e-9);
    }

    /// <summary>
    /// At the edge, where one neighbour is under the minimum (weight 0) and the other counts, the column takes the counting
    /// neighbour's weight whole, never a blend with the zero: averaging fades the shade out early (the weight at the crossing
    /// would read half the neighbour's), and the old nearest-sample rule dropped it for up to half a step before the line crossed.
    /// Also pins where the crossing is, so a regression in the altitude half shows too.
    /// </summary>
    [Fact]
    public void TheShadeEdgeTakesTheCountingNeighboursWeightWholeAndCrossesWhereTheLineDoes()
    {
        var curve = ShadeEdgeCurve();

        foreach (var position in new[] { 1.0d, 1.1d, 1.5d, 1.9d })
        {
            var (altitude, weight) = curve.ShadeAt(position);
            // Counting neighbour is sample 2 (0.8) for any position in (1, 2); sample 1 itself (a position of exactly 1) is the
            // one that does not count and neighbours sample 2 at the bottom of the segment.
            weight.ShouldBe(0.8f, 1e-6f, $"position {position}: the edge blended a counting weight with a zero");
            altitude.ShouldBe(18d + 12d * (position - 1d), 1e-9);
        }

        // The line crosses 20 degrees at 1 + 2/12 of the way to sample 2: the column there is shaded from that point on and not
        // before, because the painter skips a column whose interpolated altitude is not above the minimum.
        var crossing = 1d + 2d / 12d;
        curve.ShadeAt(crossing - 0.01d).Altitude.ShouldBeLessThan((double)MinHeight);
        curve.ShadeAt(crossing + 0.01d).Altitude.ShouldBeGreaterThan((double)MinHeight);
        curve.ShadeAt(crossing + 0.01d).Weight.ShouldBe(0.8f, 1e-6f);

        // Both ends of a segment with nothing counting stay weightless, and a position past the samples is clamped to the last.
        curve.ShadeAt(0.5d).Weight.ShouldBe(0f);
        curve.ShadeAt(99d).Weight.ShouldBe(0.4f, 1e-6f);
        curve.ShadeAt(-5d).Altitude.ShouldBe(10d, 1e-9);
    }
}
