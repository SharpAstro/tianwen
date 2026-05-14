using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging.Calibration;

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
    /// min + median. Median via in-place sort of a per-channel copy: O(n log n)
    /// + ~4 bytes per pixel of extra allocation. The histogram-based median in
    /// <see cref="Image.Statistics"/> is faster but only populates the Median
    /// field when there are enough samples + a non-trivial dynamic range,
    /// which makes it unreliable for small test images and edge cases. For
    /// a 3008^2 channel the sort-based path takes ~1-2 s; revisit with a
    /// histogram-based estimator if profiling shows it dominates.
    /// </summary>
    public static NormalizationStats ComputeStats(Image image)
    {
        var c = image.ChannelCount;
        var mins = new float[c];
        var medians = new float[c];
        for (var ch = 0; ch < c; ch++)
        {
            var channel = image.GetChannelArray(ch);
            var span = MemoryMarshal.CreateReadOnlySpan(ref channel[0, 0], channel.Length);
            mins[ch] = MinIgnoringNaN(span);
            medians[ch] = MedianViaSort(span, mins[ch]);
        }
        return new NormalizationStats(mins, medians);
    }

    private static float MedianViaSort(ReadOnlySpan<float> span, float fallbackOnEmpty)
    {
        // Copy + sort. NaN-stripping happens implicitly because Array.Sort puts
        // NaNs at the end with .NET's comparer; we count valid entries and
        // take the middle of the valid range.
        if (span.Length == 0) return fallbackOnEmpty;
        var buffer = new float[span.Length];
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
        Array.Sort(buffer, 0, validCount);
        return validCount % 2 == 1
            ? buffer[validCount / 2]
            : 0.5f * (buffer[validCount / 2 - 1] + buffer[validCount / 2]);
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
