using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The brightest part of each large, extended source in a frame (a galaxy, a nebula), which a star remover must leave alone
/// (#1658, the owner's idea of 2026-10-19). Learned removers take a bright compact core as a star: E6 took 16.5 % of M31's
/// nucleus in red and a bright knot of the Lagoon, where StarXTerminator and StarNet2 took about none. Putting the INPUT back
/// there, feathered, keeps the core whatever the remover did, at the price of the stars that sit inside it, unless the
/// frame's catalogued stars are handed over too (<see cref="CoreStars"/>, #2242), which are then left removed. The design
/// and its measurements: docs/plans/star-remover-training.md, "Bright compact structure taken as stars".
/// </summary>
/// <remarks>
/// <para>The large-scale map is the luminance's <see cref="BlockPx"/> block median, median-filtered over
/// <see cref="FilterBlocks"/> blocks a side (56 px): a star covers too little of any window to survive it, an extended source
/// does. An extended source is a connected region of that map standing <see cref="Options.ExtendedSigma"/> per-pixel sky
/// sigmas over the sky. One whose own peak stands <see cref="Options.MinPeakSigma"/> sigmas or more gets a core: where the
/// map is at least <see cref="Options.CoreShare"/> of that peak, at most <see cref="Options.MaxAreaShare"/> of the source's
/// area, its brightest blocks first, so a nebula filling the frame gives up only its brightest part.</para>
/// <para>Three rules keep a core off what is not an extended source. <b>Edge:</b> a source whose map peak lies within
/// <see cref="Options.EdgeReachPx"/> (56 px) of the border or of an absent pixel gets none, since the median window is cut
/// there and a stack's coverage edge reads as a bright source. <b>Star:</b> a bright star's halo fills the 56 px median
/// window only at a small share of the star's own height, so the map under it is low and the star's compact peak stands far
/// over it; a source whose full-resolution p90 about the peak exceeds <see cref="Options.MaxPeakRatio"/> times the map's peak
/// gets none, whereas an extended source's core IS the map and reads near 1 to 5.5. <b>Clipped star (#2360):</b> that ratio
/// has a ceiling. A star bright enough to clip has a flat top, the p90 is the clip level however bright the star is, and a
/// top as wide as Alcyone's (31 px on the master that showed it) fills the window at a sixth of that level, so the brightest
/// stars read under the bar. A source whose map peaks on a round flat top that falls off as a star does, and whose core is no
/// more than <see cref="Options.MaxCoreOverFlatTop"/> times that top, gets none: the core is the star. A clipped star ON a
/// real core (eta Carinae in the Keyhole) has a core many times its top and keeps it.</para>
/// <para>The sky sigma is read from second differences along rows by their median; a frame whose differences are mostly zero (8-bit
/// data) is read by their clipped spread instead, which on a float frame would count the galaxy's own gradients as noise.</para>
/// </remarks>
public sealed class ExtendedCoreMask
{
    /// <summary>The block the large-scale map is made of, pixels a side.</summary>
    public const int BlockPx = 8;

    /// <summary>The median filter over the block grid, blocks a side.</summary>
    public const int FilterBlocks = 7;

