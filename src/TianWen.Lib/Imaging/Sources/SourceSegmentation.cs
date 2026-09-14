using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Sources;

/// <summary>
/// Options for <see cref="SourceSegmentation.Detect(ReadOnlySpan{float}, int, int, BackgroundMap, SourceDetectionOptions?)"/>.
/// </summary>
/// <param name="ThresholdSigma">A pixel joins a source when it stands this many local RMS over the local
/// sky (<see cref="BackgroundMap"/>). Photutils' <c>detect_sources</c> convention.</param>
/// <param name="MinPixels">Connected pixels a source needs to be kept; smaller islands are noise.</param>
/// <param name="Deblend">Split a segment that holds several saddle-separated peaks into one segment per
/// peak by a watershed from the peaks down.</param>
/// <param name="DeblendSaddleFraction">Two peaks are separate sources when the lowest pixel on the line
/// between them lies under this fraction of the fainter peak's height over the sky, read on the smoothed
/// plane. 0.7 is where two measured cases meet: a 0.3 and 0.2 pair 6 px apart at 1.5 px sigma dips to 0.62
/// of the fainter and must split (0.5 kept it whole), a 4-sigma knot on a 20-sigma nebula dips to about
/// 0.8 and must not (the star deblender's 0.85 would carve it off).</param>
/// <param name="DeblendMinSeparation">Peaks closer than this, in pixels, are one source whatever the saddle says.</param>
/// <param name="DeblendMinPeakSigma">A peak is considered for deblending only when it stands this many
/// local RMS over the sky.</param>
/// <param name="DeblendMaxPeaks">Peaks considered per segment, brightest first. A Milky Way field's faint
/// nebulosity joins into one segment of millions of pixels with thousands of local maxima, and the saddle
/// test over every pair took eight minutes on the Statue master; the brightest 64 are the sources worth
/// splitting off.</param>
/// <param name="CompactCoreFraction">A segment is <see cref="Segment.IsCompact"/> (a star) when the sky-subtracted
/// flux inside the 5 by 5 window on its peak is at least this fraction of the whole segment's, and its
/// area is under <paramref name="CompactMaxArea"/>. A nebula segment spreads its flux; a star concentrates it.</param>
/// <param name="CompactMaxArea">Area above which the core fraction alone cannot make a segment compact.</param>
/// <param name="CompactPeakToMean">The second way to be compact, for a star whose skirt or diffraction
/// spikes hold most of its flux (a 7000-sigma star on a Newtonian reads a core fraction of 0.1 over 9500 px):
/// the peak over the segment's mean pixel, both sky-subtracted. A star of any brightness peaks far above
/// its mean; a nebula's peak is a few times its mean. The Bubble frame read stars at 30 to 200 and
/// nebula pieces at 2 to 6.</param>
/// <param name="SmoothingSigma">Gaussian sigma, in pixels, of the smoothing applied to the sky-subtracted
/// plane BEFORE thresholding (measurements are taken on the unsmoothed plane). The threshold is scaled by
/// the kernel's noise reduction, so "3 sigma" stays 3 sigma of the smoothed noise; what the smoothing
/// buys is fewer one-pixel noise islands and a nebula's faint wing joining its body instead of
/// fragmenting. 0 disables it. Photutils' detection kernel plays the same role.</param>
/// <param name="BackgroundPasses">Background map passes. With 2 (the default) the sources found on the
/// first pass are masked (dilated by <paramref name="BackgroundMaskMargin"/>) and the map re-estimated
/// without them, so a source wider than a mesh cell is not read as sky and cut at its own level.</param>
/// <param name="BackgroundMaskMargin">Dilation of the first-pass mask before the second background pass.</param>
/// <param name="BackgroundMaskSigma">The first-pass mask for the second background pass is everything over
/// THIS many sigmas on the smoothed plane, lower than <paramref name="ThresholdSigma"/>, so a nebula's faint
/// wing leaves the sky cells too. Masking only the detected segments left the wing in the mesh, which
/// lifted the sky under the nebula and cut it into pieces at its own level.</param>
public sealed record SourceDetectionOptions(
    float ThresholdSigma = 3f,
    int MinPixels = 5,
    bool Deblend = true,
    float DeblendSaddleFraction = 0.7f,
    float DeblendMinSeparation = 3f,
    float DeblendMinPeakSigma = 5f,
    int DeblendMaxPeaks = 64,
    float CompactCoreFraction = 0.5f,
    int CompactMaxArea = 400,
    float CompactPeakToMean = 10f,
    float SmoothingSigma = 1f,
    int BackgroundPasses = 2,
    int BackgroundMaskMargin = 5,
    float BackgroundMaskSigma = 1f)
{
    public static SourceDetectionOptions Default { get; } = new SourceDetectionOptions();

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ThresholdSigma, 0f);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinPixels, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(SmoothingSigma);
        ArgumentOutOfRangeException.ThrowIfLessThan(BackgroundPasses, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(BackgroundMaskMargin);
        ArgumentOutOfRangeException.ThrowIfNegative(BackgroundMaskSigma);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DeblendSaddleFraction, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DeblendSaddleFraction, 1f);
        ArgumentOutOfRangeException.ThrowIfLessThan(DeblendMinSeparation, 1f);
        ArgumentOutOfRangeException.ThrowIfLessThan(DeblendMinPeakSigma, 0f);
        ArgumentOutOfRangeException.ThrowIfLessThan(DeblendMaxPeaks, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(CompactCoreFraction, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CompactCoreFraction, 1f);
        ArgumentOutOfRangeException.ThrowIfLessThan(CompactMaxArea, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CompactPeakToMean, 1f);
    }
}

