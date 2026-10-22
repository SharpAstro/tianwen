using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// S2 of the synthetics review (docs/plans/star-remover-training.md, #1737): a master's star counts by significance as a
/// power law, fitted to its own catalogue where the catalogue is complete and extended below the plate builder's detection
/// limit, so a synthetic background can carry the faint stars a real master does. The injector draws brightness from the
/// catalogue's own stars, which hold none under the builder's 5 sigma; the real plate keeps those (the builder leaves
/// them), a synthetic background has none at all.
/// <para>The law is dN/dln S proportional to S^-<see cref="Alpha"/>, fitted by maximum likelihood over
/// [<see cref="FitLowSigma"/>, <see cref="FitHighSigma"/>), where every one of the eight validation catalogues is complete
/// and below their saturated stars. The extension is the DEFICIT under it, from the floor to <see cref="FitLowSigma"/>: per
/// <see cref="BinDex"/> bin, what the law expects less what the catalogue holds, never below zero, so a bin the catalogue
/// already fills gets nothing. Every extra star is a point source of its own at or above the floor, never a carpet: the
/// owner's guard (2026-10-20) is that unresolved glow belongs in the starless image and a remover takes each star's own
/// flux, so a floor under <see cref="MinFloorSigma"/> is refused.</para>
/// </summary>
public sealed class LuminosityFunction
{
    /// <summary>The low end of the fit, in the catalogue's significance: complete on every validation master.</summary>
    public const double FitLowSigma = 10.0;

    /// <summary>The high end of the fit: below the saturated stars, which the pool does not draw.</summary>
    public const double FitHighSigma = 300.0;

    /// <summary>The width of a deficit bin, in decades of significance.</summary>
    public const double BinDex = 0.125;

    /// <summary>Fewer stars than this in the fit's range fit no law.</summary>
    public const int MinFitStars = 50;

    /// <summary>The lowest floor taken: under it an extra star is no longer a point source a remover should take.</summary>
    public const double MinFloorSigma = 2.0;

    private readonly ImmutableArray<(double Lo, double Hi, double Deficit)> _bins;
    private readonly double _deficit;

    private LuminosityFunction(double alpha, int fitStars, int counted, double floorSigma, ImmutableArray<(double Lo, double Hi, double Deficit)> bins)
    {
        Alpha = alpha;
        FitStars = fitStars;
        Counted = counted;
        FloorSigma = floorSigma;
        _bins = bins;
        _deficit = bins.Sum(static b => b.Deficit);
    }

    /// <summary>The fitted slope: dN/dln S proportional to S^-Alpha.</summary>
    public double Alpha { get; }

    /// <summary>The catalogue's stars in the fit's range.</summary>
    public int FitStars { get; }

    /// <summary>The catalogue's stars the cell counts are taken over.</summary>
    public int Counted { get; }

    /// <summary>The faintest extra star, in the catalogue's significance.</summary>
    public double FloorSigma { get; }

    /// <summary>The stars the law expects between the floor and <see cref="FitLowSigma"/> that the catalogue lacks.</summary>
    public double Deficit => _deficit;

    /// <summary>The extra stars per counted catalogue star: a cell's extra count is its requested count times this.</summary>
    public double ExtraPerCountedStar => Counted > 0 ? _deficit / Counted : 0.0;

    /// <summary>Each deficit bin: its significance range and the stars it lacks.</summary>
    public ImmutableArray<(double Lo, double Hi, double Deficit)> Bins => _bins;

