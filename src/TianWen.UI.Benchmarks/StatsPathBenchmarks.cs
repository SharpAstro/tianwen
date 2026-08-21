using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using TianWen.Lib;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Stat;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Before/after for the three stats paths changed on this branch, each against the implementation it
/// replaced as a baseline.
/// </summary>
/// <remarks>
/// <para>These started life as hand-rolled probes in the test project timing "best of 3" and reading
/// <c>GC.GetTotalAllocatedBytes</c>. That was the wrong tool: BenchmarkDotNet already gives iteration
/// statistics with outlier detection, and <see cref="MemoryDiagnoserAttribute"/> gives allocated
/// bytes per op AND Gen0/1/2 collections per 1000 ops -- the collection-pressure figure the probes
/// explicitly could not produce, because five reps never triggered a GC. Keeping the old
/// implementations in ONE place here also stops them being copied into two probes, which is how the
/// "2 rents" mislabel got written twice.</para>
/// <para>Run: <c>dotnet run -c Release --project TianWen.UI.Benchmarks -- --filter '*StatsPath*'</c></para>
/// <para>Sizes: 1280 is a guide-camera frame, 3008 an ASI2600 sub -- the size the stacking pipeline
/// actually sees, and the size at which the buffers cross into the LOH.</para>
/// </remarks>
[MemoryDiagnoser]
[ShortRunJob]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class StatsPathBenchmarks
{
    private const string StarMasked = "star-masked median+MAD";
    private const string NormWhole = "Normalizer whole image";
    private const string NormBox = "Normalizer box";
    private const string HistBins = "histogram bin buffer";

    private Image _starField = null!;
    private Image _warped = null!;
    // BitMatrix is a struct, so no null-forgiving initialiser here.
    private BitMatrix _mask;
    private Rectangle _box;

    [Params(1280, 3008)]
    public int Size { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _starField = BuildFrame(Size, channels: 3, nanBorder: false);
        _warped = BuildFrame(Size, channels: 3, nanBorder: true);

        var stars = await _starField.FindStarsAsync(_starField.ReferenceStarChannel, snrMin: 10f);
        _mask = stars.StarMask ?? new BitMatrix(Size, Size);

        var inset = Math.Max(8, Size / 16);
        _box = new Rectangle(inset, inset + 40, Size - 2 * inset, Size - 2 * inset - 60);
    }

    // ------------------------------------------------------------------ star-masked median + MAD

    [BenchmarkCategory(StarMasked)]
    [Benchmark(Baseline = true, Description = "two Array.Sort over two buffers")]
    public float StarMaskedBefore()
    {
        var sum = 0f;
        for (var c = 0; c < 3; c++) { sum += OldStarMaskedMedianAndMad(_starField, c, _mask).Mad; }
        return sum;
    }

    [BenchmarkCategory(StarMasked)]
    [Benchmark(Description = "two selections over one buffer")]
    public float StarMaskedAfter()
    {
        var sum = 0f;
        for (var c = 0; c < 3; c++) { sum += _starField.GetStarMaskedMedianAndMADScaledToUnit(c, _mask).MAD; }
        return sum;
    }

    // ------------------------------------------------------------------------- Normalizer, whole

    [BenchmarkCategory(NormWhole)]
    [Benchmark(Baseline = true, Description = "min pass + NaN-strip pass")]
    public NormalizationStats NormalizerWholeBefore() => OldWholeImage(_warped);

    [BenchmarkCategory(NormWhole)]
    [Benchmark(Description = "fused single pass")]
    public NormalizationStats NormalizerWholeAfter() => Normalizer.ComputeStats(_warped);

    // --------------------------------------------------------------------------- Normalizer, box

    [BenchmarkCategory(NormBox)]
    [Benchmark(Baseline = true, Description = "2-D copy + min + strip, 2 rents")]
    public NormalizationStats NormalizerBoxBefore() => OldBox(_warped, _box);

    [BenchmarkCategory(NormBox)]
    [Benchmark(Description = "fused row-slice compaction, 1 rent")]
    public NormalizationStats NormalizerBoxAfter() => Normalizer.ComputeStats(_warped, _box);

    // ---------------------------------------------------------------- histogram bin buffer shape
    // Isolated from Image.Histogram so the comparison is the buffer construction and nothing else:
    // 65536 bins built and handed out as an ImmutableArray, which is what every Statistics() call
    // does 10-12 times per document open.

    [BenchmarkCategory(HistBins)]
    [Benchmark(Baseline = true, Description = "Builder + AddRange zeros + ToImmutableArray")]
    public ImmutableArray<uint> HistogramBinsBefore()
    {
        const uint threshold = 65536;
        var histogram = ImmutableArray.CreateBuilder<uint>((int)threshold);

        const int size = 1024;
        Span<uint> zeros = stackalloc uint[size];
        zeros.Clear();
        for (var i = 0; i < threshold; i += size)
        {
            if (i + size > threshold) { histogram.AddRange(zeros[..(int)(threshold - i)]); }
            else { histogram.AddRange(zeros); }
        }

        histogram[1234]++;
        return histogram.ToImmutableArray();
    }

    [BenchmarkCategory(HistBins)]
    [Benchmark(Description = "uint[] wrapped zero-copy")]
    public ImmutableArray<uint> HistogramBinsAfter()
    {
        const uint threshold = 65536;
        var histogram = new uint[threshold];
        histogram[1234]++;
        return ImmutableCollectionsMarshal.AsImmutableArray(histogram);
    }

    // ------------------------------------------------------------------- the replaced code, once

    /// <summary>The star-masked path as it stood: two buffers, two full sorts.</summary>
    private static (float Median, float Mad) OldStarMaskedMedianAndMad(
        Image image, int channel, BitMatrix starMask, int pixelStride = 4)
    {
        var (_, width, height) = image.Shape;
        var unitDivisor = image.MaxValue <= 1f ? 1f : image.MaxValue;
        var maxSamples = ((width / pixelStride) + 1) * ((height / pixelStride) + 1);
        var samples = new float[maxSamples];
        var count = 0;

        for (var y = 0; y < height; y += pixelStride)
        {
            for (var x = 0; x < width; x += pixelStride)
            {
                var v = image[channel, y, x];
                if (float.IsNaN(v)) continue;
                if (starMask[y, x]) continue;
                if (v <= image.MinValue) continue;
                samples[count++] = v;
            }
        }

        if (count < 100) { return (0f, 0f); }

        Array.Sort(samples, 0, count);
        var median = samples[count / 2];

        var madSamples = new float[count];
        for (var i = 0; i < count; i++) { madSamples[i] = MathF.Abs(samples[i] - median); }
        Array.Sort(madSamples);
        var mad = madSamples[count / 2];

        var invMax = 1f / unitDivisor;
        return ((median - image.MinValue) * invMax, mad * invMax);
    }

    /// <summary>The whole-image path as it stood: a min pass, then a NaN-stripping copy.</summary>
    private static NormalizationStats OldWholeImage(Image image)
    {
        var c = image.ChannelCount;
        var mins = new float[c];
        var medians = new float[c];
        Parallel.For(0, c, ch =>
        {
            var channel = image.GetChannelArray(ch);
            var span = MemoryMarshal.CreateReadOnlySpan(ref channel[0, 0], channel.Length);
            mins[ch] = OldMin(span);
            medians[ch] = OldMedian(span, mins[ch]);
        });
        return new NormalizationStats(mins, medians);
    }

    /// <summary>The box path as it stood: a two-dimensional copy, then the same two passes over it.</summary>
    private static NormalizationStats OldBox(Image image, Rectangle box)
    {
        var x0 = Math.Max(0, box.X);
        var y0 = Math.Max(0, box.Y);
        var x1 = Math.Min(image.Width, box.Right);
        var y1 = Math.Min(image.Height, box.Bottom);
        var c = image.ChannelCount;
        var mins = new float[c];
        var medians = new float[c];
        var count = (x1 - x0) * (y1 - y0);
        Parallel.For(0, c, ch =>
        {
            var channel = image.GetChannelArray(ch);
            var buf = ArrayPool<float>.Shared.Rent(count);
            try
            {
                var k = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++) { buf[k++] = channel[y, x]; }
                }
                var span = new ReadOnlySpan<float>(buf, 0, count);
                mins[ch] = OldMin(span);
                medians[ch] = OldMedian(span, mins[ch]);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buf);
            }
        });
        return new NormalizationStats(mins, medians);
    }

    private static float OldMin(ReadOnlySpan<float> span)
    {
        var min = float.PositiveInfinity;
        for (var i = 0; i < span.Length; i++)
        {
            var v = span[i];
            if (!float.IsNaN(v) && v < min) min = v;
        }
        return float.IsPositiveInfinity(min) ? 0f : min;
    }

    private static float OldMedian(ReadOnlySpan<float> span, float fallbackOnEmpty)
    {
        if (span.Length == 0) return fallbackOnEmpty;
        var buffer = ArrayPool<float>.Shared.Rent(span.Length);
        try
        {
            var validCount = 0;
            for (var i = 0; i < span.Length; i++)
            {
                var v = span[i];
                if (!float.IsNaN(v)) { buffer[validCount++] = v; }
            }
            if (validCount == 0) return fallbackOnEmpty;
            return StatisticsHelper.MedianFast(buffer.AsSpan(0, validCount));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Sky background plus noise. <paramref name="nanBorder"/> models the NaN edge a rotated or
    /// translated warp leaves on the canvas, which is what makes the compaction pass unavoidable;
    /// without it, planted stars give the star mask something to find.
    /// </summary>
    private static Image BuildFrame(int size, int channels, bool nanBorder)
    {
        var planes = new float[channels][,];
        var rng = new Random(11);
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[size, size];
            var flat = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
            var border = Math.Max(4, size / 50);
            for (var y = 0; y < size; y++)
            {
                var rowBase = y * size;
                for (var x = 0; x < size; x++)
                {
                    var outside = nanBorder
                        && (x < border || y < border || x >= size - border || y >= size - border);
                    flat[rowBase + x] = outside
                        ? float.NaN
                        : 0.010f + (float)(rng.NextDouble() - 0.5) * 0.0020f;
                }
            }
            if (!nanBorder)
            {
                var starCount = size * size / 3600;
                for (var s = 0; s < starCount; s++)
                {
                    var cx = rng.Next(20, size - 20);
                    var cy = rng.Next(20, size - 20);
                    var peak = 0.05f + (float)rng.NextDouble() * 0.8f;
                    for (var dy = -5; dy <= 5; dy++)
                    {
                        for (var dx = -5; dx <= 5; dx++)
                        {
                            var g = peak * MathF.Exp(-(dx * dx + dy * dy) / 4.5f);
                            var idx = (cy + dy) * size + (cx + dx);
                            flat[idx] = MathF.Min(1f, flat[idx] + g);
                        }
                    }
                }
            }
            planes[c] = plane;
        }

        var meta = new ImageMeta("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0,
            RowOrder.TopDown, float.NaN, float.NaN);
        return new Image(planes, BitDepth.Float32, 1f, 0f, 0f, meta);
    }
}
