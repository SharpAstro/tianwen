using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Combines a group of calibration frames into a single master frame via
/// per-pixel median across the stack. Frames must share shape and bit depth
/// (the caller is expected to have pre-grouped them via <see cref="MasterGroupKey"/>).
/// <para>
/// Three entry points mirror the three calibration frame types:
/// </para>
/// <list type="bullet">
/// <item><see cref="BuildBiasMasterAsync"/>: plain median combine.</item>
/// <item><see cref="BuildDarkMasterAsync"/>: same as bias; no exposure-ratio
/// scaling (we group by exact exposure rather than scaling at master time).</item>
/// <item><see cref="BuildFlatMasterAsync"/>: per-frame normalization to mean=1
/// first (Bayer-aware for RGGB sensors), then median combine. Median rejects
/// transient star ghosts and sensor particles that would otherwise survive
/// a mean combine.</item>
/// </list>
/// <para>
/// Memory: all input frames are held in RAM simultaneously. For a typical
/// 30-frame × 3008² mono master that's ~1 GB; the per-pixel median scratch
/// uses a small <see cref="ArrayPool{T}"/> rental per worker thread, so the
/// asymptotic cost is dominated by the inputs.
/// </para>
/// </summary>
public static class MasterFrameBuilder
{
    /// <summary>Combines bias frames via per-pixel median. Bias has no
    /// exposure-time dependence and tolerates a plain median; cosmic-ray hits
    /// on bias frames are rare enough that sigma-clip rejection isn't worth
    /// the v1 complexity.</summary>
    public static async Task<Image> BuildBiasMasterAsync(
        IReadOnlyList<FrameInfo> frames, CancellationToken cancellationToken = default)
    {
        ValidateInput(frames);
        var images = await LoadAllAsync(frames, cancellationToken);
        return BuildBiasMaster(images);
    }

    /// <summary>Combines dark frames via per-pixel median. Frames must share
    /// exposure and temperature (caller groups via <see cref="MasterGroupKey"/>);
    /// no exposure-ratio scaling is applied because dark current is non-linear
    /// at short exposures and the noise model is more accurate when masters
    /// match their lights exactly.</summary>
    public static async Task<Image> BuildDarkMasterAsync(
        IReadOnlyList<FrameInfo> frames, CancellationToken cancellationToken = default)
    {
        ValidateInput(frames);
        var images = await LoadAllAsync(frames, cancellationToken);
        return BuildDarkMaster(images);
    }

