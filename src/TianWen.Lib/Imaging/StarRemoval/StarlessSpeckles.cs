using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>One significance band's speckle rate (<see cref="StarlessSpeckles"/>).</summary>
/// <param name="MinSignificance">The band's lower edge, inclusive.</param>
/// <param name="MaxSignificance">The band's upper edge, exclusive (positive infinity for the last).</param>
/// <param name="Sites">The sites read in the band (a site too near the frame's edge or the canvas ring is not read).</param>
/// <param name="Speckled">The sites with at least one dark core pixel.</param>
public readonly record struct SpeckleBand(float MinSignificance, float MaxSignificance, int Sites, int Speckled)
{
    /// <summary>The share of the band's sites that are speckled; NaN with none read.</summary>
    public float Rate => Sites > 0 ? (float)Speckled / Sites : float.NaN;
}

/// <summary>What <see cref="StarlessSpeckles.Measure"/> reads: each band's rate and the sky's own.</summary>
/// <param name="Bands">The subtracted sites' rates by significance.</param>
/// <param name="Null">The same test at covered sky positions away from every star: the rate the noise and the structure
/// alone give, which a site's rate is judged against.</param>
public sealed record SpeckleReport(ImmutableArray<SpeckleBand> Bands, SpeckleBand Null);

/// <summary>
/// Dark speckles on a starless plate where stars were: a core pixel left more than <see cref="SpeckleSigma"/> below the
/// plate's own sky beside the star, a black pixel on screen. The hole test (a core's MEAN against its sky) cannot see one:
/// a star subtracted a fraction of a pixel off leaves a dark pixel beside a bright one, and the mean stays at the sky. One
/// definition for the classical builder's report, an eval gate and, for a learned remover, the penalty that teaches it not
/// to make them (docs/plans/star-remover-training.md). Read on a luminance: each core pixel within <see cref="CoreRadius"/>
/// of a site, against the median and the MAD (as sigma) of the plate from <see cref="SkyInner"/> to
/// <see cref="SkyOuter"/> px.
/// </summary>
public static class StarlessSpeckles
{
    /// <summary>A core pixel this many sigma under the sky is a speckle.</summary>
    public const float SpeckleSigma = 4f;

    /// <summary>The core read at a site.</summary>
    public const double CoreRadius = 3.0;

    /// <summary>The sky annulus's inner radius.</summary>
    public const double SkyInner = 6.0;

    /// <summary>The sky annulus's outer radius.</summary>
    public const double SkyOuter = 12.0;

    /// <summary>A null position keeps this far from every catalogued source.</summary>
    public const double NullClearance = 10.0;

    /// <summary>The null's positions.</summary>
    public const int NullSites = 2000;

    /// <summary>The bands' lower edges; the last runs on.</summary>
    public static readonly ImmutableArray<float> BandEdges = [0f, 20f, 100f, 1000f];

    private const int MinSkyPixels = 50;

