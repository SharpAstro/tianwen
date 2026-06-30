using System;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Pure-math producer of <see cref="StretchUniforms"/> + the sky-background
/// white-balance fallback. Lives in <c>TianWen.Lib</c> (no GPU, no UI, no
/// <c>AstroImageDocument</c> dependency) so the Lib-side stacking renderer
/// (<see cref="Stacking.MasterPreviewRenderer"/>) and the
/// <c>TianWen.UI.Abstractions</c> viewer share ONE implementation. The viewer's
/// <c>AstroImageDocument.ComputeStretchUniforms</c> / <c>ComputeSkyBackgroundWB</c>
/// forward here, so it remains the single producer the GLSL + CPU stretch paths
/// agree on.
/// </summary>
public static class StretchSolver
{
    /// <summary>
    /// Computes stretch shader uniforms from stats directly -- no
    /// <c>AstroImageDocument</c> needed. When <paramref name="whiteBalance"/> is
    /// non-null, per-channel stats are scaled by the WB multipliers before
    /// deriving shadows/midtones/rescale, so the shadow clip lands in the same
    /// coordinate space as the post-WB norm in the GLSL stretch loop. Without this
    /// adjustment, channels reduced by WB (e.g. B with wb=0.94) would have their
    /// post-WB norm fall below the un-adjusted shadow and clamp to zero, tinting
    /// the bg toward the boosted channels.
    /// <para>
    /// <paramref name="shaderWhiteBalance"/> is the triple written to
    /// <see cref="StretchUniforms.WhiteBalance"/> (the per-channel shader multiply).
    /// It defaults to <paramref name="whiteBalance"/>, so a single-WB caller is
    /// unchanged. Passing a <i>different</i> value decouples the shader multiply
    /// from the stat scaling: that is how a MANUAL WB slider stays visible even in
    /// a per-channel auto-normalised stretch (the manual portion multiplies but
    /// does not scale the stats, so the curve can't re-absorb it), while the AUTO
    /// calibration keeps scaling the stats to preserve a neutral background.
    /// </para>
    /// </summary>
    public static StretchUniforms ComputeStretchUniforms(
        StretchMode mode,
        StretchParameters parameters,
        ChannelStretchStats[] perChannelStats,
        ChannelStretchStats? lumaStats,
        float imageMaxValue,
        (float R, float G, float B)? whiteBalance = null,
        (float R, float G, float B)? lumaWeights = null,
        (float R, float G, float B)? shaderWhiteBalance = null)
    {
        // Default luma weighting is Rec.709 -- matches the previous hardcoded constants
        // and keeps existing callers (no lumaWeights argument) on the same numerical path.
        var weights = lumaWeights ?? LumaWeighting.Rec709.Weights;

        // The shader multiply defaults to the stat-scaling WB, so single-WB callers are unchanged.
        var shaderWb = shaderWhiteBalance ?? whiteBalance ?? (1f, 1f, 1f);

        if (mode is StretchMode.None)
        {
            // Linear display: there is no stretch curve to carry a WB multiply, so the WB is applied
            // directly in the None path of the shader / CPU renderer from this uniform. A neutral shaderWb
            // leaves the previous default-(1,1,1) behaviour identical. This is what lets a manual WB slider
            // (or auto calibration) act on a SER, which opens in linear mode (StretchMode.None).
            return new StretchUniforms(StretchMode.None, 1f, default, default, default, default, default)
            { LumaWeights = weights, WhiteBalance = shaderWb };
        }

        var normFactor = imageMaxValue > 1.0f + float.Epsilon ? 1f / imageMaxValue : 1f;
        var factor = parameters.Factor;
        var clipping = parameters.ShadowsClipping;
        var wb = whiteBalance ?? (1f, 1f, 1f);

        if (mode is StretchMode.Luma && lumaStats is { } luma)
        {
            // Luma stretch uses the chosen luminance weighting over the WB-adjusted channels;
            // scale luma median/mad by the weighted WB so the luma stretch aligns with the
            // actual luminance values produced post-WB.
            var lumaWb = weights.R * wb.R + weights.G * wb.G + weights.B * wb.B;
            var (s, m, h, r) = Image.ComputeStretchParameters(luma.Median * lumaWb, luma.Mad * lumaWb, factor, clipping);

            // Use per-channel pedestals for background subtraction (avoids green cast from RGGB);
            // luma-derived midtone/shadows/rescale live in LumaStretch.
            var chStats = perChannelStats;
            var ped0 = chStats.Length > 0 ? chStats[0].Pedestal : luma.Pedestal;
            var ped1 = chStats.Length > 1 ? chStats[1].Pedestal : ped0;
            var ped2 = chStats.Length > 2 ? chStats[2].Pedestal : ped0;

            // Always compute per-channel linked params alongside the luma scalar so that a
            // LumaBlend < 1 caller has the linked branch ready in the shader without a UBO
            // re-upload. Falls back to channel 0 if a stat slot is missing.
            var lch0 = chStats.Length > 0 ? chStats[0] : new ChannelStretchStats(0f, luma.Median, luma.Mad);
            var lch1 = chStats.Length > 1 ? chStats[1] : lch0;
            var lch2 = chStats.Length > 2 ? chStats[2] : lch0;
            var lp0 = Image.ComputeStretchParameters(lch0.Median * wb.R, lch0.Mad * wb.R, factor, clipping);
            var lp1 = Image.ComputeStretchParameters(lch1.Median * wb.G, lch1.Mad * wb.G, factor, clipping);
            var lp2 = Image.ComputeStretchParameters(lch2.Median * wb.B, lch2.Mad * wb.B, factor, clipping);

            return new StretchUniforms(
                Mode: StretchMode.Luma,
                NormFactor: normFactor,
                Pedestal: (ped0, ped1, ped2),
                Shadows: ((float)lp0.Shadows, (float)lp1.Shadows, (float)lp2.Shadows),
                Midtones: ((float)lp0.Midtones, (float)lp1.Midtones, (float)lp2.Midtones),
                Highlights: ((float)lp0.Highlights, (float)lp1.Highlights, (float)lp2.Highlights),
                Rescale: ((float)lp0.Rescale, (float)lp1.Rescale, (float)lp2.Rescale))
            {
                WhiteBalance = shaderWb,
                LumaWeights = weights,
                LumaStretch = ((float)s, (float)m, (float)r),
            };
        }

        // Linked or unlinked
        var stats = perChannelStats;
        var ch0 = stats.Length > 0 ? stats[0] : default;
        var ch1 = stats.Length > 1 ? stats[1] : ch0;
        var ch2 = stats.Length > 2 ? stats[2] : ch0;

        if (mode is StretchMode.Linked)
        {
            ch1 = ch0;
            ch2 = ch0;
        }

        // WB scales each channel's value range linearly: post-WB_median = wb * pre-WB_median,
        // post-WB_mad = wb * pre-WB_mad. Compute stretch params from those scaled stats so
        // shadows/rescale/midtones are consistent with the post-WB norm the shader sees.
        var p0 = Image.ComputeStretchParameters(ch0.Median * wb.R, ch0.Mad * wb.R, factor, clipping);
        var p1 = Image.ComputeStretchParameters(ch1.Median * wb.G, ch1.Mad * wb.G, factor, clipping);
        var p2 = Image.ComputeStretchParameters(ch2.Median * wb.B, ch2.Mad * wb.B, factor, clipping);

        return new StretchUniforms(
            Mode: mode,
            NormFactor: normFactor,
            Pedestal: (ch0.Pedestal, ch1.Pedestal, ch2.Pedestal),
            Shadows: ((float)p0.Shadows, (float)p1.Shadows, (float)p2.Shadows),
            Midtones: ((float)p0.Midtones, (float)p1.Midtones, (float)p2.Midtones),
            Highlights: ((float)p0.Highlights, (float)p1.Highlights, (float)p2.Highlights),
            Rescale: ((float)p0.Rescale, (float)p1.Rescale, (float)p2.Rescale))
        { WhiteBalance = shaderWb, LumaWeights = weights };
    }

