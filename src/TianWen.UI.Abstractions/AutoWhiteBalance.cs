using System;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Gray-world auto white-balance for the viewer's manual WB sliders. Computes per-channel multipliers
/// that equalise the channel means of the <i>illuminated</i> region (excluding the dark sky background),
/// normalised so the dimmest channel stays at 1.0 and the brighter (cast) channels are cut down -- i.e.
/// it only ever attenuates, never amplifies (so it can't boost a noisy channel). The result is a triple
/// fed into <see cref="ViewerState.ManualWhiteBalance"/>; the manual sliders then act as the fine-tune /
/// escape hatch (e.g. warming a planet that gray-world rendered a touch cold).
/// <para>
/// This is the planetary-friendly counterpart to the star-based photometric calibration (Tycho-2 / SPCC),
/// which needs field stars and so does nothing on a planetary SER. Gray-world works on the planet's own
/// pixels and is stable across the noisy individual frames of a capture (means don't flicker the way a
/// white-patch peak would).
/// </para>
/// </summary>
public static class AutoWhiteBalance
{
    /// <summary>Minimum / maximum WB multiplier. The single source of truth for both the auto-WB clamp and
    /// the manual WB slider range, so the auto result is always reachable by the sliders.</summary>
    public const float MinMultiplier = 0.5f;
    public const float MaxMultiplier = 2.0f;

    /// <summary>Fraction of the brightest pixel used as the "is this an illuminated pixel" threshold. Pixels
    /// at or below this are treated as sky/background and excluded from the channel means.</summary>
    private const float LitFraction = 0.2f;

    /// <summary>
    /// Computes gray-world WB multipliers for the current frame of <paramref name="source"/>, or null when
    /// the source is not colour (mono) or has no illuminated pixels. Handles both a raw Bayer mosaic
    /// (1-channel RGGB, sampled at its CFA sites) and a de-interleaved 3-channel colour source.
    /// </summary>
    public static (float R, float G, float B)? GrayWorld(IPreviewSource source)
    {
        if (source.SensorType is SensorType.RGGB && source.ChannelCount == 1)
        {
            return GrayWorldBayer(source.GetChannelData(0), source.Width, source.Height,
                source.BayerOffsetX, source.BayerOffsetY);
        }
        if (source.ChannelCount >= 3)
        {
            return GrayWorldRgb(source.GetChannelData(0), source.GetChannelData(1), source.GetChannelData(2));
        }
        return null;
    }

    /// <summary>Gray-world over a 3-channel [0,1] colour frame. Pixels whose luma exceeds
    /// <see cref="LitFraction"/> of the peak luma contribute to the per-channel means.</summary>
    public static (float R, float G, float B)? GrayWorldRgb(
        ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b)
    {
        var n = Math.Min(r.Length, Math.Min(g.Length, b.Length));
        if (n == 0)
        {
            return null;
        }

        // Pass 1: peak luma -> illumination threshold. Rec.601-ish weights; the exact weighting is
        // immaterial here, it only gates which pixels count as "planet" vs "sky".
        var peak = 0f;
        for (var i = 0; i < n; i++)
        {
            var luma = 0.3f * r[i] + 0.59f * g[i] + 0.11f * b[i];
            if (luma > peak)
            {
                peak = luma;
            }
        }
        if (peak <= 0f)
        {
            return null;
        }

        var threshold = peak * LitFraction;
        double sumR = 0, sumG = 0, sumB = 0;
        long count = 0;
        for (var i = 0; i < n; i++)
        {
            var luma = 0.3f * r[i] + 0.59f * g[i] + 0.11f * b[i];
            if (luma <= threshold)
            {
                continue;
            }
            sumR += r[i];
            sumG += g[i];
            sumB += b[i];
            count++;
        }

        return count == 0 ? null : Normalize(sumR / count, sumG / count, sumB / count);
    }

    /// <summary>Gray-world over a 1-channel [0,1] RGGB mosaic, sampling each CFA colour at its own sites.
    /// <paramref name="bayerOffsetX"/>/<paramref name="bayerOffsetY"/> place the red site (their parity);
    /// blue is the diagonal opposite, green is the other two.</summary>
    public static (float R, float G, float B)? GrayWorldBayer(
        ReadOnlySpan<float> mosaic, int width, int height, int bayerOffsetX, int bayerOffsetY)
    {
        if (width <= 0 || height <= 0 || mosaic.Length < width * height)
        {
            return null;
        }

        // Pass 1: peak mosaic value -> illumination threshold (planet pixels are bright in every CFA colour).
        var peak = 0f;
        for (var i = 0; i < mosaic.Length; i++)
        {
            if (mosaic[i] > peak)
            {
                peak = mosaic[i];
            }
        }
        if (peak <= 0f)
        {
            return null;
        }

        var threshold = peak * LitFraction;
        var rx = bayerOffsetX & 1;
        var ry = bayerOffsetY & 1;
        double sumR = 0, sumG = 0, sumB = 0;
        long cntR = 0, cntG = 0, cntB = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var isRedRow = (y & 1) == ry;
            for (var x = 0; x < width; x++)
            {
                var v = mosaic[row + x];
                if (v <= threshold)
                {
                    continue;
                }
                var isRedCol = (x & 1) == rx;
                if (isRedRow && isRedCol) { sumR += v; cntR++; }
                else if (!isRedRow && !isRedCol) { sumB += v; cntB++; }
                else { sumG += v; cntG++; }
            }
        }

        return cntR == 0 || cntG == 0 || cntB == 0
            ? null
            : Normalize(sumR / cntR, sumG / cntG, sumB / cntB);
    }

    /// <summary>Turns per-channel means into only-cut multipliers (dimmest channel -> 1.0), clamped to the
    /// reachable slider range.</summary>
    private static (float R, float G, float B)? Normalize(double meanR, double meanG, double meanB)
    {
        if (meanR <= 0 || meanG <= 0 || meanB <= 0)
        {
            return null;
        }

        // Target the dimmest mean so every multiplier is <= 1 (attenuate the over-represented channels;
        // never amplify a dim, noisy one).
        var target = Math.Min(meanR, Math.Min(meanG, meanB));
        return (
            Math.Clamp((float)(target / meanR), MinMultiplier, MaxMultiplier),
            Math.Clamp((float)(target / meanG), MinMultiplier, MaxMultiplier),
            Math.Clamp((float)(target / meanB), MinMultiplier, MaxMultiplier));
    }
}
