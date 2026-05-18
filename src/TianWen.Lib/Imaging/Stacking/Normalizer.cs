using System;
using System.Buffers;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Per-frame stats needed to normalize an image to a common median: the
/// minimum and median pixel value per channel. SetiAstro's <c>normalize_images</c>
/// uses a single luma-weighted median + min; ours splits per-channel because
/// downstream consumers (the integrator) operate per-channel anyway and a
/// per-channel normalization preserves colour balance more faithfully than
/// scaling all channels by a single luma-derived scalar.
/// </summary>
/// <param name="PerChannelMin">Minimum pixel value per channel, ignoring NaN.</param>
/// <param name="PerChannelMedian">Median pixel value per channel (50th percentile
/// via the existing <see cref="Image.Statistics"/> histogram path).</param>
public sealed record NormalizationStats(float[] PerChannelMin, float[] PerChannelMedian);

/// <summary>
/// Per-frame intensity normalization. Transforms each input pixel as
/// <c>out = (in - min) * (target / median)</c> so that, after normalization,
/// the frame's background sits at zero and its median lands at
/// <paramref name="targetMedian"/> (typically 0.25 for [0, 1] float data, or
/// the reference frame's median). This makes frames at different transparency
/// / exposure photometrically comparable for stack rejection + combine.
/// <para>
/// Per-channel: each channel uses its own min + median. For mono / raw-Bayer
/// (1 channel), this matches a luma-based normalizer exactly. For true RGB,
/// it preserves channel balance better than a single luma-weighted scalar
/// would.
/// </para>
/// </summary>
public static class Normalizer
{
    /// <summary>
    /// Computes <see cref="NormalizationStats"/> for an image — per-channel
    /// min + median. Median via quickselect (<see cref="StatisticsHelper.MedianFast(System.Span{float})"/>)
    /// on an ArrayPool-rented copy: O(n) instead of O(n log n) and zero
    /// long-lived allocations. Channels run in parallel. For a 3008^2 channel
    /// this is ~150 ms per channel (was ~1.5-2 s with sort-based path on the
    /// same hardware) -- benchmarked on the stacking-pipeline hot path where
    /// the call runs once per warped frame.
    /// </summary>
    public static NormalizationStats ComputeStats(Image image)
    {
        var c = image.ChannelCount;
        var mins = new float[c];
        var medians = new float[c];
        // Parallel across channels: 3 in the typical RGB case, so this is
        // mostly a wash on bigger machines, but free with Parallel.For and
        // matters on the 2- and 1-channel paths via cache-locality.
        Parallel.For(0, c, ch =>
        {
            var channel = image.GetChannelArray(ch);
            var span = MemoryMarshal.CreateReadOnlySpan(ref channel[0, 0], channel.Length);
            mins[ch] = MinIgnoringNaN(span);
            medians[ch] = MedianViaQuickSelect(span, mins[ch]);
        });
        return new NormalizationStats(mins, medians);
    }

