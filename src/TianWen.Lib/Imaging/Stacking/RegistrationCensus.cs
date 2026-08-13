using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// The per-frame distribution of everything registration already measured, rendered as one line.
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
/// then 97 on a frame taken after a 28-minute gap. None of that is visible without the per-frame
/// values, all of which were already in hand at the call site.</para>
///
/// <para><b>Bucket edges are fixed, deliberately.</b> Adaptive edges (min to max, split N ways) make
/// a line that cannot be compared against another session's, and comparing sessions is most of the
/// value once more than one is skipped. Fixed edges cost an occasional wasted bucket instead.</para>
/// </summary>
internal static class RegistrationCensus
{
    /// <summary>
    /// Star-count bucket edges. Spans a genuinely sparse wide field (tens) through a dense Vela
    /// panel (thousands); empty leading and trailing buckets are dropped when rendering, so the
    /// width of the line follows the data even though the edges do not.
    /// </summary>
    private static readonly int[] _starEdges = [0, 25, 50, 100, 200, 400, 800, 1600, 3200];

    /// <summary>
    /// Quad-count edges. Lower ceiling than stars because the quad set is capped at
    /// <c>QuadStars</c> detections, so counts past a few hundred cannot occur.
    /// </summary>
    private static readonly int[] _quadEdges = [0, 10, 20, 40, 80, 160, 320];

    /// <summary>
    /// One line describing the whole survivor set: star counts with a histogram, quad counts, HFD
    /// and ellipticity, and the star-count trend against capture order.
    /// </summary>
    /// <param name="starCounts">Detections per survivor, in capture order.</param>
    /// <param name="quadCounts">Quads per survivor that reached quad-forming, in capture order.
    /// Shorter than <paramref name="starCounts"/> when frames were dropped at the star floor.</param>
    /// <param name="hfds">Median HFD per survivor, pixels, in capture order.</param>
    /// <param name="eccs">Median ellipticity per survivor, in capture order.</param>
    public static string Describe(
        IReadOnlyList<int> starCounts,
        IReadOnlyList<int> quadCounts,
        IReadOnlyList<float> hfds,
        IReadOnlyList<float> eccs)
    {
        if (starCounts.Count is 0)
        {
            return "no survivors to census";
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{starCounts.Count} subs");
        AppendIntSpread(sb, "stars", starCounts, _starEdges);
        if (quadCounts.Count > 0)
        {
            AppendIntSpread(sb, "quads", quadCounts, _quadEdges);
        }
        AppendFloatSpread(sb, "hfd", hfds, "px");
        AppendFloatSpread(sb, "ecc", eccs, null);

        // The trend is the whole reason capture order is preserved by the callers. A flat field and
        // a night that degraded produce identical min/median/max, and want opposite investigations.
        if (starCounts.Count >= 4 && TryPearsonAgainstIndex(starCounts) is { } r)
        {
            sb.Append(CultureInfo.InvariantCulture, $" | stars vs capture order r={r:+0.00;-0.00}");
            if (Math.Abs(r) >= 0.3)
            {
                sb.Append(r < 0 ? " (DEGRADING through the session)" : " (improving through the session)");
            }
        }

        return sb.ToString();
    }

    private static void AppendIntSpread(StringBuilder sb, string name, IReadOnlyList<int> values, int[] edges)
    {
        var sorted = new int[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            sorted[i] = values[i];
        }
        Array.Sort(sorted);

        sb.Append(CultureInfo.InvariantCulture,
            $" | {name} {sorted[0]}/{Median(sorted)}/{sorted[^1]}");

        // Only the occupied span is rendered. A histogram whose ends are all dots reads as though
        // the data were missing rather than merely absent from those buckets.
        var first = -1;
        var last = -1;
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

    private static void AppendFloatSpread(StringBuilder sb, string name, IReadOnlyList<float> values, string? unit)
    {
        if (values.Count is 0)
        {
            return;
        }
        var sorted = new float[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            sorted[i] = values[i];
        }
        Array.Sort(sorted);
        sb.Append(CultureInfo.InvariantCulture,
            $" | {name} {sorted[0]:F2}/{Median(sorted):F2}/{sorted[^1]:F2}");
        if (unit is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {unit}");
        }
    }

    private static int Median(int[] sorted) =>
        sorted.Length % 2 is 1 ? sorted[sorted.Length / 2]
                              : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;

    private static float Median(float[] sorted) =>
        sorted.Length % 2 is 1 ? sorted[sorted.Length / 2]
                              : 0.5f * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);

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
