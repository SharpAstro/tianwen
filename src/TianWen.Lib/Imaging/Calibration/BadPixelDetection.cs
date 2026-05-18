using System;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Hot-pixel detection from a master dark frame. A hot pixel is one whose
/// dark current is so far above the median that no realistic dark-subtraction
/// (which removes the AVERAGE per-pixel offset) can eliminate the per-frame
/// shot-noise variance the pixel contributes. The standard fix is to drop
/// these pixels entirely -- mark them NaN in the calibrated light frames so
/// downstream integrators / drizzle accumulators skip them.
/// </summary>
public static class BadPixelDetection
{
    /// <summary>Subsample stride used when computing the per-channel median +
    /// MAD of the dark master. A 6224x4168 IMX455 dark at stride=32 yields
    /// ~25k samples per channel -- enough for a stable median estimate, ~10ms
    /// per channel. The dark master changes rarely (one build per session),
    /// so over-investing in precision here doesn't pay back.</summary>
    private const int StatStride = 32;

    /// <summary>Conventional MAD-to-Gaussian-sigma factor. Median +
    /// <see cref="GaussianFactor"/> * MAD approximates median + 1*sigma
    /// for a Gaussian distribution, which is what the
    /// <paramref name="sigmaThreshold"/> in <see cref="BuildMaskFromDark"/>
    /// effectively maps to.</summary>
    private const float GaussianFactor = 1.4826f;

    /// <summary>
    /// Per-channel hot-pixel mask: <c>true</c> bit = pixel is hotter than
    /// <paramref name="sigmaThreshold"/> Gaussian-sigma above the channel
    /// median, masked from downstream integration. One <see cref="BitMatrix"/>
    /// per channel keeps the memory footprint at 1 bit/pixel (8x denser
    /// than <c>bool[,]</c> -- ~3 MB per 6k frame channel vs 26 MB).
    /// </summary>
    /// <param name="darkMaster">Master dark frame.</param>
    /// <param name="sigmaThreshold">Threshold in Gaussian sigmas. Typical
    /// good value is 8 -- hot pixels usually score 100+ sigma above the
    /// channel median, so 8 is comfortably above the legitimate dark
    /// current spread without flagging warm-but-usable pixels. Pass 0 or
    /// negative to return <c>null</c> (disable masking).</param>
    /// <returns>A per-channel mask <c>BitMatrix[ChannelCount]</c> or
    /// <c>null</c> when masking is disabled.</returns>
    public static BitMatrix[]? BuildMaskFromDark(Image darkMaster, float sigmaThreshold)
    {
        if (sigmaThreshold <= 0f)
        {
            return null;
        }
        var channelCount = darkMaster.ChannelCount;
        var masks = new BitMatrix[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            var data = darkMaster.GetChannelArray(c);
            var (median, mad) = ComputeMedianAndMad(data);
            var threshold = median + sigmaThreshold * GaussianFactor * mad;
            var h = data.GetLength(0);
            var w = data.GetLength(1);
            var mask = new BitMatrix(h, w);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (data[y, x] > threshold)
                    {
                        mask[y, x] = true;
                    }
                }
            }
            masks[c] = mask;
        }
        return masks;
    }

    /// <summary>
    /// Counts the masked pixels (true bits) across all channels. Useful
    /// for the pipeline log -- a typical CMOS sensor reports 50-5000 hot
    /// pixels at sigma=8, larger or smaller counts hint at a bad sigma
    /// choice or a corrupted dark.
    /// </summary>
    public static int CountMaskedPixels(BitMatrix[]? mask, int width, int height)
    {
        if (mask is null) return 0;
        var total = 0;
        for (var c = 0; c < mask.Length; c++)
        {
            var m = mask[c];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (m[y, x]) total++;
                }
            }
        }
        return total;
    }

    private static (float Median, float Mad) ComputeMedianAndMad(float[,] data)
    {
        var h = data.GetLength(0);
        var w = data.GetLength(1);
        var sampleCount = ((h + StatStride - 1) / StatStride) * ((w + StatStride - 1) / StatStride);
        var samples = new float[sampleCount];
        var idx = 0;
        for (var y = 0; y < h; y += StatStride)
        {
            for (var x = 0; x < w; x += StatStride)
            {
                samples[idx++] = data[y, x];
            }
        }
        Array.Sort(samples, 0, idx);
        var median = samples[idx / 2];
        // Reuse the samples buffer for the deviation array -- we've already
        // consumed the sorted samples to read the median.
        for (var i = 0; i < idx; i++)
        {
            samples[i] = MathF.Abs(samples[i] - median);
        }
        Array.Sort(samples, 0, idx);
        var mad = samples[idx / 2];
        return (median, mad);
    }
}