    /// <summary>
    /// Derives an (R, G, B) white-balance triple from the median colour of the
    /// darkest 10% of star-masked sky pixels in a 3-channel image. The
    /// "sky should be grey" assumption: divide G by R and G by B, clamp the
    /// ratios to [0.5, 2], and return them as the multipliers that, applied
    /// to the channels, neutralise the sky cast.
    /// <para>Pure function -- safe to call from headless pipelines (the
    /// stacking E2E uses it as a fallback when SPCC can't run because no
    /// plate-solve / sensor throughput is available). Returns <c>null</c>
    /// when too few clean samples remain after the star-mask exclusion
    /// (&lt; 100) or green collapses to ~0.</para>
    /// </summary>
    public static (float R, float G, float B)? ComputeSkyBackgroundWB(Image image, BitMatrix starMask)
    {
        var (_, width, height) = image.Shape;
        var x0 = width / 20; var x1 = width - x0;   // skip 5% border
        var y0 = height / 20; var y1 = height - y0;
        var maxSamples = width * height / 16;
        var sr = new float[maxSamples]; var sg = new float[maxSamples]; var sb = new float[maxSamples];
        var yBuf = new float[maxSamples];
        var n = 0;

        for (var y = y0; y < y1 && n < maxSamples; y += 4)
            for (var x = x0; x < x1 && n < maxSamples; x += 4)
            {
                if (starMask[y, x]) continue;
                var r = image[0, y, x]; var g = image[1, y, x]; var b = image[2, y, x];
                if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b)) continue;
                yBuf[n] = LumaWeighting.Rec709.ToLuma(r, g, b);
                sr[n] = r; sg[n] = g; sb[n] = b;
                n++;
            }

        if (n < 100) return null;

        // Sort by luminance to find darkest 10% pixel indices
        var idx = new int[n]; for (var i = 0; i < n; i++) idx[i] = i;
        Array.Sort(yBuf, idx, 0, n);
        var k = Math.Max(n / 10, 10);

        // Collect RGB values of darkest k pixels and median-filter
        var darkR = new float[k]; var darkG = new float[k]; var darkB = new float[k];
        for (var i = 0; i < k; i++) { darkR[i] = sr[idx[i]]; darkG[i] = sg[idx[i]]; darkB[i] = sb[idx[i]]; }
        Array.Sort(darkR); Array.Sort(darkG); Array.Sort(darkB);

        var medR = darkR[k / 2]; var medG = darkG[k / 2]; var medB = darkB[k / 2];
        if (medG <= 1e-7f) return null;

        return (Math.Clamp(medG / medR, 0.5f, 2f), 1f, Math.Clamp(medG / medB, 0.5f, 2f));
    }
}