/// <summary>
/// One detected source: a connected set of pixels over the threshold, after deblending.
/// </summary>
/// <param name="Label">Its value in <see cref="SegmentationMap.LabelAt"/>; 1-based, 0 is sky.</param>
/// <param name="Area">Pixels in the segment.</param>
/// <param name="XCentroid">Sky-subtracted flux-weighted centroid, 0-based pixel coordinates (the detected-centroid frame every <see cref="Image"/> measurement uses).</param>
/// <param name="YCentroid">Likewise.</param>
/// <param name="PeakX">Brightest pixel's column.</param>
/// <param name="PeakY">Brightest pixel's row.</param>
/// <param name="Peak">Brightest pixel's value over the local sky.</param>
/// <param name="Flux">Sum of the sky-subtracted pixels.</param>
/// <param name="X0">Bounding box, inclusive.</param>
/// <param name="Y0">Bounding box, inclusive.</param>
/// <param name="X1">Bounding box, inclusive.</param>
/// <param name="Y1">Bounding box, inclusive.</param>
/// <param name="Elongation">Ratio of the major to the minor axis from the flux-weighted second moments; 1 is round.</param>
/// <param name="CoreFraction">Share of <paramref name="Flux"/> inside the 5 by 5 window on the peak.</param>
/// <param name="PeakToMean">The peak over the segment's mean sky-subtracted pixel: a star's concentration whatever its skirt.</param>
/// <param name="IsCompact">A star-like source (see <see cref="SourceDetectionOptions.CompactCoreFraction"/> and
/// <see cref="SourceDetectionOptions.CompactPeakToMean"/>); the rest is structure.</param>
public readonly record struct Segment(
    int Label,
    int Area,
    float XCentroid,
    float YCentroid,
    int PeakX,
    int PeakY,
    float Peak,
    float Flux,
    int X0,
    int Y0,
    int X1,
    int Y1,
    float Elongation,
    float CoreFraction,
    float PeakToMean,
    bool IsCompact);

/// <summary>
/// The result of <see cref="SourceSegmentation.Detect(ReadOnlySpan{float}, int, int, BackgroundMap, SourceDetectionOptions?)"/>:
/// a label per pixel (0 is sky) and a <see cref="Segment"/> per label, with the masks derived from them.
/// </summary>
public sealed class SegmentationMap
{
    private readonly int[] _labels;

