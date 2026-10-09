using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Imaging.Sources;
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

/// <summary>One background class's rates (<see cref="StarlessSpeckles.TextureEdges"/>): the sites on that background and
/// the null drawn on that background alone, so a site is judged against the rate its own surroundings give.</summary>
/// <param name="MinTexture">The class's lower edge, inclusive.</param>
/// <param name="MaxTexture">The class's upper edge, exclusive (positive infinity for the last).</param>
/// <param name="Bands">The class's sites by significance.</param>
/// <param name="Null">The test at star-free positions of the class (up to <see cref="StarlessSpeckles.NullSites"/>).</param>
public sealed record SpeckleClass(float MinTexture, float MaxTexture, ImmutableArray<SpeckleBand> Bands, SpeckleBand Null);

/// <summary>What <see cref="StarlessSpeckles.Measure"/> reads: each band's rate and the sky's own.</summary>
/// <param name="Bands">The subtracted sites' rates by significance.</param>
/// <param name="Null">The same test at covered sky positions away from every star: the rate the noise and the structure
/// alone give, which a site's rate is judged against.</param>
/// <param name="Backgrounds">The same split by the background's texture, when a <see cref="TextureField"/> was given:
/// the frame-wide null is mostly dark sky, while a nebula's texture alone fires the test far more often (10.5 percent of
/// star-free places in the Orion master's M42 core), so a site on nebula judged against the frame's null reads as a
/// remover's fault when it is the background's. Empty without a field.</param>
public sealed record SpeckleReport(ImmutableArray<SpeckleBand> Bands, SpeckleBand Null, ImmutableArray<SpeckleClass> Backgrounds = default);

/// <summary>
/// A background's texture per pixel: a sky map's spread over the pixels' own noise
/// (<see cref="PointSourceFinder.CapByDifferenceNoise"/>), 1 on a smooth sky or glow and above it where a nebula's
/// texture or a crowd of fainter stars moves the sky within a few pixels. The quantity <see cref="FittedStar.Texture"/>
/// reports at each star, read from the MASTER, so a background's class is the scene's and no remover's output can move a
/// site into an easier class by leaving residue round it.
/// </summary>
public readonly struct TextureField
{
    private readonly float[] _spread;
    private readonly float[] _noise;
    private readonly int _width;

    /// <param name="spread">The sky map's spread per pixel.</param>
    /// <param name="noise">The same capped by the pixels' own differences.</param>
    /// <param name="width">The plane's width.</param>
    public TextureField(float[] spread, float[] noise, int width)
    {
        _spread = spread;
        _noise = noise;
        _width = width;
    }

    /// <summary>The sky map's spread per pixel.</summary>
    public float[] Spread => _spread;

    /// <summary>The spread capped by the pixels' own noise: what the hole tests read.</summary>
    public float[] Noise => _noise;

    /// <summary>The texture at a pixel; NaN where the noise is not positive.</summary>
    public float At(int x, int y)
    {
        var i = (y * _width) + x;
        return _noise[i] > 0 ? _spread[i] / _noise[i] : float.NaN;
    }

    /// <summary>
    /// The field of a luminance whose sky map the point-source finder measured (<see cref="PointSourceFinder.Find"/>): the
    /// map's spread, and that spread capped by the pixels' own differences at the finder's sky block. The one definition
    /// the starless builder's hole tests, its catalogue's texture column and the speckle classes share.
    /// </summary>
    public static TextureField FromSkyMap(BackgroundMap sky, float[] luminance, int width, int height, BitMatrix? absent, double fwhm)
    {
        var spread = new float[luminance.Length];
        sky.FillRms(spread);
        float[] noise = [.. spread];
        PointSourceFinder.CapByDifferenceNoise(noise, luminance, width, height, absent, PointSourceFinder.SkyBlockFor(fwhm));
        return new TextureField(spread, noise, width);
    }
}

