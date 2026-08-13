using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// The per-frame distribution of everything registration already measured: measured once as
/// <see cref="Spread"/>, rendered for a log by <see cref="Describe"/>, and durable so one bake's
/// numbers can be diffed against another's.
///
/// <para><b>Why this exists.</b> A whole session (49 subs, "Segaull+Thors_Helmet") was dropped with
/// 48 of 49 subs failing quad fit, and the skip message named only the REFERENCE frame's star count
/// plus the min/max quad range. That is not enough to say which of the two opposite causes applied,
/// so the drop got written down as "genuinely too star-poor to register" and stayed in the plan doc
/// for weeks. It was wrong: reconstructing the histogram by hand from the Debug file log afterwards
/// showed 44 to 97 stars on EVERY frame (median 70) and a healthy median of 46 quads per sub against
/// the reference's 58. Plenty on both sides, none corresponding, which is the quad-PURITY failure
/// the neighbouring comment in <c>SessionRegistrar</c> describes, and a completely different fix.</para>
///
/// <para><b>What decides it is the spread, not any single number</b>, which is why this reports a
/// distribution rather than a mean. Star counts flat and healthy with quads collapsing means purity.
/// Star counts low everywhere means a genuinely sparse field. Star counts SLIDING with capture order
/// means the night degraded (focus drift, dew, cloud, rising extinction), and that is what the same
/// session turned out to show: r = -0.38 against capture order, 78 stars early down to 44 by the end,
/// then 97 on a frame taken after a 28-minute gap.</para>
///
/// <para><b>Bucket edges are fixed, deliberately.</b> Adaptive edges (min to max, split N ways) make
/// a histogram that cannot be compared against another session's or another bake's, and comparison is
/// the entire point once a skipped session is being kept as a regression fixture: the sessions that
/// fail are deliberately left in the source set, so that a change in WHICH sessions fail, or in their
/// numbers, is a signal. Fixed edges cost an occasional wasted bucket instead.</para>
/// </summary>
public static class RegistrationCensus
{
    /// <summary>
    /// Star-count bucket edges. Spans a genuinely sparse wide field (tens) through a dense Vela
    /// panel (thousands); empty leading and trailing buckets are dropped when rendering, so the
    /// width of the line follows the data even though the edges do not.
    /// </summary>
    public static readonly ImmutableArray<int> StarEdges = [0, 25, 50, 100, 200, 400, 800, 1600, 3200];

    /// <summary>
    /// Quad-count edges. Lower ceiling than stars because the quad set is capped at
    /// <c>QuadStars</c> detections, so counts past a few hundred cannot occur.
    /// </summary>
    public static readonly ImmutableArray<int> QuadEdges = [0, 10, 20, 40, 80, 160, 320];

    /// <summary>
    /// One session's registration spread. Persisted verbatim by <c>DatasetSkipStore</c>, so these
    /// are the numbers a later bake is compared against; <see cref="Describe"/> is the only place
    /// they are turned into prose, because a second stored rendering of the same values is how one
    /// of the two goes stale.
    /// </summary>
    /// <param name="Subs">Survivors the census covers.</param>
    /// <param name="QuadFrames">Survivors that reached quad-forming, i.e. cleared the star floor.
    /// Lower than <paramref name="Subs"/> means frames were dropped before quads were counted, so
    /// the quad figures describe a subset and must not be read as covering the session.</param>
    /// <param name="StarHistogram">Counts per <see cref="StarEdges"/> bucket. Kept rather than
    /// derived-on-render because a SHIFTED histogram with an unchanged median is itself the signal a
    /// bake-to-bake comparison is looking for.</param>
    /// <param name="StarTrend">Pearson correlation of star count against capture order, or
    /// <see langword="null"/> when undefined (a perfectly constant session).</param>
    public sealed record Spread(
        int Subs,
        int StarsMin, int StarsMedian, int StarsMax,
        int QuadFrames, int QuadsMin, int QuadsMedian, int QuadsMax,
        float HfdMin, float HfdMedian, float HfdMax,
        float EccMin, float EccMedian, float EccMax,
        double? StarTrend,
        ImmutableArray<int> StarHistogram,
        ImmutableArray<int> QuadHistogram);

    /// <summary>
    /// Measures the spread over one session's survivors. All four lists are in capture order, which
    /// is load-bearing for <see cref="Spread.StarTrend"/>: a session that degraded through the night
    /// has the same min/median/max as one that was uniformly poor, and wants a different fix.
    /// <see langword="null"/> when there are no survivors to describe.
    /// </summary>
    /// <param name="quadCounts">Shorter than <paramref name="starCounts"/> when frames were dropped
    /// at the star floor and so never formed quads.</param>
    public static Spread? Measure(
        IReadOnlyList<int> starCounts,
        IReadOnlyList<int> quadCounts,
        IReadOnlyList<float> hfds,
        IReadOnlyList<float> eccs)
    {
        if (starCounts.Count is 0)
        {
            return null;
        }

        var stars = SortedCopy(starCounts);
        var quads = SortedCopy(quadCounts);
        var hfd = SortedCopy(hfds);
        var ecc = SortedCopy(eccs);

        return new Spread(
            Subs: starCounts.Count,
            StarsMin: stars[0], StarsMedian: Median(stars), StarsMax: stars[^1],
            QuadFrames: quads.Length,
            QuadsMin: quads.Length > 0 ? quads[0] : 0,
            QuadsMedian: quads.Length > 0 ? Median(quads) : 0,
            QuadsMax: quads.Length > 0 ? quads[^1] : 0,
            HfdMin: First(hfd), HfdMedian: Median(hfd), HfdMax: Last(hfd),
            EccMin: First(ecc), EccMedian: Median(ecc), EccMax: Last(ecc),
            StarTrend: starCounts.Count >= 4 ? TryPearsonAgainstIndex(starCounts) : null,
            StarHistogram: Histogram(stars, StarEdges),
            QuadHistogram: Histogram(quads, QuadEdges));
    }

