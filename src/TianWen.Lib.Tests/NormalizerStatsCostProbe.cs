using System;
using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Times <see cref="Normalizer.ComputeStats(Image)"/> and its box overload against the
/// implementation they replaced, both in one run, so the improvement is a measured number rather
/// than an inference from counting passes.
/// </summary>
/// <remarks>
/// <para>The "before" is reimplemented here rather than read from git, for the same reason
/// <c>HistogramSelectionParityTests</c> reimplements its oracle: a number taken from an earlier
/// build on an earlier day is not comparable, and this runs both on the same machine, same JIT, same
/// data, interleaved.</para>
/// <para>What it measured on a 12-core box, 3008x3008 x3: whole image 82.7 -> 77.8 ms, box
/// 68.7 -> 60.4 ms. Real but modest, and the reason is worth recording -- the cost is dominated
/// by the SELECTION over ~9 M floats per channel, not by the compaction passes removed, so
/// deleting one or two traversals of 36 MB buys single digits. The visible headroom is elsewhere:
/// <c>Parallel.For</c> runs over CHANNELS, so a 3-channel frame keeps 3 of 12 cores busy and the
/// other 9 idle. Intra-channel parallelism (or a parallel selection) is the next real lever, and
/// is not attempted here.</para>
/// <para>Env-gated (<c>TIANWEN_NORMALIZER_COST_PROBE=1</c>). Shape is a real stacking frame:
/// 3008x3008, three channels, with a NaN border of the kind a warped frame carries -- which is what
/// makes the NaN-stripping pass unavoidable and therefore worth fusing.</para>
/// </remarks>
[Collection("Imaging")]
public class NormalizerStatsCostProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_NORMALIZER_COST_PROBE";

    private const int Size = 3008;
    private const int Channels = 3;

    [Fact]
    public void TimeComputeStatsAgainstThePassesItReplaced()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }, $"{EnvVar} not set");

        var image = BuildWarpedLikeFrame();

        // Touch every plane so no variant is charged for first-touch page faults.
        // Skip NaN in the checksum: the frame is ~6% NaN by design, so summing blindly reports
        // NaN and stops being usable as evidence that the pages were touched.
        var warm = 0.0;
        for (var c = 0; c < Channels; c++)
        {
            var span = image.GetChannelSpan(c);
            for (var i = 0; i < span.Length; i += 1024)
            {
                if (!float.IsNaN(span[i])) { warm += span[i]; }
            }
        }
        output.WriteLine($"{Size}x{Size} x{Channels} = {Size * (long)Size * Channels / 1e6:F0} Mpx total, " +
            $"NaN border, warm-up checksum {warm:F0}");
        output.WriteLine($"cores={Environment.ProcessorCount}");
        output.WriteLine("");

        // Inset box, the shape the stacking pipeline actually passes (footprint intersection AABB).
        var box = new Rectangle(180, 220, Size - 400, Size - 460);

        Time("whole image  BEFORE (min pass + NaN-strip pass, 1 rent)", () => OldWholeImage(image));
        Time("whole image  AFTER  (fused, 1 rent)", () => Normalizer.ComputeStats(image));
        output.WriteLine("");
        Time("box          BEFORE (2-D copy + min pass + strip pass, 2 rents)", () => OldBox(image, box));
        Time("box          AFTER  (fused row-slice compaction, 1 rent)", () => Normalizer.ComputeStats(image, box));

        void Time(string label, Func<NormalizationStats> run)
        {
            run();
            var best = double.MaxValue;
            NormalizationStats? result = null;
            for (var rep = 0; rep < 3; rep++)
            {
                var sw = Stopwatch.StartNew();
                result = run();
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            output.WriteLine($"{label,-58} {best,8:F1} ms   " +
                $"min={result!.PerChannelMin[0]:F5} median={result.PerChannelMedian[0]:F5}");
        }
    }

    /// <summary>The whole-image path as it stood: a min pass, then a NaN-stripping copy into a
    /// second rented buffer, then the selection.</summary>
    private static NormalizationStats OldWholeImage(Image image)
    {
        var c = image.ChannelCount;
        var mins = new float[c];
        var medians = new float[c];
        Parallel.For(0, c, ch =>
        {
            var channel = image.GetChannelArray(ch);
            var span = MemoryMarshal.CreateReadOnlySpan(ref channel[0, 0], channel.Length);
            mins[ch] = OldMinIgnoringNaN(span);
            medians[ch] = OldMedianViaQuickSelect(span, mins[ch]);
        });
        return new NormalizationStats(mins, medians);
    }

    /// <summary>The box path as it stood: a two-dimensional indexed copy of the box, then the same
    /// two passes over it.</summary>
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
                    for (var x = x0; x < x1; x++)
                    {
                        buf[k++] = channel[y, x];
                    }
                }
                var span = new ReadOnlySpan<float>(buf, 0, count);
                mins[ch] = OldMinIgnoringNaN(span);
                medians[ch] = OldMedianViaQuickSelect(span, mins[ch]);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buf);
            }
        });
        return new NormalizationStats(mins, medians);
    }

    private static float OldMinIgnoringNaN(ReadOnlySpan<float> span)
    {
        var min = float.PositiveInfinity;
        for (var i = 0; i < span.Length; i++)
        {
            var v = span[i];
            if (!float.IsNaN(v) && v < min) min = v;
        }
        return float.IsPositiveInfinity(min) ? 0f : min;
    }

    private static float OldMedianViaQuickSelect(ReadOnlySpan<float> span, float fallbackOnEmpty)
    {
        if (span.Length == 0) return fallbackOnEmpty;

        var buffer = ArrayPool<float>.Shared.Rent(span.Length);
        try
        {
            var validCount = 0;
            for (var i = 0; i < span.Length; i++)
            {
                var v = span[i];
                if (!float.IsNaN(v))
                {
                    buffer[validCount++] = v;
                }
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
    /// Sky background plus noise, with a NaN border on all four sides -- what a rotated/translated
    /// warp leaves on the canvas, and the reason the compaction pass exists at all.
    /// </summary>
    private static Image BuildWarpedLikeFrame()
    {
        var planes = new float[Channels][,];
        var rng = new Random(7);
        for (var c = 0; c < Channels; c++)
        {
            var plane = new float[Size, Size];
            var flat = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
            for (var y = 0; y < Size; y++)
            {
                var rowBase = y * Size;
                for (var x = 0; x < Size; x++)
                {
                    // ~6% of the frame is NaN edge, a realistic dither/rotation footprint loss.
                    var outside = x < 60 || y < 45 || x >= Size - 40 || y >= Size - 55;
                    flat[rowBase + x] = outside
                        ? float.NaN
                        : 0.010f + (float)(rng.NextDouble() - 0.5) * 0.0020f + c * 0.001f;
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