    /// <summary>
    /// Reads the speckle rate of <paramref name="sites"/> (each a subtracted star's centre and significance) on
    /// <paramref name="luminance"/>, and the null at <see cref="NullSites"/> seeded positions at least
    /// <see cref="NullClearance"/> px from every one of <paramref name="sources"/> (every catalogued source, subtracted or
    /// not). Pixels in <paramref name="absent"/> are never read.
    /// </summary>
    public static SpeckleReport Measure(
        float[] luminance, int width, int height, BitMatrix? absent, IReadOnlyList<(float X, float Y, float Significance)> sites,
        IReadOnlyList<(float X, float Y)> sources, int seed = 1)
    {
        var bandSites = new int[BandEdges.Length];
        var bandSpeckled = new int[BandEdges.Length];
        var scratch = new float[(int)Math.Ceiling(Math.PI * SkyOuter * SkyOuter) + 64];
        foreach (var (x, y, significance) in sites)
        {
            if (Read(luminance, width, height, absent, x, y, scratch) is not { } speckled)
            {
                continue;
            }
            var band = BandOf(significance);
            bandSites[band]++;
            if (speckled)
            {
                bandSpeckled[band]++;
            }
        }
        var bands = ImmutableArray.CreateBuilder<SpeckleBand>(BandEdges.Length);
        for (var b = 0; b < BandEdges.Length; b++)
        {
            bands.Add(new SpeckleBand(BandEdges[b], b + 1 < BandEdges.Length ? BandEdges[b + 1] : float.PositiveInfinity, bandSites[b], bandSpeckled[b]));
        }

        var grid = new Dictionary<long, List<int>>();
        var cell = NullClearance;
        static long Key(int cx, int cy) => ((long)cy << 32) ^ (uint)cx;
        for (var i = 0; i < sources.Count; i++)
        {
            var key = Key((int)Math.Floor(sources[i].X / cell), (int)Math.Floor(sources[i].Y / cell));
            if (!grid.TryGetValue(key, out var list))
            {
                list = new List<int>();
                grid[key] = list;
            }
            list.Add(i);
        }
        bool Clear(double px, double py)
        {
            var gx = (int)Math.Floor(px / cell);
            var gy = (int)Math.Floor(py / cell);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!grid.TryGetValue(Key(gx + dx, gy + dy), out var list))
                    {
                        continue;
                    }
                    foreach (var i in list)
                    {
                        var ex = sources[i].X - px;
                        var ey = sources[i].Y - py;
                        if (ex * ex + ey * ey < NullClearance * NullClearance)
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }
        var rng = new Random(seed);
        int nullSites = 0, nullSpeckled = 0;
        for (var tries = 0; nullSites < NullSites && tries < 50 * NullSites; tries++)
        {
            var px = SkyOuter + rng.NextDouble() * (width - 2 * SkyOuter - 1);
            var py = SkyOuter + rng.NextDouble() * (height - 2 * SkyOuter - 1);
            if (!Clear(px, py) || Read(luminance, width, height, absent, px, py, scratch) is not { } speckled)
            {
                continue;
            }
            nullSites++;
            if (speckled)
            {
                nullSpeckled++;
            }
        }
        return new SpeckleReport(bands.MoveToImmutable(), new SpeckleBand(0f, float.PositiveInfinity, nullSites, nullSpeckled));
    }

    private static int BandOf(float significance)
    {
        for (var b = BandEdges.Length - 1; b > 0; b--)
        {
            if (significance >= BandEdges[b])
            {
                return b;
            }
        }
        return 0;
    }

    // Whether a site is speckled; null where it cannot be read (the window past the frame or the ring, too little sky).
    private static bool? Read(float[] plane, int width, int height, BitMatrix? absent, double cx, double cy, float[] scratch)
    {
        var r = (int)Math.Ceiling(SkyOuter);
        var x0 = (int)Math.Round(cx);
        var y0 = (int)Math.Round(cy);
        if (x0 - r < 0 || y0 - r < 0 || x0 + r >= width || y0 + r >= height)
        {
            return null;
        }
        var count = 0;
        for (var y = y0 - r; y <= y0 + r; y++)
        {
            for (var x = x0 - r; x <= x0 + r; x++)
            {
                var d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d2 < SkyInner * SkyInner || d2 >= SkyOuter * SkyOuter)
                {
                    continue;
                }
                var v = plane[(y * width) + x];
                if ((absent is { } a && a[y, x]) || !float.IsFinite(v))
                {
                    continue;
                }
                scratch[count++] = v;
            }
        }
        if (count < MinSkyPixels)
        {
            return null;
        }
        var sky = scratch.AsSpan(0, count);
        var median = StatisticsHelper.NthSmallest(sky, count / 2);
        for (var i = 0; i < count; i++)
        {
            sky[i] = Math.Abs(sky[i] - median);
        }
        var sigma = 1.4826f * StatisticsHelper.NthSmallest(sky, count / 2);
        if (!(sigma > 0))
        {
            return null;
        }
        var limit = median - (SpeckleSigma * sigma);
        var c = (int)Math.Ceiling(CoreRadius);
        for (var y = y0 - c; y <= y0 + c; y++)
        {
            for (var x = x0 - c; x <= x0 + c; x++)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > CoreRadius * CoreRadius || (absent is { } a && a[y, x]))
                {
                    continue;
                }
                var v = plane[(y * width) + x];
                if (float.IsFinite(v) && v < limit)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