    internal SegmentationMap(int width, int height, int[] labels, ImmutableArray<Segment> segments)
    {
        Width = width;
        Height = height;
        _labels = labels;
        Segments = segments;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Every source, ordered by label; <c>Segments[i].Label == i + 1</c>.</summary>
    public ImmutableArray<Segment> Segments { get; }

    /// <summary>Row-major labels, <see cref="Width"/> x <see cref="Height"/>; 0 is sky.</summary>
    public ReadOnlySpan<int> Labels => _labels;

    public int LabelAt(int x, int y) => _labels[y * Width + x];

    /// <summary>Every source pixel, dilated by <paramref name="marginPx"/> (0 for the bare segments).</summary>
    public BitMatrix SourceMask(int marginPx = 0) => BuildMask(static s => true, marginPx);

    /// <summary>Compact (star-like) sources, dilated by <paramref name="marginPx"/>: the mask to protect stars or to exclude them.</summary>
    public BitMatrix StarMask(int marginPx = 3) => BuildMask(static s => s.IsCompact, marginPx);

    /// <summary>Extended sources: nebulosity, galaxies, anything a star is not.</summary>
    public BitMatrix StructureMask(int marginPx = 0) => BuildMask(static s => !s.IsCompact, marginPx);

    /// <summary>Pixels that are no source and not within <paramref name="marginPx"/> of one: where the sky and its noise are read.</summary>
    public BitMatrix SkyMask(int marginPx = 3)
    {
        var sources = SourceMask(marginPx);
        var sky = new BitMatrix(Height, Width);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                sky[y, x] = !sources[y, x];
            }
        }

        return sky;
    }

    private BitMatrix BuildMask(Func<Segment, bool> select, int marginPx)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(marginPx);
        var selected = new bool[Segments.Length + 1];
        foreach (var s in Segments)
        {
            selected[s.Label] = select(s);
        }

        var mask = new BitMatrix(Height, Width);
        for (var y = 0; y < Height; y++)
        {
            var row = y * Width;
            for (var x = 0; x < Width; x++)
            {
                if (selected[_labels[row + x]])
                {
                    mask[y, x] = true;
                }
            }
        }

        // The margin is a square dilation on the mask's own words; a disc stamped per source pixel was
        // O(pixels x margin squared) and ran for minutes on a 3840 by 2160 frame, and a boolean plane
        // per mask cost 90 MB on a 16 Mpx frame.
        mask.DilateSquare(marginPx);
        return mask;
    }
}

/// <summary>Boolean-plane helpers shared by the masks.</summary>
internal static class MaskOps
{
    /// <summary>In-place dilation by a (2r+1)-square, as two sliding-window passes.</summary>
    internal static void DilateSquare(bool[] flags, int width, int height, int radius)
    {
        var temp = new bool[flags.Length];
        // Rows: temp[x] = any flags[x-r .. x+r].
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var run = 0; // pixels since the last set flag, counting the set one as 0
            // Forward pass marks x .. x+r after a set flag; backward pass marks x-r .. x before it.
            run = int.MaxValue;
            for (var x = 0; x < width; x++)
            {
                if (flags[row + x])
                {
                    run = 0;
                }
                else if (run != int.MaxValue)
                {
                    run++;
                }

                temp[row + x] = run <= radius;
            }

            run = int.MaxValue;
            for (var x = width - 1; x >= 0; x--)
            {
                if (flags[row + x])
                {
                    run = 0;
                }
                else if (run != int.MaxValue)
                {
                    run++;
                }

                if (run <= radius)
                {
                    temp[row + x] = true;
                }
            }
        }

        // Columns: flags[y] = any temp[y-r .. y+r].
        for (var x = 0; x < width; x++)
        {
            var run = int.MaxValue;
            for (var y = 0; y < height; y++)
            {
                if (temp[y * width + x])
                {
                    run = 0;
                }
                else if (run != int.MaxValue)
                {
                    run++;
                }

                flags[y * width + x] = run <= radius;
            }

            run = int.MaxValue;
            for (var y = height - 1; y >= 0; y--)
            {
                if (temp[y * width + x])
                {
                    run = 0;
                }
                else if (run != int.MaxValue)
                {
                    run++;
                }

                if (run <= radius)
                {
                    flags[y * width + x] = true;
                }
            }
        }
    }

    internal static BitMatrix ToBitMatrix(bool[] flags, int width, int height)
    {
        var mask = new BitMatrix(height, width);
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (flags[row + x])
                {
                    mask[y, x] = true;
                }
            }
        }

        return mask;
    }
}

