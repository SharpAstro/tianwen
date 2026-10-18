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
/// there, feathered, keeps the core whatever the remover did, at the price of the few stars that sit inside it. The design
/// and its measurements: docs/plans/star-remover-training.md, "Bright compact structure taken as stars".
/// </summary>
/// <remarks>
/// <para>The large-scale map is the luminance's <see cref="BlockPx"/> block median, median-filtered over
/// <see cref="FilterBlocks"/> blocks a side (56 px): a star covers too little of any window to survive it, an extended source
/// does. An extended source is a connected region of that map standing <see cref="Options.ExtendedSigma"/> per-pixel sky
/// sigmas over the sky. One whose own peak stands <see cref="Options.MinPeakSigma"/> sigmas or more gets a core: where the
/// map is at least <see cref="Options.CoreShare"/> of that peak, at most <see cref="Options.MaxAreaShare"/> of the source's
/// area, its brightest blocks first, so a nebula filling the frame gives up only its brightest part.</para>
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
    public sealed record Options(
        double CoreShare = 0.5,
        double MaxAreaShare = 0.01,
        double ExtendedSigma = 3.0,
        double MinPeakSigma = 50.0,
        int MinSourceBlocks = 16,
        int FeatherRadiusPx = 4)
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
    public readonly record struct Core(int X, int Y, double PeakSigma, int SourceAreaPx, int CoreAreaPx);

    private ExtendedCoreMask(int width, int height, float[] weight, ImmutableArray<Core> cores, double skySigma)
    {
        Width = width;
        Height = height;
        Weight = weight;
        Cores = cores;
        SkySigma = skySigma;
    }

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
            foreach (var m in top)
            {
                keep[m] = true;
            }
            found.Add((new Core(((peakAt % gw) * BlockPx) + (BlockPx / 2), ((peakAt / gw) * BlockPx) + (BlockPx / 2),
                peak / sigma, members.Count * BlockPx * BlockPx, top.Count * BlockPx * BlockPx), peak));
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