    /// <summary>Combines flat frames: subtract the <paramref name="pedestal"/> (master bias or
    /// master dark-flat) if one is supplied, per-frame normalize each to mean=1
    /// (Bayer-aware), then per-pixel median across the stack. The
    /// normalization step is essential: flats with different transparency
    /// or illumination level would otherwise contribute unequally to the
    /// median. Bayer flats normalize each of the four R/G/G/B Bayer
    /// positions independently so the CFA channel balance is preserved
    /// (different colours hit different cell types at different
    /// efficiencies, even on a "uniform" light source).
    ///
    /// <para><b>Why the pedestal has to come off first.</b> A recorded flat is
    /// <c>offset + signal</c>, and normalising that to mean=1 divides the offset in too, which
    /// makes the master flatter than the illumination it is meant to describe. The flat then
    /// UNDER-corrects: the correction it applies is scaled by <c>signal / (offset + signal)</c>.
    /// Measured on a real ASI533MC Pro frame, the bias sits at 788 ADU against a flat level of
    /// 38,912, so 2.03%, which leaves about 0.41% of a 20% corner vignette uncorrected. That is
    /// invisible in a finished picture and is NOT invisible to a denoiser trained on the result:
    /// it is a smooth position-dependent error identical in every sub of a session, so it
    /// survives every Noise2Noise pair intact and can be learned as signal.</para>
    ///
    /// <para>A bias is the right pedestal at typical flat exposures and a dark-flat is
    /// interchangeable with it there: on the reference archive a 1.09 s dark-flat medians 784 ADU
    /// against the bias's 788, because dark current over a second on a cooled sensor is nil. Bias
    /// is preferred because it needs no exposure match and because <c>IMAGETYP</c> is reliable for
    /// it, whereas 11% of that archive's dark-flats are written as <c>DARK</c>.</para></summary>
    /// <param name="frames">Raw flat frames to combine.</param>
    /// <param name="pedestal">Master bias or master dark-flat to remove from each flat BEFORE
    /// normalising, or null to combine the flats raw (the historical behaviour).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<Image> BuildFlatMasterAsync(
        IReadOnlyList<FrameInfo> frames, Image? pedestal = null, CancellationToken cancellationToken = default)
    {
        ValidateInput(frames);
        var images = await LoadAllAsync(frames, cancellationToken);
        return BuildFlatMaster(images, pedestal);
    }

    // ---------- Pure-math overloads (testable without FITS I/O) ----------

    internal static Image BuildBiasMaster(IReadOnlyList<Image> images)
    {
        ValidateShapes(images);
        return CombineMedian(images, FrameType.Bias);
    }

    internal static Image BuildDarkMaster(IReadOnlyList<Image> images)
    {
        ValidateShapes(images);
        return CombineMedian(images, FrameType.Dark);
    }

    internal static Image BuildFlatMaster(IReadOnlyList<Image> images, Image? pedestal = null)
    {
        ValidateShapes(images);
        // Normalize each input to mean=1 in place. Inputs are throwaway; 
        // they only exist to feed this combine, and we'd otherwise pay a 2x
        // memory tax to keep originals + scaled copies in flight.
        foreach (var image in images)
        {
            // Same reason the subtraction is in place: Image.Subtract would allocate a second
            // full-frame copy per flat, and the input is about to be normalised in place anyway.
            if (pedestal is not null)
            {
                SubtractInPlace(image, pedestal);
            }
            NormalizeFlatInPlace(image);
        }
        return CombineMedian(images, FrameType.Flat);
    }

    /// <summary>Subtracts <paramref name="pedestal"/> from <paramref name="image"/> in place,
    /// clamped at zero. A shape mismatch is a no-op rather than a throw: the pedestal is an
    /// optional improvement resolved by header matching, so a wrongly-sized one must degrade to
    /// the previous uncalibrated behaviour instead of failing a stack that would otherwise run.
    /// Callers that can validate the shape should do so and log.</summary>
    private static void SubtractInPlace(Image image, Image pedestal)
    {
        if (pedestal.Width != image.Width || pedestal.Height != image.Height
            || pedestal.ChannelCount != image.ChannelCount)
        {
            return;
        }
        for (var c = 0; c < image.ChannelCount; c++)
        {
            var target = image.GetChannelArray(c);
            var source = pedestal.GetChannelArray(c);
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var v = target[y, x] - source[y, x];
                    target[y, x] = v > 0f ? v : 0f;
                }
            }
        }
    }

    // ---------- Helpers ----------

    private static void ValidateInput(IReadOnlyList<FrameInfo> frames)
    {
        if (frames is null || frames.Count == 0)
        {
            throw new ArgumentException("Master builder needs at least one frame.", nameof(frames));
        }
    }

    private static async Task<Image[]> LoadAllAsync(IReadOnlyList<FrameInfo> frames, CancellationToken ct)
    {
        var images = new Image[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            images[i] = await frames[i].LoadFullAsync(ct);
        }
        return images;
    }

    private static void ValidateShapes(IReadOnlyList<Image> images)
    {
        if (images.Count == 0)
        {
            throw new ArgumentException("Master builder needs at least one frame.", nameof(images));
        }
        var (c, w, h) = images[0].Shape;
        for (var i = 1; i < images.Count; i++)
        {
            var s = images[i].Shape;
            if (s.ChannelCount != c || s.Width != w || s.Height != h)
            {
                throw new ArgumentException(
                    $"Frame {i} shape mismatch: expected {c}x{h}x{w}, got {s.ChannelCount}x{s.Height}x{s.Width}.",
                    nameof(images));
            }
        }
    }

    /// <summary>
    /// Per-pixel median across the stack. Parallelized over output rows;
    /// each worker thread rents one scratch buffer of length N (frame count)
    /// from <see cref="ArrayPool{T}"/> and reuses it across that row's pixels.
    /// </summary>
    private static Image CombineMedian(IReadOnlyList<Image> images, FrameType resultType)
    {
        var (channelCount, width, height) = images[0].Shape;
        var n = images.Count;
        var output = Image.CreateChannelData(channelCount, height, width);

        // Snapshot the input channel arrays once so the inner loop is just
        // arithmetic: avoids the GetChannelArray virtual call per pixel.
        var inputChannels = new float[channelCount][][,];
        for (var c = 0; c < channelCount; c++)
        {
            inputChannels[c] = new float[n][,];
            for (var f = 0; f < n; f++)
            {
                inputChannels[c][f] = images[f].GetChannelArray(c);
            }
        }

        for (var c = 0; c < channelCount; c++)
        {
            var channelInputs = inputChannels[c];
            var outChannel = output[c];

            System.Threading.Tasks.Parallel.For(0, height, () => ArrayPool<float>.Shared.Rent(n),
                (row, _, buffer) =>
                {
                    for (var col = 0; col < width; col++)
                    {
                        for (var f = 0; f < n; f++)
                        {
                            buffer[f] = channelInputs[f][row, col];
                        }
                        var span = buffer.AsSpan(0, n);
                        span.Sort();
                        outChannel[row, col] = n % 2 == 1
                            ? span[n / 2]
                            : 0.5f * (span[n / 2 - 1] + span[n / 2]);
                    }
                    return buffer;
                },
                buffer => ArrayPool<float>.Shared.Return(buffer));
        }

        // Take metadata from the first input; the master inherits sensor /
        // exposure / temperature so downstream consumers know what light
        // frames it calibrates. FrameType is overwritten to the result type
        // so a saved master shows up correctly on the next folder scan.
        var seedMeta = images[0].ImageMeta with { FrameType = resultType };
        return new Image(
            data: output,
            bitDepth: BitDepth.Float32,
            maxValue: images[0].MaxValue,
            minValue: images[0].MinValue,
            pedestal: images[0].Pedestal,
            imageMeta: seedMeta);
    }

    /// <summary>
    /// Normalizes a single flat frame to mean=1 per "logical channel". For a
    /// true 3-channel image this is per-channel. For a 1-channel RGGB Bayer
    /// frame this is per-Bayer-quadrant: the four CFA positions are scaled
    /// independently so the mosaic's R/G/G/B balance is preserved. Operates
    /// in place; the input image's pixel data is mutated.
    /// <para>Internal so the dataset builder's <c>MasterCache</c> can put a FOREIGN
    /// master flat (whose scale is arbitrary, a [0,1] Astro Pixel Processor export
    /// or raw ADU) through the SAME normalization as a built one, so both satisfy the
    /// <see cref="Calibrator"/>'s "flat divided, mean ~ 1.0" contract identically.</para>
    /// </summary>
    internal static void NormalizeFlatInPlace(Image image)
    {
        if (image.ChannelCount == 1 && image.ImageMeta.SensorType is SensorType.RGGB)
        {
            NormalizeBayerFlatInPlace(image);
        }
        else
        {
            NormalizePerChannelFlatInPlace(image);
        }
    }

    private static void NormalizePerChannelFlatInPlace(Image image)
    {
        var (channelCount, width, height) = image.Shape;
        for (var c = 0; c < channelCount; c++)
        {
            var channel = image.GetChannelArray(c);
            var span = MemoryMarshal.CreateSpan(ref channel[0, 0], channel.Length);
            var mean = ComputeMean(span);
            if (mean > 0f)
            {
                var inv = 1f / mean;
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] *= inv;
                }
            }
        }
    }

    /// <summary>
    /// Bayer-aware per-quadrant normalization. For RGGB at the conventional
    /// offset (0, 0): R at even-row + even-col, G at the two mixed parities,
    /// B at odd-row + odd-col. The two green positions are pooled into a
    /// single mean (matches <see cref="Image.Histogram.cs"/>'s
    /// <c>BayerMediansInRegion</c> convention) so the resulting flat
    /// preserves the colour-channel balance of the original CFA.
    /// </summary>
    private static void NormalizeBayerFlatInPlace(Image image)
    {
        var (_, width, height) = image.Shape;
        var meta = image.ImageMeta;
        var offsetX = meta.BayerOffsetX;
        var offsetY = meta.BayerOffsetY;
        var channel = image.GetChannelArray(0);

        // Two-pass: accumulate Σ + N per Bayer position, then scale each.
        // Eight floats / four ints of state: fits in registers; no
        // allocation needed.
        double sumR = 0, sumG = 0, sumB = 0;
        long countR = 0, countG = 0, countB = 0;

        for (var y = 0; y < height; y++)
        {
            var yp = ((y - offsetY) % 2 + 2) % 2;
            for (var x = 0; x < width; x++)
            {
                var v = channel[y, x];
                if (float.IsNaN(v)) continue;
                var xp = ((x - offsetX) % 2 + 2) % 2;
                switch (yp * 2 + xp)
                {
                    case 0: sumR += v; countR++; break;
                    case 1: case 2: sumG += v; countG++; break;
                    case 3: sumB += v; countB++; break;
                }
            }
        }

        var meanR = countR > 0 ? sumR / countR : 0;
        var meanG = countG > 0 ? sumG / countG : 0;
        var meanB = countB > 0 ? sumB / countB : 0;
        var invR = meanR > 0 ? (float)(1.0 / meanR) : 0f;
        var invG = meanG > 0 ? (float)(1.0 / meanG) : 0f;
        var invB = meanB > 0 ? (float)(1.0 / meanB) : 0f;

        for (var y = 0; y < height; y++)
        {
            var yp = ((y - offsetY) % 2 + 2) % 2;
            for (var x = 0; x < width; x++)
            {
                var xp = ((x - offsetX) % 2 + 2) % 2;
                var inv = (yp * 2 + xp) switch
                {
                    0 => invR,
                    1 or 2 => invG,
                    _ => invB,
                };
                if (inv != 0f)
                {
                    channel[y, x] *= inv;
                }
            }
        }
    }

    private static float ComputeMean(ReadOnlySpan<float> span)
    {
        double sum = 0;
        long count = 0;
        for (var i = 0; i < span.Length; i++)
        {
            var v = span[i];
            if (!float.IsNaN(v))
            {
                sum += v;
                count++;
            }
        }
        return count > 0 ? (float)(sum / count) : 0f;
    }
}