/// <summary>
/// Source detection by thresholding over a <see cref="BackgroundMap"/>, 8-connected labelling, and a
/// watershed deblend from saddle-separated peaks: the shape of photutils' <c>detect_sources</c> and
/// <c>deblend_sources</c>, on one plane, with a compact-versus-extended flag per source so the
/// consumers that want stars and those that want structure read one map.
/// </summary>
/// <remarks>
/// <para>Why beside <see cref="Image.FindStarsAsync"/>: the star detector answers "where are the stars
/// and how wide", with a box around each; it has no notion of a source that is not a star, and its
/// mask is stamped discs. This labels EVERY connected region over the local threshold, so a nebula is
/// a segment with an area and a boundary, a star is a segment with a concentrated core, and the sky is
/// label 0. The two agree on stars by construction only roughly; a caller measuring stars keeps the
/// star detector.</para>
/// <para>Deblending: peaks are strict 3 by 3 maxima over <see cref="SourceDetectionOptions.DeblendMinPeakSigma"/>;
/// a fainter peak survives against every brighter one only if the lowest pixel on the line between
/// them lies under <see cref="SourceDetectionOptions.DeblendSaddleFraction"/> of its own height, the
/// same saddle test <c>Image.StarDeblend</c> uses with a stricter fraction. The segment's pixels are
/// then flooded from the surviving peaks in descending order of value, each pixel taking the label of
/// its brightest already-labelled neighbour, which is a watershed on the intensity.</para>
/// </remarks>
public static class SourceSegmentation
{
    /// <summary>Detect on one channel of an image.</summary>
    public static SegmentationMap Detect(Image image, int channel, BackgroundMap background, SourceDetectionOptions? options = null)
        => Detect(image.GetChannelSpan(channel), image.Width, image.Height, background, options);

    /// <summary>Detect on one channel of an image, estimating the background map with default options first.</summary>
    public static SegmentationMap Detect(Image image, int channel, SourceDetectionOptions? options = null)
        => Detect(image, channel, BackgroundMap.Estimate(image, channel), options);

    /// <summary>
    /// Detect on a row-major plane against its background map. With <see cref="SourceDetectionOptions.BackgroundPasses"/>
    /// over 1 the map is re-estimated with the first pass's sources masked, from the same plane and the
    /// map's own block size, and the detection repeated on the refined map.
    /// </summary>
    public static SegmentationMap Detect(ReadOnlySpan<float> plane, int width, int height, BackgroundMap background, SourceDetectionOptions? options = null)
    {
        options ??= SourceDetectionOptions.Default;
        options.Validate();
        if (plane.Length != width * height)
        {
            throw new ArgumentException($"plane has {plane.Length} samples for {width}x{height}", nameof(plane));
        }

        if (background.Width != width || background.Height != height)
        {
            throw new ArgumentException($"background map is {background.Width}x{background.Height} for a {width}x{height} plane", nameof(background));
        }

        // The per-pass planes are allocated once and reused by the second pass: the sky-subtracted plane,
        // its smoothed copy, the threshold flags and the labels are 36 bytes a pixel, 601 MB over two
        // passes on a 16 Mpx frame before this.
        var buffers = new DetectBuffers(width * height, options.SmoothingSigma > 0f);
        var (map, lowMask) = DetectOnce(plane, width, height, background, options, buffers);
        for (var pass = 1; pass < options.BackgroundPasses; pass++)
        {
            if (map.Segments.Length == 0)
            {
                break;
            }

            lowMask.DilateSquare(options.BackgroundMaskMargin);
            var refined = BackgroundMap.Estimate(plane, width, height, lowMask, new BackgroundMapOptions(BlockSize: background.BlockSize));
            (map, lowMask) = DetectOnce(plane, width, height, refined, options, buffers);
        }

        return map;
    }

    private sealed class DetectBuffers(int n, bool smoothing)
    {
        public float[] Signal { get; } = new float[n];
        public float[] Detect { get; } = smoothing ? new float[n] : [];
        public float[] SmoothTemp { get; } = smoothing ? new float[n] : [];
        public bool[] Above { get; } = new bool[n];
        public int[] Position { get; } = new int[n];
    }

