using System;
using System.Buffers;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Per-frame stats needed to normalize an image to a common median: the additive floor and the
/// median pixel value per channel. SetiAstro's <c>normalize_images</c> uses a single luma-weighted
/// median + min; ours splits per-channel because downstream consumers (the integrator) operate
/// per-channel anyway and a per-channel normalization preserves colour balance more faithfully
/// than scaling all channels by a single luma-derived scalar.
/// </summary>
/// <param name="PerChannelFloor">
/// The additive anchor per channel: the value that maps to zero. This is the frame's
/// <see cref="Image.Pedestal"/> (the calibrated zero, which every frame of a group shares), NOT the
/// frame's minimum pixel. It used to be the minimum, and that made the gain of every frame and
/// channel a function of whatever its single most negative pixel happened to be: a hot pixel, a
/// cosmic ray, a flat that reaches zero in a corner (the calibrator divides by
/// <c>max(flat, epsilon)</c> and makes a spike of ~1e9), or a demosaic overshoot beside a saturated
/// star. Measured across one 89-frame session (SV605CC, 30 s): with the AHD debayer the red
/// channel's gain wandered by x3.7 from frame to frame, green by x2.3 and blue by x3.0, each
/// channel independently; with MHC a single flat-edge spike put the min at -1e9 and mapped every
/// pixel of every frame onto the target to within a few float ulps, so the star layer integrated
/// to a constant. Frames entering a stack with random per-channel gains is a photometric error the
/// rejector then acts on, and it is what a colour calibration downstream cannot undo.
/// </param>
/// <param name="PerChannelMedian">Median pixel value per channel (50th percentile).</param>
public sealed record NormalizationStats(float[] PerChannelFloor, float[] PerChannelMedian);

/// <summary>
/// Per-frame intensity normalization. Transforms each input pixel as
/// <c>out = (in - floor) * (target / (median - floor))</c> so that, after normalization, the
/// frame's zero sits at zero and its median lands at <paramref name="targetMedian"/>
/// (typically 0.5 for [0, 1] float data, or the reference frame's median). This makes frames at
/// different transparency / sky brightness comparable for stack rejection + combine. The floor is
/// the frame's pedestal, so the only per-frame quantity in the map is the sky median: the gain of a
/// frame follows its sky and nothing else (see <see cref="NormalizationStats"/> for what anchoring
/// on the minimum did instead).
/// <para>
/// Per-channel: each channel uses its own median. For mono / raw-Bayer (1 channel), this matches a
/// luma-based normalizer exactly. For true RGB, it preserves channel balance better than a single
/// luma-weighted scalar would.
/// </para>
/// </summary>
public static class Normalizer
{
    /// <summary>
    /// Computes <see cref="NormalizationStats"/> for an image: the pedestal as the per-channel
    /// floor, and the per-channel median. Median via quickselect
    /// (<see cref="StatisticsHelper.MedianFast(System.Span{float})"/>) on an ArrayPool-rented
    /// copy: O(n) instead of O(n log n) and zero long-lived allocations. Channels run in parallel.
    /// For a 3008^2 channel this is ~150 ms per channel (was ~1.5-2 s with sort-based path on the
    /// same hardware) -- benchmarked on the stacking-pipeline hot path where the call runs once per
    /// warped frame.
    /// </summary>
    public static NormalizationStats ComputeStats(Image image)
    {
        var c = image.ChannelCount;
        var floors = new float[c];
        var medians = new float[c];
        Array.Fill(floors, image.Pedestal);

        // Parallel across channels: 3 in the typical RGB case, so this is
        // mostly a wash on bigger machines, but free with Parallel.For and
        // matters on the 2- and 1-channel paths via cache-locality.
        Parallel.For(0, c, ch =>
        {
            var channel = image.GetChannelArray(ch);
            var span = MemoryMarshal.CreateReadOnlySpan(ref channel[0, 0], channel.Length);

            // Rent rather than allocate: a 3008^2 channel is 36 MB, and 244 frames x 3
            // channels is ~26 GB of churn the GC would otherwise have to collect. The pool
            // returns oversize buffers, so slice to the valid count.
            var buf = ArrayPool<float>.Shared.Rent(span.Length);
            try
            {
                var n = CompactFinite(span, buf, out _);
                medians[ch] = n == 0 ? floors[ch] : MedianFast(buf.AsSpan(0, n));
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buf);
            }
        });

