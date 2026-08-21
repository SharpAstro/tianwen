using System;
using System.Buffers;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Allocation cost of the stats paths, measured rather than inferred from reading the code.
/// </summary>
/// <remarks>
/// <para>Written because three commits on this branch claimed allocation improvements ("drops the
/// second 6 MB buffer", "two rents become one") that had only been READ off the source, never
/// measured. On a branch whose subject is memory that is the wrong way round.</para>
/// <para>The instrument is <see cref="GC.GetTotalAllocatedBytes(bool)"/>, which is process-wide and
/// so counts the <see cref="Parallel.For"/> worker threads these paths use.
/// <c>GC.GetAllocatedBytesForCurrentThread</c> would miss them, and working set cannot see an
/// allocation change at all -- it varied by more than 1200 MB run to run when it was tried on this
/// project, which is the reason that note exists.</para>
/// <para>GC collection counts are reported alongside the byte totals, because the bytes are the
/// cause and the collections are the effect anyone actually feels.</para>
/// <para>Env-gated: <c>TIANWEN_STATS_ALLOC_PROBE=1</c>.</para>
/// </remarks>
[Collection("Imaging")]
public class StatsAllocationProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_STATS_ALLOC_PROBE";

    [Fact]
    public async Task WhatTheseStatsPathsActuallyAllocate()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }, $"{EnvVar} not set");

        var ct = TestContext.Current.CancellationToken;

        // ---------------------------------------------------------------- ArrayPool's real cap
        // Both Normalizer paths rent buffers the size of a whole channel. Whether that is a pooled
        // reuse or a fresh allocation every call depends on ArrayPool<float>.Shared's maximum, which
        // is an implementation detail worth measuring rather than recalling.
        foreach (var elements in new[] { 1 << 18, 1 << 20, 1 << 21, 1 << 23, 9_048_064 })
        {
            var first = ArrayPool<float>.Shared.Rent(elements);
            ArrayPool<float>.Shared.Return(first);
            var second = ArrayPool<float>.Shared.Rent(elements);
            ArrayPool<float>.Shared.Return(second);
            output.WriteLine($"ArrayPool rent {elements,9} floats ({elements * 4L / (1024 * 1024),4} MB): " +
                $"{(ReferenceEquals(first, second) ? "POOLED (reused)" : "NOT pooled (fresh allocation)")}");
        }
        output.WriteLine("");

        // ------------------------------------------------- star-masked median/MAD, old vs new
        var doc = BuildFrame(3008, 3008, channels: 3, nanBorder: false);
        var stars = await doc.FindStarsAsync(doc.ReferenceStarChannel, snrMin: 10f, cancellationToken: ct);
        // Pattern-bound so the local is typed non-nullable: an `if (mask is null) else` does NOT
        // narrow inside the lambdas below, because flow analysis will not carry narrowing into a
        // closure over a variable that could be reassigned.
        if (stars.StarMask is not { } mask)
        {
            output.WriteLine("no star mask, skipping the star-masked comparison");
        }
        else
        {
            output.WriteLine($"--- star-masked median+MAD, 3008x3008 x3 ({stars.Count} stars in the mask)");
            Measure("BEFORE  two Array.Sort over two float[] buffers", 5, () =>
            {
                for (var c = 0; c < 3; c++) { OldStarMaskedMedianAndMad(doc, c, mask); }
            });
            Measure("AFTER   two selections over one buffer", 5, () =>
            {
                for (var c = 0; c < 3; c++) { doc.GetStarMaskedMedianAndMADScaledToUnit(c, mask); }
            });
            output.WriteLine("");
        }

        // ------------------------------------------------------- Normalizer, old vs new
        var warped = BuildFrame(3008, 3008, channels: 3, nanBorder: true);
        var box = new Rectangle(180, 220, 3008 - 400, 3008 - 460);

        output.WriteLine("--- Normalizer.ComputeStats, 3008x3008 x3 with a NaN border");
        Measure("BEFORE  whole image: min pass + strip pass, 1 rent", 5, () => OldWholeImage(warped));
        Measure("AFTER   whole image: fused, 1 rent", 5, () => Normalizer.ComputeStats(warped));
        Measure("BEFORE  box: 2-D copy + min + strip, 2 rents", 5, () => OldBox(warped, box));
        Measure("AFTER   box: fused row-slice compaction, 1 rent", 5, () => Normalizer.ComputeStats(warped, box));
        output.WriteLine("");

        // ------------------------------------------------------------------------- histogram
        // This probe is what found it: the bins were an ImmutableArray Builder, so every call paid
        // for the builder's backing array AND a second one because ToImmutableArray() on a Builder
        // copies -- 0.50 MB per call, 10-12 calls per document open. Now a plain uint[] wrapped
        // without copying: 0.25 MB.
        output.WriteLine("--- Image.Statistics, 3008x3008, one channel (was 0.50 MB/call before the zero-copy wrap)");
        Measure("Statistics(0)", 5, () => doc.Statistics(0));

        void Measure(string label, int reps, Action run)
        {
            // Warm up: JIT, and let the pool reach steady state so a first-call fill is not counted
            // as if it happened every call.
            run();
            run();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < reps; i++) { run(); }
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            output.WriteLine($"{label,-52} {allocated / (double)reps / (1024 * 1024),8:F2} MB/call   " +
                $"gc(g0/g1/g2) +{GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2}");
        }
    }

    /// <summary>The star-masked path as it stood: two separate buffers, two full sorts.</summary>
    private static (float Pedestral, float Median, float Mad) OldStarMaskedMedianAndMad(
        Image image, int channel, BitMatrix starMask, int pixelStride = 4)
    {
        var (_, width, height) = image.Shape;
        var unitDivisor = image.MaxValue <= 1f ? 1f : image.MaxValue;
        var pedestal = image.MinValue / unitDivisor;
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

        if (count < 100)
        {
            var fb = image.GetPedestralMedianAndMADScaledToUnit(channel);
            return (fb.Pedestral, fb.Median, fb.MAD);
        }

        Array.Sort(samples, 0, count);
        var median = samples[count / 2];

        var madSamples = new float[count];
        for (var i = 0; i < count; i++)
        {
            madSamples[i] = MathF.Abs(samples[i] - median);
        }
        Array.Sort(madSamples);
        var mad = madSamples[count / 2];

        var invMax = 1f / unitDivisor;
        return (pedestal, (median - image.MinValue) * invMax, mad * invMax);
    }

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

    private static Image BuildFrame(int width, int height, int channels, bool nanBorder)
    {
        var planes = new float[channels][,];
        var rng = new Random(11);
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[height, width];
            var flat = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
            for (var y = 0; y < height; y++)
            {
                var rowBase = y * width;
                for (var x = 0; x < width; x++)
                {
                    var outside = nanBorder && (x < 60 || y < 45 || x >= width - 40 || y >= height - 55);
                    flat[rowBase + x] = outside
                        ? float.NaN
                        : 0.010f + (float)(rng.NextDouble() - 0.5) * 0.0020f;
                }
            }
            if (!nanBorder)
            {
                // A few thousand stars so the star mask is realistic.
                for (var s = 0; s < 2500; s++)
                {
                    var cx = rng.Next(20, width - 20);
                    var cy = rng.Next(20, height - 20);
                    var peak = 0.05f + (float)rng.NextDouble() * 0.8f;
                    for (var dy = -5; dy <= 5; dy++)
                    {
                        for (var dx = -5; dx <= 5; dx++)
                        {
                            var g = peak * MathF.Exp(-(dx * dx + dy * dy) / 4.5f);
                            var idx = (cy + dy) * width + (cx + dx);
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