    /// <summary>What counts as an extended source and how much of it is protected.</summary>
    /// <param name="CoreShare">The share of a source's own peak (over the sky) the core reaches down to.</param>
    /// <param name="MaxAreaShare">The most of a source's area its core may take.</param>
    /// <param name="ExtendedSigma">The large-scale map's height over the sky, in per-pixel sky sigmas, that makes a region
    /// part of an extended source.</param>
    /// <param name="MinPeakSigma">The peak a source needs before it gets a core: a faint nebula is never taken as a star.</param>
    /// <param name="MinSourceBlocks">The fewest blocks an extended source covers.</param>
    /// <param name="FeatherRadiusPx">The radius of the three box blurs the mask's edge is feathered with (about a Gaussian of
    /// 1.1 times it).</param>
    /// <param name="EdgeReachPx">A source whose map peak lies closer than this to the frame's border or to an absent pixel gets
    /// no core: the median window is cut there, so the map is a coverage edge, not a source (default the window's reach,
    /// <see cref="FilterBlocks"/> times <see cref="BlockPx"/>).</param>
    /// <param name="MaxPeakRatio">A source gets no core when the 90th percentile of the full-resolution light above the sky in a
    /// box about its peak (side the square root of the core's area) exceeds this multiple of the map's peak above the sky: that
    /// is a star's halo, not an extended source. The survey (2026-11) put stars at 7.2 and over and extended sources at 5.5 and
    /// under; the bar sits in that gap.</param>
    /// <param name="MaxCoreOverFlatTop">A source whose map peaks on a clipped star's flat top (<see cref="Core.FlatTopPx"/>,
    /// round and falling off as a star does) gets no core when its core is no more than this many times the top's area: the
    /// core is then the star's own light. 9 is the disc of three top radii; on the survey (2026-11-21) the one star kept as a
    /// core reads 1.3 and the two cores with a clipped star on them (eta Carinae) 71 and 128. 0 turns the rule off.</param>
    public sealed record Options(
        double CoreShare = 0.5,
        double MaxAreaShare = 0.01,
        double ExtendedSigma = 3.0,
        double MinPeakSigma = 50.0,
        int MinSourceBlocks = 16,
        int FeatherRadiusPx = 4,
        int EdgeReachPx = FilterBlocks * BlockPx,
        double MaxPeakRatio = 6.0,
        double MaxCoreOverFlatTop = 9.0)
    {
        /// <summary>The defaults, measured on the owner's M31, Lagoon and Trifid frames (2026-10-19).</summary>
        public static readonly Options Default = new();
    }

    /// <summary>One protected core.</summary>
    /// <param name="X">Where the source's large-scale map peaks, frame pixels.</param>
    /// <param name="Y">Where it peaks.</param>
    /// <param name="PeakSigma">That peak over the sky, in per-pixel sky sigmas.</param>
    /// <param name="SourceAreaPx">The extended source's area.</param>
    /// <param name="CoreAreaPx">The core's area, before feathering.</param>
    /// <param name="EdgeDistancePx">The peak's distance (Chebyshev) to the frame's border or the nearest absent pixel, capped at
    /// <see cref="Options.EdgeReachPx"/>.</param>
    /// <param name="PeakRatio">The star rule's measure: the 90th percentile of the full-resolution light above the sky in a box
    /// about the peak, over the map's peak above the sky.</param>
    /// <param name="FlatTopPx">The clipped-star rule's first measure: how many connected pixels about the brightest one near
    /// the peak stand at <see cref="FlatTopShare"/> of its light or over (0: the top runs past what the rule looks at, or
    /// does not hold the peak).</param>
    /// <param name="FlatTopRoundness">How round that top is: its axis ratio times how well it fills the ellipse of its second
    /// moments, 1 for a filled disc.</param>
    /// <param name="FlatTopFall">The light two top radii out from the top's centre, as a share of the top's (1 where it
    /// cannot be read).</param>
    public readonly record struct Core(
        int X, int Y, double PeakSigma, int SourceAreaPx, int CoreAreaPx, int EdgeDistancePx = 0, double PeakRatio = 0,
        int FlatTopPx = 0, double FlatTopRoundness = 0, double FlatTopFall = 1);

    /// <summary>The share of the brightest pixel's light (over the sky) a pixel needs to belong to the flat top.</summary>
    public const double FlatTopShare = 0.9;

    // What makes a flat top a clipped star's. Read on the survey of 2026-11-21 (201 session masters, both star rules off so
    // that every source is listed with its measures; docs/plans/star-remover-training.md, "A clipped star reads as a core"):
    // the 37 stars with a top of 50 px or over fall to 0.010 to 0.144 of the top two radii out, the eleven kept cores of
    // clipped nebula light (Orion, the Helix, the Tarantula, a Large Magellanic Cloud knot) to 0.221 and over, so the fall's
    // bar is the geometric middle of that gap; 36 of the 37 stars are round to 0.80 or over. Roundness alone does not tell
    // the two apart (two of Orion's tops read 0.83 and 0.87).
    private const int MinStarTopPx = 50;
    private const double MinStarTopRoundness = 0.75;
    private const double MaxStarTopFall = 0.18;

    private ExtendedCoreMask(int width, int height, float[] weight, ImmutableArray<Core> cores, double skySigma, CoreStars.Report? starsLeftOut = null)
    {
        Width = width;
        Height = height;
        Weight = weight;
        Cores = cores;
        SkySigma = skySigma;
        StarsLeftOut = starsLeftOut;
    }