        return new NormalizationStats(floors, medians);
    }

    /// <summary>
    /// Box-restricted overload of <see cref="ComputeStats(Image)"/>. Takes the median only over
    /// pixels inside <paramref name="box"/>, ignoring NaN. Used by the stacking pipeline to compute
    /// per-frame stats over the geometric intersection of all warped frames' footprints on the
    /// canvas (the rotated-quad-intersection AABB), so a frame's median is read where every frame
    /// has data rather than being pulled by its own NaN edge regions.
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
        var floors = new float[c];
        var medians = new float[c];
        Array.Fill(floors, image.Pedestal);
        var count = (x1 - x0) * (y1 - y0);
        var width = image.Width;

        Parallel.For(0, c, ch =>
        {
            var channel = image.GetChannelArray(ch);
            var flat = MemoryMarshal.CreateReadOnlySpan(ref channel[0, 0], channel.Length);

            // Rented from the pool to avoid 3-channel x N-frame GC churn on the stacking hot
            // path. A box row is contiguous, so the compaction runs row by row straight into the
            // scratch with flat indexing instead of channel[y, x].
            var buf = ArrayPool<float>.Shared.Rent(count);
            try
            {
                var n = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = flat.Slice(y * width + x0, x1 - x0);
                    n += CompactFinite(row, buf.AsSpan(n), out _);
                }
                // An all-NaN box has no median; the floor stands in, and the scale then falls back
                // to identity in ComputeScale.
                medians[ch] = n == 0 ? floors[ch] : MedianFast(buf.AsSpan(0, n));
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buf);
            }
        });

        return new NormalizationStats(floors, medians);
    }

    /// <summary>
    /// Whole-frame normalize. Returns a new <see cref="Image"/> with
    /// per-channel <c>(pixel - floor) * (target / (median - floor))</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Stats array lengths don't match
    /// the image's channel count.</exception>
    public static Image Apply(Image image, NormalizationStats stats, float targetMedian)
    {
        var c = image.ChannelCount;
        if (stats.PerChannelFloor.Length != c || stats.PerChannelMedian.Length != c)
        {
            throw new ArgumentException(
                $"Stats arrays must have length ChannelCount ({c}); got Floor={stats.PerChannelFloor.Length}, Median={stats.PerChannelMedian.Length}.",
                nameof(stats));
        }

        var dst = Image.CreateChannelData(c, image.Height, image.Width);
        for (var ch = 0; ch < c; ch++)
        {
            var scale = ComputeScale(stats.PerChannelMedian[ch], stats.PerChannelFloor[ch], targetMedian);
            var srcChannel = image.GetChannelArray(ch);
            var srcSpan = MemoryMarshal.CreateReadOnlySpan(ref srcChannel[0, 0], srcChannel.Length);
            var dstSpan = MemoryMarshal.CreateSpan(ref dst[ch][0, 0], dst[ch].Length);
            NormalizeVec(srcSpan, stats.PerChannelFloor[ch], scale, dstSpan);
        }

        // The pedestal has been mapped to zero, so the result carries none.
        return new Image(dst, BitDepth.Float32, image.MaxValue, 0f, 0f, image.ImageMeta);
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
        if ((uint)channel >= (uint)stats.PerChannelFloor.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), $"Channel {channel} out of range (stats has {stats.PerChannelFloor.Length}).");
        }

        var floor = stats.PerChannelFloor[channel];
        var scale = ComputeScale(stats.PerChannelMedian[channel], floor, targetMedian);
        NormalizeVec(src, floor, scale, dst);
    }

    /// <summary>
    /// The multiplicative term of the map, shared by every consumer that applies the stats itself
    /// (the in-RAM and streaming integrators keep per-frame scalars rather than a normalized copy).
    /// </summary>
    public static float ComputeScale(float median, float floor, float targetMedian)
    {
        // out = (in - floor) * scale, with the median mapping to targetMedian:
        // targetMedian = (median - floor) * scale -> scale = targetMedian / (median - floor).
        // A median at or below the floor has no sky to normalize on; identity, never a division.
        var denom = median - floor;
        return denom > 0f ? targetMedian / denom : 1f;
    }

    private static void NormalizeVec(ReadOnlySpan<float> src, float floor, float scale, Span<float> dst)
    {
        var width = Vector<float>.Count;
        var floorVec = new Vector<float>(floor);
        var scaleVec = new Vector<float>(scale);

        var i = 0;
        for (; i <= src.Length - width; i += width)
        {
            var v = new Vector<float>(src[i..]);
            ((v - floorVec) * scaleVec).CopyTo(dst[i..]);
        }
        for (; i < src.Length; i++)
        {
            dst[i] = (src[i] - floor) * scale;
        }
    }
}