    /// <summary>
    /// Fits the law to <paramref name="counted"/>, every counted catalogue star's significance, and measures the deficit
    /// down to <paramref name="floorSigma"/>; null when the catalogue holds fewer than <see cref="MinFitStars"/> stars in the
    /// fit's range.
    /// </summary>
    public static LuminosityFunction? Fit(IReadOnlyList<float> counted, double floorSigma)
    {
        if (!(floorSigma >= MinFloorSigma && floorSigma < FitLowSigma))
        {
            throw new ArgumentOutOfRangeException(nameof(floorSigma), floorSigma,
                $"the luminosity function's floor is in [{MinFloorSigma}, {FitLowSigma}) sigma");
        }
        var inFit = counted.Where(static s => s >= FitLowSigma && s < FitHighSigma).Select(static s => (double)s).ToArray();
        if (inFit.Length < MinFitStars)
        {
            return null;
        }
        var alpha = FitAlpha(inFit, FitLowSigma, FitHighSigma);
        var norm = inFit.Length / (Math.Pow(FitLowSigma, -alpha) - Math.Pow(FitHighSigma, -alpha));

        // Bin edges on the BinDex grid from the floor up to the fit's low end, the floor itself the first edge.
        var edges = new List<double> { floorSigma };
        for (var k = (int)Math.Floor(Math.Log10(floorSigma) / BinDex) + 1; Math.Pow(10.0, k * BinDex) < FitLowSigma * (1 - 1e-12); k++)
        {
            edges.Add(Math.Pow(10.0, k * BinDex));
        }
        edges.Add(FitLowSigma);
        var bins = ImmutableArray.CreateBuilder<(double, double, double)>(edges.Count - 1);
        for (var i = 0; i + 1 < edges.Count; i++)
        {
            var (lo, hi) = (edges[i], edges[i + 1]);
            var model = norm * (Math.Pow(lo, -alpha) - Math.Pow(hi, -alpha));
            var observed = counted.Count(s => s >= lo && s < hi);
            bins.Add((lo, hi, Math.Max(0.0, model - observed)));
        }
        return new LuminosityFunction(alpha, inFit.Length, counted.Count, floorSigma, bins.MoveToImmutable());
    }

    /// <summary>
    /// The maximum-likelihood slope of dN/dln S proportional to S^-alpha truncated to [<paramref name="lo"/>,
    /// <paramref name="hi"/>), for samples inside it: the root of the likelihood's derivative, found by bisection over
    /// (0.001, 10).
    /// </summary>
    public static double FitAlpha(IReadOnlyList<double> samples, double lo, double hi)
    {
        var n = samples.Count;
        var sumLog = samples.Sum(static s => Math.Log(s));
        double Slope(double a)
        {
            var (pl, ph) = (Math.Pow(lo, -a), Math.Pow(hi, -a));
            return n / a + n * (Math.Log(lo) * pl - Math.Log(hi) * ph) / (pl - ph) - sumLog;
        }
        var (left, right) = (1e-3, 10.0);
        for (var i = 0; i < 100; i++)
        {
            var mid = 0.5 * (left + right);
            if (Slope(mid) > 0)
            {
                left = mid;
            }
            else
            {
                right = mid;
            }
        }
        return 0.5 * (left + right);
    }

    /// <summary>
    /// How many extra stars a cell asking for <paramref name="requested"/> catalogue stars takes: requested times
    /// <see cref="ExtraPerCountedStar"/>, rounded up or down at random in proportion, so the mean is exact.
    /// </summary>
    public int DrawCount(int requested, Random rng)
    {
        var expected = requested * ExtraPerCountedStar;
        var whole = Math.Floor(expected);
        return (int)whole + (rng.NextDouble() < expected - whole ? 1 : 0);
    }

    /// <summary>One extra star's significance: a bin in proportion to its deficit, then the law within the bin.</summary>
    public double DrawSignificance(Random rng)
    {
        var pick = rng.NextDouble() * _deficit;
        var bin = _bins[^1];
        foreach (var b in _bins)
        {
            if (pick < b.Deficit)
            {
                bin = b;
                break;
            }
            pick -= b.Deficit;
        }
        // Inverse CDF of S^-(alpha+1) on [lo, hi).
        var (pl, ph) = (Math.Pow(bin.Lo, -Alpha), Math.Pow(bin.Hi, -Alpha));
        var s = Math.Pow(pl - rng.NextDouble() * (pl - ph), -1.0 / Alpha);
        return Math.Clamp(s, bin.Lo, bin.Hi);
    }
}