    /// <summary>What <see cref="CoreStars"/> found when the catalogued stars were taken out of this mask
    /// (<see cref="LeavingOut"/>); null on a mask as <see cref="Find"/> made it.</summary>
    public CoreStars.Report? StarsLeftOut { get; }

    /// <summary>The frame's width.</summary>
    public int Width { get; }

    /// <summary>The frame's height.</summary>
    public int Height { get; }

    /// <summary>Per pixel, row-major, how much of the input a starless frame takes back: 1 inside a core, 0 away from every
    /// core, feathered between.</summary>
    public float[] Weight { get; }

    /// <summary>The cores, brightest source first.</summary>
    public ImmutableArray<Core> Cores { get; }

    /// <summary>The per-pixel sky sigma the thresholds were read in.</summary>
    public double SkySigma { get; }

    /// <summary>Finds the cores of <paramref name="image"/> (its luminance, the mean of its channels; the canvas ring, and any
    /// pixel not finite in every channel, left out).</summary>
    public static ExtendedCoreMask Find(Image image, Options? options = null)
    {
        options ??= Options.Default;
        var (channels, width, height) = image.Shape;
        var luminance = new float[width * height];
        var present = new bool[width * height];
        var ring = image.AbsentPixels();
        for (var c = 0; c < channels; c++)
        {
            var span = image.GetChannelSpan(c);
            for (var i = 0; i < luminance.Length; i++)
            {
                luminance[i] += span[i] / channels;
            }
        }
        var values = new List<float>(luminance.Length);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                present[i] = float.IsFinite(luminance[i]) && !(ring is { } r && r[y, x]);
                if (present[i])
                {
                    values.Add(luminance[i]);
                }
            }
        }
        var weight = new float[width * height];
        if (values.Count == 0)
        {
            return new ExtendedCoreMask(width, height, weight, [], double.NaN);
        }
        var sky = StatisticsHelper.MedianFast(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values));
        var sigma = SkySigmaOf(luminance, present, width, height);

        // The large-scale map, on the block grid.
        var gw = width / BlockPx;
        var gh = height / BlockPx;
        if (gw < FilterBlocks || gh < FilterBlocks || !(sigma > 0))
        {
            return new ExtendedCoreMask(width, height, weight, [], sigma);
        }
        var grid = new float[gw * gh];
        Span<float> block = stackalloc float[BlockPx * BlockPx];
        for (var by = 0; by < gh; by++)
        {
            for (var bx = 0; bx < gw; bx++)
            {
                var n = 0;
                for (var y = by * BlockPx; y < (by + 1) * BlockPx; y++)
                {
                    for (var x = bx * BlockPx; x < (bx + 1) * BlockPx; x++)
                    {
                        if (present[(y * width) + x])
                        {
                            block[n++] = luminance[(y * width) + x];
                        }
                    }
                }
                grid[(by * gw) + bx] = n > 0 ? StatisticsHelper.MedianFast(block[..n]) : sky;
            }
        }
        var map = new float[gw * gh];
        Span<float> window = stackalloc float[FilterBlocks * FilterBlocks];
        const int half = FilterBlocks / 2;
        for (var by = 0; by < gh; by++)
        {
            for (var bx = 0; bx < gw; bx++)
            {
                var n = 0;
                for (var y = Math.Max(0, by - half); y <= Math.Min(gh - 1, by + half); y++)
                {
                    for (var x = Math.Max(0, bx - half); x <= Math.Min(gw - 1, bx + half); x++)
                    {
                        window[n++] = grid[(y * gw) + x];
                    }
                }
                map[(by * gw) + bx] = StatisticsHelper.MedianFast(window[..n]) - sky;
            }
        }

        // Each extended source, and its core.
        var label = new int[gw * gh];
        var keep = new bool[gw * gh];
        var found = new List<(Core Core, double Peak)>();
        var queue = new Queue<int>();
        var members = new List<int>();
        var extended = options.ExtendedSigma * sigma;
        for (var start = 0; start < map.Length; start++)
        {
            if (label[start] != 0 || !(map[start] > extended))
            {
                continue;
            }
            members.Clear();
            label[start] = 1;
            queue.Enqueue(start);
            while (queue.TryDequeue(out var at))
            {
                members.Add(at);
                var (ax, ay) = (at % gw, at / gw);
                foreach (var (nx, ny) in (ReadOnlySpan<(int, int)>)[(ax - 1, ay), (ax + 1, ay), (ax, ay - 1), (ax, ay + 1)])
                {
                    if (nx >= 0 && ny >= 0 && nx < gw && ny < gh && label[(ny * gw) + nx] == 0 && map[(ny * gw) + nx] > extended)
                    {
                        label[(ny * gw) + nx] = 1;
                        queue.Enqueue((ny * gw) + nx);
                    }
                }
            }
            if (members.Count < options.MinSourceBlocks)
            {
                continue;
            }
            var peakAt = members[0];
            foreach (var m in members)
            {
                if (map[m] > map[peakAt])
                {
                    peakAt = m;
                }
            }
            var peak = map[peakAt];
            if (peak < options.MinPeakSigma * sigma)
            {
                continue;
            }
            // The core: at least CoreShare of the peak, at most MaxAreaShare of the source, brightest blocks first.
            var top = new List<int>();
            foreach (var m in members)
            {
                if (map[m] >= options.CoreShare * peak)
                {
                    top.Add(m);
                }
            }
            var cap = Math.Max(1, (int)(options.MaxAreaShare * members.Count));
            if (top.Count > cap)
            {
                top.Sort((a, b) => map[b].CompareTo(map[a]));
                top.RemoveRange(cap, top.Count - cap);
            }
            var px = ((peakAt % gw) * BlockPx) + (BlockPx / 2);
            var py = ((peakAt / gw) * BlockPx) + (BlockPx / 2);
            var coreAreaPx = top.Count * BlockPx * BlockPx;

            // Edge rule: a source peaking where the median window is cut by the frame's border or by absent pixels is a
            // coverage edge or a stack's rim, never a core.
            var edge = EdgeDistance(present, width, height, px, py, options.EdgeReachPx);
            if (edge < options.EdgeReachPx)
            {
                continue;
            }
            // Star rule: a star's halo fills the median window only at a small share of its own height, so a star reads as a
            // faint extended source with a very tall compact peak; an extended source's core is the map.
            var ratio = PeakRatio(luminance, present, width, height, px, py, (int)Math.Round(Math.Sqrt(coreAreaPx)), sky, peak);
            if (ratio > options.MaxPeakRatio)
            {
                continue;
            }
            // Clipped-star rule: the ratio above cannot read a clipped star whose flat top is wide, since the p90 is the clip
            // level and the window under such a top is a large share of it. Where the map peaks on a star's flat top and the
            // core is no larger than the star's own disc, the core is the star.
            var flat = FlatTopAt(luminance, present, width, height, px, py, sky);
            if (options.MaxCoreOverFlatTop > 0 && flat.IsStarTop && coreAreaPx <= options.MaxCoreOverFlatTop * flat.AreaPx)
            {
                continue;
            }
            foreach (var m in top)
            {
                keep[m] = true;
            }
            found.Add((new Core(px, py, peak / sigma, members.Count * BlockPx * BlockPx, coreAreaPx, edge, ratio,
                flat.AreaPx, flat.Roundness, flat.Fall), peak));
        }

        // Feathered OUTWARD: the cores grown by the blur's reach (three passes of its radius) first, so the blur's ramp lies
        // outside them and a core takes the input back whole (feathered in place, a 15-block core kept 0.998 at its centre).
        var grow = options.FeatherRadiusPx > 0 ? (3 * options.FeatherRadiusPx + BlockPx - 1) / BlockPx : 0;
        var grown = new bool[gw * gh];
        for (var by = 0; by < gh; by++)
        {
            for (var bx = 0; bx < gw; bx++)
            {
                if (!keep[(by * gw) + bx])
                {
                    continue;
                }
                for (var y = Math.Max(0, by - grow); y <= Math.Min(gh - 1, by + grow); y++)
                {
                    for (var x = Math.Max(0, bx - grow); x <= Math.Min(gw - 1, bx + grow); x++)
                    {
                        grown[(y * gw) + x] = true;
                    }
                }
            }
        }

        // Back to the frame's grid, feathered, never onto a pixel the frame does not have.
        for (var y = 0; y < gh * BlockPx; y++)
        {
            for (var x = 0; x < gw * BlockPx; x++)
            {
                weight[(y * width) + x] = grown[((y / BlockPx) * gw) + (x / BlockPx)] ? 1f : 0f;
            }
        }
        if (found.Count > 0 && options.FeatherRadiusPx > 0)
        {
            RobustBackgroundFit.BoxBlur3(weight, width, height, options.FeatherRadiusPx, new float[weight.Length]);
        }
        for (var i = 0; i < weight.Length; i++)
        {
            weight[i] = present[i] ? Math.Clamp(weight[i], 0f, 1f) : 0f;
        }
        found.Sort((a, b) => b.Peak.CompareTo(a.Peak));
        var cores = ImmutableArray.CreateBuilder<Core>(found.Count);
        foreach (var (core, _) in found)
        {
            cores.Add(core);
        }
        return new ExtendedCoreMask(width, height, weight, cores.MoveToImmutable(), sigma);
    }

    /// <summary>
    /// This mask with <paramref name="footprint"/> taken out of it: per pixel the weight times one less the footprint (1 on
    /// a star, <see cref="CoreStars.Find"/>), so where a catalogued star sits inside a core the remover's plate stands and
    /// the star is not put back with the core (#2242). The cores themselves are as they were.
    /// </summary>
    public ExtendedCoreMask LeavingOut(float[] footprint, CoreStars.Report report)
    {
        if (footprint.Length != Weight.Length)
        {
            throw new ArgumentException($"the footprint holds {footprint.Length} pixels and the mask {Weight.Length}");
        }
        var weight = new float[Weight.Length];
        for (var i = 0; i < weight.Length; i++)
        {
            weight[i] = Weight[i] * (1f - Math.Clamp(footprint[i], 0f, 1f));
        }
        return new ExtendedCoreMask(Width, Height, weight, Cores, SkySigma, report);
    }

    /// <summary>
    /// <see cref="Find"/>, and with <paramref name="catalogStars"/> the catalogued stars the frame shows inside the cores
    /// taken out of what the mask puts back (<see cref="CoreStars"/>, #2242). Without stars, or where no zero point can be
    /// read off them, the mask is <see cref="Find"/>'s.
    /// </summary>
    public static ExtendedCoreMask Find(Image image, Options? options, IReadOnlyList<FrameStar>? catalogStars, CoreStars.Options? starOptions = null)
    {
        var mask = Find(image, options);
        if (catalogStars is null || mask.Cores.Length == 0)
        {
            return mask;
        }
        var (footprint, report) = CoreStars.Find(image, mask, catalogStars, starOptions);
        return footprint is null
            ? new ExtendedCoreMask(mask.Width, mask.Height, mask.Weight, mask.Cores, mask.SkySigma, report)
            : mask.LeavingOut(footprint, report);
    }

    /// <summary>
    /// <paramref name="starless"/> with the input put back in the cores: <c>w input + (1 - w) starless</c> per channel, where
    /// both are finite; elsewhere the starless pixel as it was.
    /// </summary>
    public Image Apply(Image input, Image starless)
    {
        if (input.Width != Width || input.Height != Height || starless.Width != Width || starless.Height != Height)
        {
            throw new ArgumentException($"the mask is {Width}x{Height}; the input is {input.Width}x{input.Height} and the starless {starless.Width}x{starless.Height}");
        }
        var channels = starless.ChannelCount;
        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var a = input.GetChannelSpan(Math.Min(c, input.ChannelCount - 1));
            var s = starless.GetChannelSpan(c);
            var plane = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var i = (y * Width) + x;
                    var w = Weight[i];
                    plane[y, x] = w > 0f && float.IsFinite(a[i]) && float.IsFinite(s[i]) ? (w * a[i]) + ((1f - w) * s[i]) : s[i];
                }
            }
            planes[c] = plane;
        }
        return new Image(planes, BitDepth.Float32, starless.MaxValue, starless.MinValue, starless.Pedestal, starless.ImageMeta);
    }

    // The Chebyshev distance from (px, py) to the frame's border or the nearest absent pixel, capped at reach.
    private static int EdgeDistance(bool[] present, int width, int height, int px, int py, int reach)
    {
        var d = Math.Min(Math.Min(px, py), Math.Min(width - 1 - px, height - 1 - py));
        d = Math.Min(d, reach);
        for (var y = Math.Max(0, py - reach); y <= Math.Min(height - 1, py + reach); y++)
        {
            for (var x = Math.Max(0, px - reach); x <= Math.Min(width - 1, px + reach); x++)
            {
                if (!present[(y * width) + x])
                {
                    d = Math.Min(d, Math.Max(Math.Abs(x - px), Math.Abs(y - py)));
                }
            }
        }
        return d;
    }

    // A flat top as the clipped-star rule reads it.
    private readonly record struct FlatTop(int AreaPx, double Roundness, double Fall)
    {
        public static readonly FlatTop None = new(0, 0, 1);

        public bool IsStarTop => AreaPx >= MinStarTopPx && Roundness >= MinStarTopRoundness && Fall <= MaxStarTopFall;
    }

    // The flat top at a source's peak: the connected pixels standing at FlatTopShare of the brightest light within half the
    // median window of (px, py), how round they are, and how far the light has fallen two top radii out from their centre.
    // None where the top does not hold the map's peak (its centre lies further from it than its own radius and a block) or
    // runs past twice the window, which is wider than a star's top this rule is about.
    private static FlatTop FlatTopAt(float[] luminance, bool[] present, int width, int height, int px, int py, float sky)
    {
        const int search = FilterBlocks * BlockPx / 2;
        const int bound = 2 * FilterBlocks * BlockPx;
        var (bx, by, best) = (-1, -1, float.NegativeInfinity);
        for (var y = Math.Max(0, py - search); y <= Math.Min(height - 1, py + search); y++)
        {
            for (var x = Math.Max(0, px - search); x <= Math.Min(width - 1, px + search); x++)
            {
                var i = (y * width) + x;
                if (present[i] && luminance[i] > best)
                {
                    (bx, by, best) = (x, y, luminance[i]);
                }
            }
        }
        var light = best - sky;
        if (bx < 0 || !(light > 0))
        {
            return FlatTop.None;
        }
        var floor = sky + (float)(FlatTopShare * light);
        const int side = (2 * bound) + 1;
        var seen = new bool[side * side];
        var open = new Stack<(int X, int Y)>();
        open.Push((0, 0));
        seen[(bound * side) + bound] = true;
        var count = 0;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        // Offsets from the brightest pixel, so the moments keep their digits.
        while (open.TryPop(out var at))
        {
            count++;
            sx += at.X;
            sy += at.Y;
            sxx += at.X * at.X;
            syy += at.Y * at.Y;
            sxy += at.X * at.Y;
            foreach (var (dx, dy) in (ReadOnlySpan<(int, int)>)[(at.X - 1, at.Y), (at.X + 1, at.Y), (at.X, at.Y - 1), (at.X, at.Y + 1)])
            {
                var (nx, ny) = (bx + dx, by + dy);
                if (nx < 0 || ny < 0 || nx >= width || ny >= height || !present[(ny * width) + nx] || luminance[(ny * width) + nx] < floor)
                {
                    continue;
                }
                if (Math.Abs(dx) > bound || Math.Abs(dy) > bound)
                {
                    return FlatTop.None;
                }
                var k = ((dy + bound) * side) + dx + bound;
                if (!seen[k])
                {
                    seen[k] = true;
                    open.Push((dx, dy));
                }
            }
        }
        var (cx, cy) = (sx / count, sy / count);
        var radius = Math.Sqrt(count / Math.PI);
        var (tx, ty) = (bx + cx, by + cy);
        if (Math.Sqrt(((tx - px) * (tx - px)) + ((ty - py) * (ty - py))) > radius + BlockPx)
        {
            return FlatTop.None;
        }

        // Roundness from the second moments (a pixel is a square, so each variance carries its own 1/12): the axis ratio,
        // times how well the top fills the ellipse of those moments. A disc of radius R has R^2 / 4 on each axis, so a filled
        // ellipse holds 4 pi sqrt(major minor) pixels; a ragged or hollow top holds fewer.
        var vxx = (sxx / count) - (cx * cx) + (1.0 / 12);
        var vyy = (syy / count) - (cy * cy) + (1.0 / 12);
        var vxy = (sxy / count) - (cx * cy);
        var mean = (vxx + vyy) / 2;
        var spread = Math.Sqrt(Math.Max(0, (mean * mean) - ((vxx * vyy) - (vxy * vxy))));
        var (major, minor) = (mean + spread, Math.Max(mean - spread, 1e-12));
        var roundness = Math.Sqrt(minor / major) * Math.Min(1.0, count / (4 * Math.PI * Math.Sqrt(major * minor)));

        // The light two radii out, in a ring two pixels wide, by its median.
        var inner = 2 * radius;
        var reach = (int)Math.Ceiling(inner + 2);
        var ring = new List<float>();
        for (var y = Math.Max(0, (int)Math.Floor(ty) - reach); y <= Math.Min(height - 1, (int)Math.Ceiling(ty) + reach); y++)
        {
            for (var x = Math.Max(0, (int)Math.Floor(tx) - reach); x <= Math.Min(width - 1, (int)Math.Ceiling(tx) + reach); x++)
            {
                var d = Math.Sqrt(((x - tx) * (x - tx)) + ((y - ty) * (y - ty)));
                if (d >= inner && d < inner + 2 && present[(y * width) + x])
                {
                    ring.Add(luminance[(y * width) + x]);
                }
            }
        }
        var fall = ring.Count > 0
            ? Math.Max(0, (StatisticsHelper.MedianFast(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ring)) - sky) / light)
            : 1.0;
        return new FlatTop(count, roundness, fall);
    }

    // The 90th percentile of the full-resolution light above the sky in a box of the given side about (px, py), over the
    // large-scale map's peak above the sky.
    private static double PeakRatio(float[] luminance, bool[] present, int width, int height, int px, int py, int side, float sky, float mapPeak)
    {
        var half = Math.Max(1, side) / 2;
        var values = new List<float>();
        for (var y = Math.Max(0, py - half); y < Math.Min(height, py + half); y++)
        {
            for (var x = Math.Max(0, px - half); x < Math.Min(width, px + half); x++)
            {
                if (present[(y * width) + x])
                {
                    values.Add(luminance[(y * width) + x] - sky);
                }
            }
        }
        if (values.Count == 0 || !(mapPeak > 0))
        {
            return 0;
        }
        values.Sort();
        return values[Math.Min(values.Count - 1, (int)(0.9 * values.Count))] / mapPeak;
    }

    // The per-pixel sky sigma from horizontal SECOND differences (x[i-1] - 2 x[i] + x[i+1], variance 6 sigma^2), which a
    // galaxy's gradient does not reach as first differences do (15 % high on a steep one): their median, or on quantised
    // data, whose differences are mostly zero, their spread clipped at five times its first reading.
    private static double SkySigmaOf(float[] luminance, bool[] present, int width, int height)
    {
        var step = Math.Max(1, (int)((long)width * height / 4_000_000));
        var diffs = new List<float>();
        for (var y = 0; y < height; y += step)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = (y * width) + x;
                if (present[i - 1] && present[i] && present[i + 1])
                {
                    diffs.Add(luminance[i - 1] - (2f * luminance[i]) + luminance[i + 1]);
                }
            }
        }
        if (diffs.Count == 0)
        {
            return double.NaN;
        }
        var abs = new float[diffs.Count];
        for (var i = 0; i < abs.Length; i++)
        {
            abs[i] = Math.Abs(diffs[i]);
        }
        var mad = StatisticsHelper.MedianFast(abs);
        if (mad > 0)
        {
            return 1.4826 * mad / Math.Sqrt(6);
        }
        double sum = 0, sq = 0;
        foreach (var d in diffs)
        {
            sum += d;
            sq += (double)d * d;
        }
        var spread = Math.Sqrt(Math.Max(0, (sq / diffs.Count) - ((sum / diffs.Count) * (sum / diffs.Count))));
        double cs = 0, csq = 0;
        var n = 0;
        foreach (var d in diffs)
        {
            if (Math.Abs(d) < 5 * spread)
            {
                cs += d;
                csq += (double)d * d;
                n++;
            }
        }
        return n > 1 ? Math.Sqrt(Math.Max(0, (csq / n) - ((cs / n) * (cs / n)))) / Math.Sqrt(6) : double.NaN;
    }
}