    /// <summary>
    /// Box-restricted overload of <see cref="ComputeStats(Image)"/>. Walks
    /// min/median only over pixels inside <paramref name="box"/>, ignoring
    /// NaN. Used by the stacking pipeline to compute per-frame stats over the
    /// geometric intersection of all warped frames' footprints on the canvas
    /// (the rotated-quad-intersection AABB), so frames with large NaN edge
    /// regions don't collapse their (median - min) and explode the per-frame
    /// normalization scale.
    /// <para>
    /// Falls back to whole-image stats if <paramref name="box"/> is empty
    /// (intersection was disjoint) or clamps to image bounds.
    /// </para>
    /// </summary>
    public static NormalizationStats ComputeStats(Image image, Rectangle box)
    {
        var x0 = Math.Max(0, box.X);
        var y0 = Math.Max(0, box.Y);
        var x1 = Math.Min(image.Width,  box.Right);
        var y1 = Math.Min(image.Height, box.Bottom);
        if (x1 <= x0 || y1 <= y0) return ComputeStats(image);

        var c = image.ChannelCount;
        var mins = new float[c];
        var medians = new float[c];
        var count = (x1 - x0) * (y1 - y0);
        Parallel.For(0, c, ch =>
        {
            var channel = image.GetChannelArray(ch);
            // Copy the box's pixels into a contiguous scratch buffer and run
            // the same MinIgnoringNaN + MedianViaQuickSelect path as the
            // whole-image overload. Rented from the pool to avoid 3-channel
            // x N-frame GC churn on the stacking hot path.
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
                mins[ch] = MinIgnoringNaN(span);
                medians[ch] = MedianViaQuickSelect(span, mins[ch]);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buf);
            }
        });
        return new NormalizationStats(mins, medians);
    }

    private static float MedianViaQuickSelect(ReadOnlySpan<float> span, float fallbackOnEmpty)
    {
        if (span.Length == 0) return fallbackOnEmpty;

        // Rent rather than allocate -- a 3008^2 channel is 36 MB, 244 frames x
        // 3 channels = ~26 GB of churn that the GC would otherwise have to
        // collect. The pool returns oversize buffers, so we slice to the
        // valid count.
        var buffer = ArrayPool<float>.Shared.Rent(span.Length);
        try
        {
            // Strip NaN -- MedianFast's quickselect uses < / > comparisons that
            // are ill-defined on NaN (false-against-everything would land NaN
            // in unpredictable partition positions). Single pass copy + filter.
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
            return MedianFast(buffer.AsSpan(0, validCount));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Whole-frame normalize. Returns a new <see cref="Image"/> with
    /// per-channel <c>(pixel - min) * (target / median)</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Stats array lengths don't match
    /// the image's channel count.</exception>
    public static Image Apply(Image image, NormalizationStats stats, float targetMedian)
    {
        var c = image.ChannelCount;
        if (stats.PerChannelMin.Length != c || stats.PerChannelMedian.Length != c)
        {
            throw new ArgumentException(
                $"Stats arrays must have length ChannelCount ({c}); got Min={stats.PerChannelMin.Length}, Median={stats.PerChannelMedian.Length}.",
                nameof(stats));
        }

        var dst = Image.CreateChannelData(c, image.Height, image.Width);
        for (var ch = 0; ch < c; ch++)
        {
            var scale = ComputeScale(stats.PerChannelMedian[ch], stats.PerChannelMin[ch], targetMedian);
            var srcChannel = image.GetChannelArray(ch);
            var srcSpan = MemoryMarshal.CreateReadOnlySpan(ref srcChannel[0, 0], srcChannel.Length);
            var dstSpan = MemoryMarshal.CreateSpan(ref dst[ch][0, 0], dst[ch].Length);
            NormalizeVec(srcSpan, stats.PerChannelMin[ch], scale, dstSpan);
        }
        return new Image(dst, BitDepth.Float32, image.MaxValue, 0f, image.Pedestal, image.ImageMeta);
    }

    /// <summary>
    /// Tile-mode normalization: applies the per-channel transform to a
    /// row-major tile slice. Used by the Phase 8 tile-pipelined integrator
    /// so no full normalized image ever materialises.
    /// </summary>
    public static void ApplyTile(
        ReadOnlySpan<float> src,
        int channel,
        NormalizationStats stats,
        float targetMedian,
        Span<float> dst)
    {
        if (src.Length != dst.Length)
        {
            throw new ArgumentException($"src/dst length mismatch: {src.Length} vs {dst.Length}.", nameof(dst));
        }
        if ((uint)channel >= (uint)stats.PerChannelMin.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), $"Channel {channel} out of range (stats has {stats.PerChannelMin.Length}).");
        }

        var min = stats.PerChannelMin[channel];
        var scale = ComputeScale(stats.PerChannelMedian[channel], min, targetMedian);
        NormalizeVec(src, min, scale, dst);
    }

    private static float ComputeScale(float median, float min, float targetMedian)
    {
        // Scale derived from out = (in - min) * scale, with median mapping to
        // targetMedian: targetMedian = (median - min) * scale -> scale = targetMedian / (median - min).
        var denom = median - min;
        return denom > 0f ? targetMedian / denom : 1f;
    }

    private static void NormalizeVec(ReadOnlySpan<float> src, float min, float scale, Span<float> dst)
    {
        var width = Vector<float>.Count;
        var minVec = new Vector<float>(min);
        var scaleVec = new Vector<float>(scale);
        var i = 0;
        for (; i <= src.Length - width; i += width)
        {
            var v = new Vector<float>(src[i..]);
            ((v - minVec) * scaleVec).CopyTo(dst[i..]);
        }
        for (; i < src.Length; i++)
        {
            dst[i] = (src[i] - min) * scale;
        }
    }

    private static float MinIgnoringNaN(ReadOnlySpan<float> span)
    {
        // Vector<float>.Min returns NaN-poisoned results if any lane is NaN.
        // Scalar loop with explicit NaN skip is simpler than mask logic and
        // not the perf bottleneck (this runs once per frame at stats time).
        var min = float.PositiveInfinity;
        for (var i = 0; i < span.Length; i++)
        {
            var v = span[i];
            if (!float.IsNaN(v) && v < min) min = v;
        }
        return float.IsPositiveInfinity(min) ? 0f : min;
    }
}