/// <summary>
/// Dark speckles on a starless plate where stars were: a core pixel left more than <see cref="SpeckleSigma"/> below the
/// plate's own sky beside the star, a black pixel on screen. The hole test (a core's MEAN against its sky) cannot see one:
/// a star subtracted a fraction of a pixel off leaves a dark pixel beside a bright one, and the mean stays at the sky. One
/// definition for the classical builder's report, an eval gate and, for a learned remover, the penalty that teaches it not
/// to make them (docs/plans/star-remover-training.md). Read on a luminance: each core pixel within <see cref="CoreRadius"/>
/// of a site, against the median of the plate from <see cref="SkyInner"/> to <see cref="SkyOuter"/> px and, as sigma,
/// that ring's MAD capped by the pixels' own differences (<see cref="PointSourceFinder.CapByDifferenceNoise"/>): on a
/// bright nebula the ring's MAD is its texture, and against it the dots a remover digs there read as nothing (in the
/// Orion master's M42 core 23.5 percent of the stars carried one against the pixels' noise, where the texture alone gives
/// 10.5 at star-free places; read against the ring's MAD the whole frame said 2.6).
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

    /// <summary>
    /// The background classes' lower edges in <see cref="TextureField"/> texture; the last runs on. Smooth (a dark sky or a
    /// glow, which reads 1.0 to 1.1), textured, and strongly textured; the synthetic builder test's 4 sigma ripple read
    /// 1.47 and the Orion master's M42 core 1.6.
    /// </summary>
    public static readonly ImmutableArray<float> TextureEdges = [0f, 1.2f, 2f];

    private const int MinSkyPixels = 50;

    /// <summary>
    /// Reads the speckle rate of <paramref name="sites"/> (each a subtracted star's centre and significance) on
    /// <paramref name="luminance"/>, and the null at <see cref="NullSites"/> seeded positions at least
    /// <see cref="NullClearance"/> px from every one of <paramref name="sources"/> (every catalogued source, subtracted or
    /// not). Pixels in <paramref name="absent"/> are never read. With <paramref name="texture"/> (the MASTER's, never the
    /// plate's) every site and null position is also classed by its background (<see cref="TextureEdges"/>), and each class
    /// draws a null of its own; the frame-wide null keeps the positions it has without a field.
    /// </summary>
    public static SpeckleReport Measure(
        float[] luminance, int width, int height, BitMatrix? absent, IReadOnlyList<(float X, float Y, float Significance)> sites,
        IReadOnlyList<(float X, float Y)> sources, int seed = 1, TextureField? texture = null)
    {
        var classes = texture is null ? 0 : TextureEdges.Length;
        var bandSites = new int[BandEdges.Length];
        var bandSpeckled = new int[BandEdges.Length];
        var classSites = new int[classes, BandEdges.Length];
        var classSpeckled = new int[classes, BandEdges.Length];
        var scratch = NewScratch();
        var cap = NoiseCap(luminance, width, height, absent);
        foreach (var (x, y, significance) in sites)
        {
            if (Read(luminance, width, height, absent, x, y, scratch, cap) is not { } speckled)
            {
                continue;
            }
            var band = BandOf(significance);
            bandSites[band]++;
            if (speckled)
            {
                bandSpeckled[band]++;
            }
            if (texture is { } field && ClassOf(field, x, y) is var c and >= 0)
            {
                classSites[c, band]++;
                if (speckled)
                {
                    classSpeckled[c, band]++;
                }
            }
        }
        var bands = ImmutableArray.CreateBuilder<SpeckleBand>(BandEdges.Length);
        for (var b = 0; b < BandEdges.Length; b++)
        {
            bands.Add(Band(b, bandSites[b], bandSpeckled[b]));
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
        // The frame-wide null takes the first NullSites readable positions, exactly as without a field; the draw goes on
        // only to fill each class's own null, with as many tries again per class.
        var rng = new Random(seed);
        int nullSites = 0, nullSpeckled = 0;
        var classNull = new int[classes];
        var classNullSpeckled = new int[classes];
        bool ClassesWanting()
        {
            for (var c = 0; c < classes; c++)
            {
                if (classNull[c] < NullSites)
                {
                    return true;
                }
            }
            return false;
        }
        for (var tries = 0; (nullSites < NullSites || ClassesWanting()) && tries < 50 * NullSites * (1 + classes); tries++)
        {
            var px = SkyOuter + rng.NextDouble() * (width - 2 * SkyOuter - 1);
            var py = SkyOuter + rng.NextDouble() * (height - 2 * SkyOuter - 1);
            var cls = texture is { } field ? ClassOf(field, px, py) : -1;
            if (nullSites >= NullSites && (cls < 0 || classNull[cls] >= NullSites))
            {
                continue;  // the frame's null is full, and so is this position's class: nothing to read it for
            }
            if (!Clear(px, py) || Read(luminance, width, height, absent, px, py, scratch, cap) is not { } speckled)
            {
                continue;
            }
            if (nullSites < NullSites)
            {
                nullSites++;
                if (speckled)
                {
                    nullSpeckled++;
                }
            }
            if (cls >= 0 && classNull[cls] < NullSites)
            {
                classNull[cls]++;
                if (speckled)
                {
                    classNullSpeckled[cls]++;
                }
            }
        }
        var backgrounds = ImmutableArray<SpeckleClass>.Empty;
        if (classes > 0)
        {
            var builder = ImmutableArray.CreateBuilder<SpeckleClass>(classes);
            for (var c = 0; c < classes; c++)
            {
                var classBands = ImmutableArray.CreateBuilder<SpeckleBand>(BandEdges.Length);
                for (var b = 0; b < BandEdges.Length; b++)
                {
                    classBands.Add(Band(b, classSites[c, b], classSpeckled[c, b]));
                }
                builder.Add(new SpeckleClass(TextureEdges[c], c + 1 < classes ? TextureEdges[c + 1] : float.PositiveInfinity,
                    classBands.MoveToImmutable(), new SpeckleBand(0f, float.PositiveInfinity, classNull[c], classNullSpeckled[c])));
            }
            backgrounds = builder.MoveToImmutable();
        }
        return new SpeckleReport(bands.MoveToImmutable(), new SpeckleBand(0f, float.PositiveInfinity, nullSites, nullSpeckled), backgrounds);
    }

    private static SpeckleBand Band(int b, int sites, int speckled)
        => new SpeckleBand(BandEdges[b], b + 1 < BandEdges.Length ? BandEdges[b + 1] : float.PositiveInfinity, sites, speckled);

    // The background class at a position, from the texture at its pixel; -1 where the texture is not a number.
    private static int ClassOf(TextureField field, double x, double y)
    {
        var t = field.At((int)Math.Round(x), (int)Math.Round(y));
        if (!float.IsFinite(t))
        {
            return -1;
        }
        for (var c = TextureEdges.Length - 1; c > 0; c--)
        {
            if (t >= TextureEdges[c])
            {
                return c;
            }
        }
        return 0;
    }

    /// <summary>The scratch a site's read needs.</summary>
    internal static float[] NewScratch() => new float[(int)Math.Ceiling(Math.PI * SkyOuter * SkyOuter) + 64];

    /// <summary>The cap on a ring's MAD: the pixels' own noise, as the hole tests read it.</summary>
    internal static float[] NoiseCap(float[] luminance, int width, int height, BitMatrix? absent)
    {
        var cap = new float[luminance.Length];
        Array.Fill(cap, float.PositiveInfinity);
        PointSourceFinder.CapByDifferenceNoise(cap, luminance, width, height, absent, (int)Math.Round(2 * SkyOuter));
        return cap;
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

    // Whether a site is speckled; null where it cannot be read (the window past the frame or the ring, too little sky). With
    // dark, every dark core pixel is collected rather than the first answering.
    private static bool? Read(
        float[] plane, int width, int height, BitMatrix? absent, double cx, double cy, float[] scratch, float[] cap, List<int>? dark = null)
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
        var sigma = Math.Min(1.4826f * StatisticsHelper.NthSmallest(sky, count / 2), cap[(y0 * width) + x0]);
        if (!(sigma > 0))
        {
            return null;
        }
        var limit = median - (SpeckleSigma * sigma);
        var c = (int)Math.Ceiling(CoreRadius);
        var speckled = false;
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
                    if (dark is null)
                    {
                        return true;
                    }
                    dark.Add((y * width) + x);
                    speckled = true;
                }
            }
        }
        return speckled;
    }
}