    /// <summary>One line, from the record alone, for an Information or Warning log.</summary>
    public static string Describe(Spread? census)
    {
        if (census is null)
        {
            return "no survivors to census";
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{census.Subs} subs");
        sb.Append(CultureInfo.InvariantCulture, $" | stars {census.StarsMin}/{census.StarsMedian}/{census.StarsMax}");
        AppendHistogram(sb, census.StarHistogram, StarEdges);
        if (census.QuadFrames > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" | quads {census.QuadsMin}/{census.QuadsMedian}/{census.QuadsMax}");
            // Said only when it differs, because equal counts are the normal case and stating them
            // every time trains the eye to skip the clause that matters.
            if (census.QuadFrames != census.Subs)
            {
                sb.Append(CultureInfo.InvariantCulture, $" (over {census.QuadFrames} of {census.Subs})");
            }
            AppendHistogram(sb, census.QuadHistogram, QuadEdges);
        }
        sb.Append(CultureInfo.InvariantCulture, $" | hfd {census.HfdMin:F2}/{census.HfdMedian:F2}/{census.HfdMax:F2} px");
        sb.Append(CultureInfo.InvariantCulture, $" | ecc {census.EccMin:F2}/{census.EccMedian:F2}/{census.EccMax:F2}");

        if (census.StarTrend is { } r)
        {
            sb.Append(CultureInfo.InvariantCulture, $" | stars vs capture order r={r:+0.00;-0.00}");
            if (Math.Abs(r) >= 0.3)
            {
                sb.Append(r < 0 ? " (DEGRADING through the session)" : " (improving through the session)");
            }
        }

        return sb.ToString();
    }

    private static void AppendHistogram(StringBuilder sb, ImmutableArray<int> counts, ImmutableArray<int> edges)
    {
        // Only the occupied span is rendered. A histogram whose ends are all zeroes reads as though
        // the data were missing rather than merely absent from those buckets.
        var first = -1;
        var last = -1;
        for (var b = 0; b < counts.Length; b++)
        {
            if (counts[b] is 0)
            {
                continue;
            }
            if (first < 0)
            {
                first = b;
            }
            last = b;
        }
        if (first < 0)
        {
            return;
        }

        sb.Append(" [");
        for (var b = first; b <= last; b++)
        {
            if (b > first)
            {
                sb.Append(' ');
            }
            var label = b == edges.Length - 1
                ? string.Create(CultureInfo.InvariantCulture, $"{edges[b]}+")
                : string.Create(CultureInfo.InvariantCulture, $"{edges[b]}-{edges[b + 1] - 1}");
            sb.Append(CultureInfo.InvariantCulture, $"{label}:{counts[b]}");
        }
        sb.Append(']');
    }

    private static ImmutableArray<int> Histogram(int[] sorted, ImmutableArray<int> edges)
    {
        var counts = new int[edges.Length];
        foreach (var v in sorted)
        {
            var b = edges.Length - 1;
            for (var e = 1; e < edges.Length; e++)
            {
                if (v < edges[e])
                {
                    b = e - 1;
                    break;
                }
            }
            counts[b]++;
        }
        return [.. counts];
    }

    private static int[] SortedCopy(IReadOnlyList<int> values)
    {
        var copy = new int[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            copy[i] = values[i];
        }
        Array.Sort(copy);
        return copy;
    }

    private static float[] SortedCopy(IReadOnlyList<float> values)
    {
        var copy = new float[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            copy[i] = values[i];
        }
        Array.Sort(copy);
        return copy;
    }

    private static int Median(int[] sorted) =>
        sorted.Length is 0 ? 0
        : sorted.Length % 2 is 1 ? sorted[sorted.Length / 2]
                                 : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;

    private static float Median(float[] sorted) =>
        sorted.Length is 0 ? 0f
        : sorted.Length % 2 is 1 ? sorted[sorted.Length / 2]
                                 : 0.5f * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);

    private static float First(float[] sorted) => sorted.Length > 0 ? sorted[0] : 0f;

    private static float Last(float[] sorted) => sorted.Length > 0 ? sorted[^1] : 0f;

    /// <summary>
    /// Pearson correlation of the values against their own index, i.e. against capture order.
    /// <see langword="null"/> when the values are constant, where the coefficient is undefined
    /// rather than zero (a perfectly steady session must not report "no trend" via a divide).
    /// </summary>
    private static double? TryPearsonAgainstIndex(IReadOnlyList<int> values)
    {
        var n = values.Count;
        var meanIndex = (n - 1) / 2.0;
        double sum = 0;
        foreach (var v in values)
        {
            sum += v;
        }
        var meanValue = sum / n;

        double cov = 0, varIndex = 0, varValue = 0;
        for (var i = 0; i < n; i++)
        {
            var di = i - meanIndex;
            var dv = values[i] - meanValue;
            cov += di * dv;
            varIndex += di * di;
            varValue += dv * dv;
        }
        var den = Math.Sqrt(varIndex * varValue);
        return den > 0 ? cov / den : null;
    }
}
