using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>Image.Histogram</c> costs ~14 ns/px and is the dominant cost of a viewer document open, which
/// makes 10-12 full traversals (see <c>DocumentOpenCostProbe</c> and <c>docs/todo/imaging.md</c>).
/// ~42 cycles for a bin-and-increment loop is far too many to accept on inspection, so this probe
/// ATTRIBUTES them: each variant changes exactly one thing against the one before it, so the deltas
/// name the cause instead of a code reading guessing at it.
/// </summary>
/// <remarks>
/// Env-gated (<c>TIANWEN_HISTOGRAM_PROBE=1</c>) because it allocates ~100 MB and runs for seconds.
/// The plane is touched before timing: a freshly allocated 96 MB array otherwise charges its
/// first-touch page faults to whichever variant runs first, which is exactly how the first run of
/// <c>DocumentOpenCostProbe</c> came to report 2.2 s for <c>Statistics</c>.
/// </remarks>
[Collection("Imaging")]
public class HistogramCostDecompositionProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_HISTOGRAM_PROBE";

    private const int Width = 6000;
    private const int Height = 4000;

    [Fact]
    public void WhereTheHistogramTimeGoes()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }, $"{EnvVar} not set");

        var (image, plane) = BuildAstroLikeImage();

        var warm = 0.0;
        var probe = image.GetChannelSpan(0);
        for (var i = 0; i < probe.Length; i += 1024) { warm += probe[i]; }
        var pixels = (long)Width * Height;
        output.WriteLine($"{Width}x{Height} = {pixels / 1e6:F1} Mpx, maxValue={image.MaxValue:F4} " +
            $"minValue={image.MinValue:F4}, warm-up checksum {warm:F0}");
        output.WriteLine($"cores={Environment.ProcessorCount}");
        output.WriteLine("");

        // The unit-scaled-float branch of Histogram: bins map [0,1] onto [0,65535].
        const float ScaleFactor = ushort.MaxValue;
        const uint Threshold = 65536u;
        var pedestal = image.MinValue * ScaleFactor;

        Time("V0 production Statistics(0, removePedestral: true)", () =>
        {
            var h = image.Statistics(0, removePedestral: true);
            return (h.Mean, h.Total);
        });
        Time("V1 + flat span (was float[,] [h,w] indexing)", () => SpanBuilder(plane, ScaleFactor, pedestal, Threshold));
        Time("V2 + uint[] bins (was ImmutableArray Builder)", () => PlainArray(plane, ScaleFactor, pedestal, Threshold));
        Time("V3 + int clamp (was Math.Clamp double overload)", () => IntClamp(plane, ScaleFactor, pedestal, Threshold));
        Time("V4 + parallel row bands", () => ParallelBands(plane, ScaleFactor, pedestal, Threshold));

        void Time(string label, Func<(float Mean, double Total)> run)
        {
            run();  // JIT and branch-predictor warm-up, not measured.
            var best = double.MaxValue;
            (float Mean, double Total) result = default;
            for (var rep = 0; rep < 3; rep++)
            {
                var sw = Stopwatch.StartNew();
                result = run();
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            output.WriteLine($"{label,-52} {best,8:F1} ms  {best * 1e6 / pixels,5:F2} ns/px   " +
                $"mean={result.Mean:F6} total={result.Total}");
        }
    }

    /// <summary>Flat span; everything else identical to production, Builder included.</summary>
    private static (float Mean, double Total) SpanBuilder(float[,] plane, float scaleFactor, float pedestal, uint threshold)
    {
        var data = MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
        var histogram = ImmutableArray.CreateBuilder<uint>((int)threshold);
        for (var i = 0; i < threshold; i++) { histogram.Add(0u); }

        var histTotal = 0u;
        var count = 1;
        var totalValue = 0.0;
        for (var i = 0; i < data.Length; i++)
        {
            var raw = data[i];
            if (!float.IsNaN(raw))
            {
                var v = raw * scaleFactor - pedestal;
                if (v < threshold)
                {
                    var bin = (int)Math.Clamp(MathF.Round(v), 0, threshold - 1);
                    histogram[bin]++;
                    histTotal++;
                    totalValue += v;
                    count++;
                }
            }
        }
        return ((float)(totalValue / count), histTotal);
    }

    /// <summary>As V1, but the bins are a plain array so the JIT can keep the base in a register.</summary>
    private static (float Mean, double Total) PlainArray(float[,] plane, float scaleFactor, float pedestal, uint threshold)
    {
        var data = MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
        var bins = new uint[threshold];
        var histTotal = 0u;
        var count = 1;
        var totalValue = 0.0;
        for (var i = 0; i < data.Length; i++)
        {
            var raw = data[i];
            if (!float.IsNaN(raw))
            {
                var v = raw * scaleFactor - pedestal;
                if (v < threshold)
                {
                    var bin = (int)Math.Clamp(MathF.Round(v), 0, threshold - 1);
                    bins[bin]++;
                    histTotal++;
                    totalValue += v;
                    count++;
                }
            }
        }
        return ((float)(totalValue / count), histTotal);
    }

    /// <summary>As V2, but the clamp stays in int instead of going float -> double -> int.</summary>
    private static (float Mean, double Total) IntClamp(float[,] plane, float scaleFactor, float pedestal, uint threshold)
    {
        var data = MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
        var bins = new uint[threshold];
        var histTotal = 0u;
        var count = 1;
        var totalValue = 0.0;
        var maxBin = (int)threshold - 1;
        for (var i = 0; i < data.Length; i++)
        {
            var raw = data[i];
            if (!float.IsNaN(raw))
            {
                var v = raw * scaleFactor - pedestal;
                if (v < threshold)
                {
                    var bin = (int)MathF.Round(v);
                    bin = bin < 0 ? 0 : (bin > maxBin ? maxBin : bin);
                    bins[bin]++;
                    histTotal++;
                    totalValue += v;
                    count++;
                }
            }
        }
        return ((float)(totalValue / count), histTotal);
    }

    /// <summary>
    /// As V3, but over fixed row bands with one bin array each, reduced in band order. Fixed bands
    /// (not work-stealing chunks) so the reduction order is deterministic and the double sum is
    /// reproducible run to run.
    /// </summary>
    private static (float Mean, double Total) ParallelBands(float[,] plane, float scaleFactor, float pedestal, uint threshold)
    {
        var bandCount = Math.Min(Environment.ProcessorCount, Height);
        var rowsPerBand = (Height + bandCount - 1) / bandCount;
        var partials = new (uint[] Bins, uint Total, double Sum, int Count)[bandCount];
        var maxBin = (int)threshold - 1;

        Parallel.For(0, bandCount, band =>
        {
            // The span is created inside the lambda: a Span cannot be captured, but the array can.
            var data = MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
            var y0 = band * rowsPerBand;
            var y1 = Math.Min(Height, y0 + rowsPerBand);
            var bins = new uint[threshold];
            var total = 0u;
            var sum = 0.0;
            var n = 0;
            for (var i = y0 * Width; i < y1 * Width; i++)
            {
                var raw = data[i];
                if (!float.IsNaN(raw))
                {
                    var v = raw * scaleFactor - pedestal;
                    if (v < threshold)
                    {
                        var bin = (int)MathF.Round(v);
                        bin = bin < 0 ? 0 : (bin > maxBin ? maxBin : bin);
                        bins[bin]++;
                        total++;
                        sum += v;
                        n++;
                    }
                }
            }
            partials[band] = (bins, total, sum, n);
        });

        var merged = new uint[threshold];
        var histTotal = 0u;
        var totalValue = 0.0;
        var count = 1;
        for (var band = 0; band < bandCount; band++)
        {
            var (bins, total, sum, n) = partials[band];
            for (var b = 0; b < merged.Length; b++) { merged[b] += bins[b]; }
            histTotal += total;
            totalValue += sum;
            count += n;
        }
        return ((float)(totalValue / count), histTotal);
    }

    /// <summary>
    /// Tight background with read noise plus planted Gaussian stars. The bin DISTRIBUTION is the
    /// point: a concentrated background is what turns the bin increment into a serial
    /// read-modify-write chain on one cache line, so a uniform ramp would understate it.
    /// </summary>
    private static (Image Image, float[,] Plane) BuildAstroLikeImage()
    {
        var plane = new float[Height, Width];
        var flat = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
        var rng = new Random(42);
        for (var i = 0; i < flat.Length; i++)
        {
            flat[i] = 0.012f + (float)(rng.NextDouble() - 0.5) * 0.0016f;
        }

        for (var s = 0; s < 3000; s++)
        {
            var cx = rng.Next(20, Width - 20);
            var cy = rng.Next(20, Height - 20);
            var peak = 0.05f + (float)rng.NextDouble() * 0.9f;
            for (var dy = -6; dy <= 6; dy++)
            {
                for (var dx = -6; dx <= 6; dx++)
                {
                    var g = peak * MathF.Exp(-(dx * dx + dy * dy) / 5.0f);
                    var idx = (cy + dy) * Width + (cx + dx);
                    flat[idx] = MathF.Min(1f, flat[idx] + g);
                }
            }
        }

        var max = 0f;
        var min = float.MaxValue;
        for (var i = 0; i < flat.Length; i++)
        {
            if (flat[i] > max) { max = flat[i]; }
            if (flat[i] < min) { min = flat[i]; }
        }

        var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.Zero, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
            RowOrder.TopDown, float.NaN, float.NaN);
        return (new Image([plane], BitDepth.Float32, max, min, 0f, meta), plane);
    }
}