    private static (SegmentationMap Map, BitMatrix LowMask) DetectOnce(ReadOnlySpan<float> plane, int width, int height, BackgroundMap background, SourceDetectionOptions options, DetectBuffers buffers)
    {
        var n = width * height;
        var signal = buffers.Signal;   // sky-subtracted value, NaN where the pixel is not finite
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var v = plane[row + x];
                signal[row + x] = float.IsFinite(v) ? v - background.BackgroundAt(x, y) : float.NaN;
            }
        }

        // Detection on the smoothed signal, measurement on the signal itself. The threshold stays in the
        // UNSMOOTHED noise's sigmas (photutils' convention: detect_threshold on the data, applied to the
        // convolved data). Scaling it down by the kernel's noise gain was tried and read 88 segments on a
        // frame with six sources: correlated noise clusters past any pixel-count floor at 3 smoothed sigmas.
        var detect = signal;
        if (options.SmoothingSigma > 0f)
        {
            detect = buffers.Detect;
            SmoothForDetection(signal, width, height, options.SmoothingSigma, detect, buffers.SmoothTemp);
        }

        var above = buffers.Above;
        Array.Clear(above);
        var low = new BitMatrix(height, width);
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var d = detect[row + x];
                if (!float.IsFinite(d))
                {
                    continue;
                }

                var rms = background.RmsAt(x, y);
                above[row + x] = d > options.ThresholdSigma * rms;
                if (d > options.BackgroundMaskSigma * rms)
                {
                    low[y, x] = true;
                }
            }
        }

        // The labels are the map's own array and cannot be reused across passes; the pass before is dropped.
        var labels = new int[n];
        var count = LabelConnected(above, width, height, labels);
        count = DropSmall(labels, count, options.MinPixels);
        if (options.Deblend && count > 0)
        {
            // Peaks and saddles are read on the SMOOTHED plane (photutils deblends the convolved data):
            // on the raw one every noise bump on a nebula's surface is a strict maximum.
            count = Deblend(detect, labels, count, width, height, background, options, buffers.Position);
        }

        var segments = Measure(signal, labels, count, width, height, options);
        return (new SegmentationMap(width, height, labels, segments), low);
    }

    /// <summary>
    /// Separable Gaussian smoothing of the sky-subtracted plane (NaN read as zero, edges clamped), returning
    /// the factor by which white noise's sigma shrinks under the kernel, so the caller's threshold can be
    /// expressed in the smoothed noise's own sigma.
    /// </summary>
    internal static float SmoothForDetection(float[] signal, int width, int height, float sigma, float[] destination)
        => SmoothForDetection(signal, width, height, sigma, destination, new float[signal.Length]);

    internal static float SmoothForDetection(float[] signal, int width, int height, float sigma, float[] destination, float[] temp)
    {
        var radius = Math.Max(1, (int)MathF.Ceiling(3f * sigma));
        var kernel = new float[2 * radius + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            sum += kernel[i + radius];
        }

        var sumSq = 0f;
        for (var i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
            sumSq += kernel[i] * kernel[i];
        }

        // Two separable passes: the 2D kernel's sum of squares is the square of the 1D one's.
        var noiseGain = sumSq;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var acc = 0f;
                for (var k = -radius; k <= radius; k++)
                {
                    var xx = Math.Clamp(x + k, 0, width - 1);
                    var v = signal[row + xx];
                    acc += kernel[k + radius] * (float.IsNaN(v) ? 0f : v);
                }

                temp[row + x] = acc;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var acc = 0f;
                for (var k = -radius; k <= radius; k++)
                {
                    var yy = Math.Clamp(y + k, 0, height - 1);
                    acc += kernel[k + radius] * temp[yy * width + x];
                }

                destination[y * width + x] = acc;
            }
        }

        return noiseGain;
    }

    /// <summary>Two-pass 8-connected labelling with union-find; returns the label count (labels 1..count, compacted).</summary>
    internal static int LabelConnected(bool[] above, int width, int height, int[] labels)
    {
        var parent = new List<int> { 0 };

        int Find(int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }

            return a;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
            {
                if (ra < rb)
                {
                    parent[rb] = ra;
                }
                else
                {
                    parent[ra] = rb;
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = row + x;
                if (!above[i])
                {
                    continue;
                }

                var best = 0;
                // West, north-west, north, north-east: the neighbours already visited in raster order.
                if (x > 0 && labels[i - 1] != 0)
                {
                    best = labels[i - 1];
                }

                if (y > 0)
                {
                    var up = i - width;
                    if (x > 0 && labels[up - 1] != 0)
                    {
                        best = Merge(best, labels[up - 1]);
                    }

                    if (labels[up] != 0)
                    {
                        best = Merge(best, labels[up]);
                    }

                    if (x + 1 < width && labels[up + 1] != 0)
                    {
                        best = Merge(best, labels[up + 1]);
                    }
                }

                if (best == 0)
                {
                    best = parent.Count;
                    parent.Add(best);
                }

                labels[i] = best;

                int Merge(int current, int other)
                {
                    if (current == 0)
                    {
                        return other;
                    }

                    Union(current, other);
                    return current;
                }
            }
        }

        // Resolve roots and compact to 1..count.
        var remap = new int[parent.Count];
        var count = 0;
        for (var p = 1; p < parent.Count; p++)
        {
            var root = Find(p);
            if (remap[root] == 0)
            {
                remap[root] = ++count;
            }

            remap[p] = remap[root];
        }

        for (var i = 0; i < labels.Length; i++)
        {
            if (labels[i] != 0)
            {
                labels[i] = remap[labels[i]];
            }
        }

        return count;
    }

    /// <summary>Segments under <paramref name="minPixels"/> become sky; the rest are relabelled 1..count.</summary>
    internal static int DropSmall(int[] labels, int count, int minPixels)
    {
        var area = new int[count + 1];
        foreach (var l in labels)
        {
            area[l]++;
        }

        var remap = new int[count + 1];
        var kept = 0;
        for (var l = 1; l <= count; l++)
        {
            remap[l] = area[l] >= minPixels ? ++kept : 0;
        }

        for (var i = 0; i < labels.Length; i++)
        {
            labels[i] = remap[labels[i]];
        }

        return kept;
    }

    private readonly record struct Peak(int X, int Y, float Value);

    /// <summary>Split every segment with several saddle-separated peaks; returns the new label count.</summary>
    /// <param name="position">A frame-sized scratch array (the pixel's rank in its segment's descending order); every entry touched is reset before return.</param>
    private static int Deblend(float[] signal, int[] labels, int count, int width, int height, BackgroundMap background, SourceDetectionOptions options, int[] position)
    {
        // Gather each segment's pixels once.
        var area = new int[count + 1];
        foreach (var l in labels)
        {
            area[l]++;
        }

        var start = new int[count + 2];
        for (var l = 1; l <= count; l++)
        {
            start[l + 1] = start[l] + area[l];
        }

        var pixels = new int[start[count + 1]];
        var fill = (int[])start.Clone();
        for (var i = 0; i < labels.Length; i++)
        {
            var l = labels[i];
            if (l != 0)
            {
                pixels[fill[l]++] = i;
            }
        }

        var next = count;
        var peaks = new List<Peak>();
        for (var l = 1; l <= count; l++)
        {
            var seg = pixels.AsSpan(start[l], area[l]);
            if (seg.Length < 2 * options.MinPixels)
            {
                continue;
            }

            peaks.Clear();
            foreach (var i in seg)
            {
                var x = i % width;
                var y = i / width;
                var v = signal[i];
                if (!(v > options.DeblendMinPeakSigma * background.RmsAt(x, y)))
                {
                    continue;
                }

                if (IsStrictMaximum(signal, labels, l, x, y, width, height))
                {
                    peaks.Add(new Peak(x, y, v));
                }
            }

            if (peaks.Count < 2)
            {
                continue;
            }

            peaks.Sort(static (a, b) => b.Value.CompareTo(a.Value));
            if (peaks.Count > options.DeblendMaxPeaks)
            {
                peaks.RemoveRange(options.DeblendMaxPeaks, peaks.Count - options.DeblendMaxPeaks);
            }

            var survivors = new List<Peak>(peaks.Count) { peaks[0] };
            for (var c = 1; c < peaks.Count; c++)
            {
                var candidate = peaks[c];
                var separate = true;
                foreach (var brighter in survivors)
                {
                    if (!IsSaddleSeparated(signal, width, height, brighter, candidate, options))
                    {
                        separate = false;
                        break;
                    }
                }

                if (separate)
                {
                    survivors.Add(candidate);
                }
            }

            if (survivors.Count < 2)
            {
                continue;
            }

            // Watershed: the brightest survivor keeps the segment's label, the others take new ones; then every
            // pixel in descending order of value joins its brightest already-labelled neighbour.
            var peakLabel = new int[survivors.Count];
            peakLabel[0] = l;
            for (var k = 1; k < survivors.Count; k++)
            {
                peakLabel[k] = ++next;
            }

            var order = seg.ToArray();
            Array.Sort(order, (a, b) => signal[b].CompareTo(signal[a]));
            var assigned = new int[order.Length];   // parallel to order; position[] maps a pixel back to its rank
            for (var k = 0; k < order.Length; k++)
            {
                position[order[k]] = k;
            }

            for (var k = 0; k < survivors.Count; k++)
            {
                assigned[position[survivors[k].Y * width + survivors[k].X]] = peakLabel[k];
            }

            foreach (var i in order)
            {
                var k = position[i];
                if (assigned[k] != 0)
                {
                    continue;
                }

                var x = i % width;
                var y = i / width;
                var bestLabel = 0;
                var bestValue = float.NegativeInfinity;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if ((dx | dy) == 0 || nx < 0 || nx >= width)
                        {
                            continue;
                        }

                        var ni = ny * width + nx;
                        if (labels[ni] != l)
                        {
                            continue;
                        }

                        var nk = position[ni];
                        if (assigned[nk] == 0)
                        {
                            continue;
                        }

                        if (signal[ni] > bestValue)
                        {
                            bestValue = signal[ni];
                            bestLabel = assigned[nk];
                        }
                    }
                }

                // A pruned local maximum has no labelled neighbour yet: it joins the nearest surviving peak.
                if (bestLabel == 0)
                {
                    var bestDistance = float.PositiveInfinity;
                    for (var s = 0; s < survivors.Count; s++)
                    {
                        var dx = survivors[s].X - x;
                        var dy = survivors[s].Y - y;
                        var d = dx * dx + dy * dy;
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            bestLabel = peakLabel[s];
                        }
                    }
                }

                assigned[k] = bestLabel;
            }

            for (var k = 0; k < order.Length; k++)
            {
                labels[order[k]] = assigned[k];
                position[order[k]] = 0;
            }
        }

        return next;
    }

    private static bool IsStrictMaximum(float[] signal, int[] labels, int label, int x, int y, int width, int height)
    {
        var centre = signal[y * width + x];
        for (var dy = -1; dy <= 1; dy++)
        {
            var ny = y + dy;
            if (ny < 0 || ny >= height)
            {
                continue;
            }

            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                if ((dx | dy) == 0 || nx < 0 || nx >= width)
                {
                    continue;
                }

                var ni = ny * width + nx;
                if (labels[ni] == label && signal[ni] >= centre)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The star deblender's rule (<c>Image.StarDeblend.IsSaddleSeparated</c>) at this class's fraction:
    /// quarter-pixel steps along the line, the dip has to fall under the fraction of the fainter peak.
    /// </summary>
    private static bool IsSaddleSeparated(float[] signal, int width, int height, Peak brighter, Peak fainter, SourceDetectionOptions options)
    {
        var dx = fainter.X - brighter.X;
        var dy = fainter.Y - brighter.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < options.DeblendMinSeparation)
        {
            return false;
        }

        // Quarter-pixel steps: the dip between two maxima a few pixels apart is one pixel wide, and a
        // coarser walk steps over it (the star deblender's lesson).
        var steps = (int)MathF.Ceiling(length * 4f);
        var lowest = float.MaxValue;
        for (var s = 1; s < steps; s++)
        {
            var t = (float)s / steps;
            var x = (int)MathF.Round(brighter.X + dx * t);
            var y = (int)MathF.Round(brighter.Y + dy * t);
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                continue;
            }

            var v = signal[y * width + x];
            if (!float.IsNaN(v) && v < lowest)
            {
                lowest = v;
            }
        }

        return lowest < options.DeblendSaddleFraction * fainter.Value;
    }

    private static ImmutableArray<Segment> Measure(float[] signal, int[] labels, int count, int width, int height, SourceDetectionOptions options)
    {
        if (count == 0)
        {
            return [];
        }

        var area = new int[count + 1];
        var flux = new double[count + 1];
        var sx = new double[count + 1];
        var sy = new double[count + 1];
        var sxx = new double[count + 1];
        var syy = new double[count + 1];
        var sxy = new double[count + 1];
        var peak = new float[count + 1];
        var peakX = new int[count + 1];
        var peakY = new int[count + 1];
        var x0 = new int[count + 1];
        var y0 = new int[count + 1];
        var x1 = new int[count + 1];
        var y1 = new int[count + 1];
        Array.Fill(x0, int.MaxValue);
        Array.Fill(y0, int.MaxValue);
        Array.Fill(x1, int.MinValue);
        Array.Fill(y1, int.MinValue);
        Array.Fill(peak, float.NegativeInfinity);

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var l = labels[row + x];
                if (l == 0)
                {
                    continue;
                }

                var v = signal[row + x];
                var w = MathF.Max(v, 0f);
                area[l]++;
                flux[l] += v;
                sx[l] += w * x;
                sy[l] += w * y;
                sxx[l] += w * (double)x * x;
                syy[l] += w * (double)y * y;
                sxy[l] += w * (double)x * y;
                if (v > peak[l])
                {
                    peak[l] = v;
                    peakX[l] = x;
                    peakY[l] = y;
                }

                if (x < x0[l]) x0[l] = x;
                if (x > x1[l]) x1[l] = x;
                if (y < y0[l]) y0[l] = y;
                if (y > y1[l]) y1[l] = y;
            }
        }

        // Positive-weight sums for the moments: the same loop's w, re-accumulated as a total.
        var wsum = new double[count + 1];
        for (var i = 0; i < labels.Length; i++)
        {
            var l = labels[i];
            if (l != 0)
            {
                wsum[l] += MathF.Max(signal[i], 0f);
            }
        }

        var builder = ImmutableArray.CreateBuilder<Segment>(count);
        for (var l = 1; l <= count; l++)
        {
            var w = wsum[l];
            float cx, cy, elongation;
            if (w > 0)
            {
                cx = (float)(sx[l] / w);
                cy = (float)(sy[l] / w);
                var vxx = sxx[l] / w - (double)cx * cx;
                var vyy = syy[l] / w - (double)cy * cy;
                var vxy = sxy[l] / w - (double)cx * cy;
                var half = 0.5 * (vxx + vyy);
                var diff = Math.Sqrt(Math.Max(0.0, 0.25 * (vxx - vyy) * (vxx - vyy) + vxy * vxy));
                var major = Math.Max(half + diff, 1e-9);
                var minor = Math.Max(half - diff, 1e-9);
                elongation = (float)Math.Sqrt(major / minor);
            }
            else
            {
                cx = peakX[l];
                cy = peakY[l];
                elongation = 1f;
            }

            // Core fraction: the sky-subtracted flux in the 5 by 5 window on the peak, within the segment.
            var core = 0.0;
            for (var dy = -2; dy <= 2; dy++)
            {
                var yy = peakY[l] + dy;
                if (yy < 0 || yy >= height)
                {
                    continue;
                }

                for (var dx = -2; dx <= 2; dx++)
                {
                    var xx = peakX[l] + dx;
                    if (xx < 0 || xx >= width)
                    {
                        continue;
                    }

                    var i = yy * width + xx;
                    if (labels[i] == l)
                    {
                        core += MathF.Max(signal[i], 0f);
                    }
                }
            }

            var coreFraction = w > 0 ? (float)(core / w) : 1f;
            var peakToMean = w > 0 ? (float)(peak[l] / (w / area[l])) : 1f;
            var compact = (coreFraction >= options.CompactCoreFraction && area[l] <= options.CompactMaxArea)
                || peakToMean >= options.CompactPeakToMean;
            builder.Add(new Segment(l, area[l], cx, cy, peakX[l], peakY[l], peak[l], (float)flux[l],
                x0[l], y0[l], x1[l], y1[l], elongation, coreFraction, peakToMean, compact));
        }

        return builder.MoveToImmutable();
    }
}
