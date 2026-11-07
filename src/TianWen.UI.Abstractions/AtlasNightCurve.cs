using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>One sample of the night's curve: when, how high, and how much it counts for the planner.</summary>
/// <param name="Time">The sample's instant.</param>
/// <param name="Altitude">The object's altitude in degrees.</param>
/// <param name="Weight">
/// 0 at or below the profile's minimum altitude, else the moon factor the planner's score scaled the sample by (1 when no Moon
/// counts): the shade's strength, so a moon-washed stretch reads paler.
/// </param>
public readonly record struct NightSample(DateTimeOffset Time, double Altitude, float Weight);

/// <summary>
/// The atlas info box's night curve (#2059, docs/plans/yearly-planner.md section A): the object's altitude across the planner's
/// night, which part of it the planner COUNTS, for how long, in what window, and how it would rate. A pure model with no
/// drawing; the host paints <see cref="Samples"/> and the info panel words <see cref="ObjectInfoPanel.NightLine"/>.
/// </summary>
/// <remarks>
/// <para><b>One rule, not two.</b> Everything here comes from ONE <see cref="ObservationScheduler.ScoreTargetWithWeights"/> call,
/// whose loop is the one that sums the planner's score: the altitudes are the score's own samples and the weights are the
/// factors it multiplied them by. No altitude or Moon-proximity loop exists in this type, so it cannot disagree with the
/// planner the first time the planner's rule changes.</para>
/// <para><b>The rating is the planner's, never a bare score</b> (<see cref="PlannerActions.ScoreToRating"/> of the combined score
/// against tonight's best): <c>TotalScore</c> is a sum of degree-samples with no unit a person can read.</para>
/// </remarks>
/// <param name="Samples">The planner's samples from astro dark to astro twilight, in order.</param>
/// <param name="Step">The spacing of the samples (the planner's grid, nominally 15 minutes).</param>
/// <param name="AstroDark">Where the night starts.</param>
/// <param name="AstroTwilight">Where the night ends.</param>
/// <param name="MinAltitude">The profile's minimum altitude, degrees: a sample must be above it to count.</param>
/// <param name="Shaded">
/// False for a body that gets no shade (the Moon, whose own light is the thing the moon term subtracts, and which moves
/// several degrees a night): <see cref="UsableTime"/>, <see cref="WindowStart"/> and <see cref="Rating"/> then say nothing.
/// </param>
/// <param name="UsableTime">The samples above the minimum, times <see cref="Step"/>.</param>
/// <param name="WindowStart">The first counting sample (the planner's <c>OptimalStart</c>), or null when none counts.</param>
/// <param name="WindowEnd">The last counting sample (<c>OptimalStart + OptimalDuration</c>), or null when none counts.</param>
/// <param name="Rating">The planner's rating, 0.5 to 5.0 stars, of this object against tonight's best.</param>
public sealed record AtlasNightCurve(
    ImmutableArray<NightSample> Samples,
    TimeSpan Step,
    DateTimeOffset AstroDark,
    DateTimeOffset AstroTwilight,
    byte MinAltitude,
    bool Shaded,
    TimeSpan UsableTime,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    float Rating)
{
    /// <summary>
    /// The curve for <paramref name="target"/> on the planner's night, from one planner score.
    /// </summary>
    /// <param name="target">
    /// The object with its RA/Dec ALREADY RESOLVED. For a moving body (a planet, a comet) the caller passes its position at the
    /// night's MIDPOINT: a night's drift is well under the grid's resolution for all but the Moon, which gets no shade.
    /// </param>
    /// <param name="transform">The site.</param>
    /// <param name="astroDark">The night's start, the planner's.</param>
    /// <param name="astroTwilight">The night's end, the planner's.</param>
    /// <param name="minHeightAboveHorizon">The profile's minimum altitude.</param>
    /// <param name="bestCombinedScore">Tonight's best <see cref="ScoredTarget.CombinedScore"/>, which the rating is against.</param>
    /// <param name="objectType">The object's type, as the planner's score takes it.</param>
    /// <param name="objectBonus">The planner's object bonus for the target (1 when it has none), so the rating matches its row.</param>
    /// <param name="moonAvoidanceRadiusDeg">The planner's Moon-avoidance radius; 0 leaves the Moon out.</param>
    public static AtlasNightCurve Create(
        Target target,
        Transform transform,
        DateTimeOffset astroDark,
        DateTimeOffset astroTwilight,
        byte minHeightAboveHorizon,
        double bestCombinedScore,
        ObjectType objectType = ObjectType.Unknown,
        double objectBonus = 1.0,
        double moonAvoidanceRadiusDeg = ObservationScheduler.DefaultMoonAvoidanceRadiusDeg)
    {
        var weighted = ObservationScheduler.ScoreTargetWithWeights(
            target, transform, astroDark, astroTwilight, minHeightAboveHorizon, objectType, moonAvoidanceRadiusDeg);
        var scored = objectBonus == 1.0 ? weighted.Scored : weighted.Scored with { ObjectBonus = (Half)objectBonus };
        return From(scored, weighted.Weights, astroDark, astroTwilight, minHeightAboveHorizon, bestCombinedScore,
            shaded: target.CatalogIndex != CatalogIndex.Moon);
    }

    /// <summary>
    /// The altitude and the shade's weight at a fractional <paramref name="position"/> along the samples (0 is the first sample,
    /// <c>Samples.Length - 1</c> the last): the altitude is interpolated between the two samples around it, and the weight between
    /// them only when BOTH count. A sample at or below the minimum has weight 0 (it does not count), so averaging with it would
    /// fade the shade out before the line crossed the level, or start it up to half a step late; where only one neighbour counts
    /// the column takes its weight whole. This is what the painter shades a column by, here so a test can pin the edge.
    /// </summary>
    /// <param name="position">A fractional sample index, clamped to the samples.</param>
    internal (double Altitude, float Weight) ShadeAt(double position)
    {
        var last = Samples.Length - 1;
        if (last < 1)
        {
            return Samples.Length == 1 ? (Samples[0].Altitude, Samples[0].Weight) : default;
        }

        position = Math.Clamp(position, 0d, last);
        var i = Math.Min((int)position, last - 1);
        var frac = position - i;
        var (a, b) = (Samples[i], Samples[i + 1]);
        var weight = a.Weight > 0f && b.Weight > 0f
            ? a.Weight + (b.Weight - a.Weight) * (float)frac
            : MathF.Max(a.Weight, b.Weight);
        return (a.Altitude + (b.Altitude - a.Altitude) * frac, weight);
    }

    /// <summary>
    /// The curve from a score and its weights (<see cref="ObservationScheduler.WeightedScore"/>): the pure half of
    /// <see cref="Create"/>.
    /// </summary>
    /// <param name="scored">The planner's score, whose <see cref="ScoredTarget.ElevationProfile"/> holds the samples.</param>
    /// <param name="weights">One weight per sample, as <see cref="ObservationScheduler.ScoreTargetWithWeights"/> returns them.</param>
    /// <param name="astroDark">The night's start.</param>
    /// <param name="astroTwilight">The night's end.</param>
    /// <param name="minHeightAboveHorizon">The profile's minimum altitude.</param>
    /// <param name="bestCombinedScore">Tonight's best combined score, which the rating is against.</param>
    /// <param name="shaded">False for a body that gets no shade (the Moon): every weight reads 0 and nothing is reported.</param>
    public static AtlasNightCurve From(
        in ScoredTarget scored,
        ImmutableArray<float> weights,
        DateTimeOffset astroDark,
        DateTimeOffset astroTwilight,
        byte minHeightAboveHorizon,
        double bestCombinedScore,
        bool shaded = true)
    {
        var profile = scored.ElevationProfile;
        var samples = ImmutableArray.CreateBuilder<NightSample>(weights.Length);
        for (var i = 0; i < weights.Length; i++)
        {
            if (!profile.TryGetValue(RaDecEventTime.Balance + i, out var info))
            {
                throw new ArgumentException($"The score has no sample {i} for the {weights.Length} weights given.", nameof(weights));
            }
            samples.Add(new NightSample(info.Time, info.Alt, shaded ? weights[i] : 0f));
        }

        var step = samples.Count > 1
            ? (samples[^1].Time - samples[0].Time) / (samples.Count - 1)
            : TimeSpan.Zero;

        var counting = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        foreach (var sample in samples)
        {
            if (sample.Altitude > minHeightAboveHorizon)
            {
                counting++;
                first ??= sample.Time;
                last = sample.Time;
            }
        }

        if (!shaded)
        {
            return new AtlasNightCurve(samples.MoveToImmutable(), step, astroDark, astroTwilight, minHeightAboveHorizon,
                Shaded: false, TimeSpan.Zero, null, null, 0f);
        }

        return new AtlasNightCurve(
            samples.MoveToImmutable(), step, astroDark, astroTwilight, minHeightAboveHorizon,
            Shaded: true,
            UsableTime: step * counting,
            WindowStart: first,
            WindowEnd: last,
            Rating: PlannerActions.ScoreToRating(scored.CombinedScore, bestCombinedScore));
    }
}
