using System;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Choice of pivot target for background-neutralization gain computation.
/// All methods produce gains satisfying <c>out = val * g + (1-g)</c>, i.e.
/// highlights at <c>val=1</c> stay fixed; only the relation between channel
/// background levels changes.
/// </summary>
public enum BackgroundNeutralizationMethod
{
    /// <summary>Target = mean(R,G,B). Balances around the photographic average.
    /// Default; matches the historical SETI Astro Suite Pro pivot1 behaviour.</summary>
    Mean,

    /// <summary>Target = G. Green channel passes through unchanged
    /// (<c>gG = 1</c>); R and B scale so their background matches green.
    /// Useful for OSC sensors where green carries the strongest signal.</summary>
    GreenPivot,

    /// <summary>Target = min(R,G,B). The darkest channel passes through
    /// (<c>g = 1</c>) and the others scale up to match. No background signal
    /// is "thrown away" — useful when one channel is significantly cleaner.</summary>
    MinPivot,
}

/// <summary>
/// Background neutralization via pivot1 mode (port of SETI Astro Suite Pro).
/// Makes the sampled sky background neutral gray while protecting highlights.
/// </summary>
public static class BackgroundNeutralization
{
    /// <summary>
    /// Computes per-channel pivot1 neutralization gains from measured background region values.
    /// </summary>
    /// <param name="perChannelBg">Per-channel background values in pedestal-subtracted space
    /// (from <see cref="Image.ScanBackgroundRegion"/>).</param>
    /// <param name="method">Pivot target choice — affects which channel(s) stay fixed.
    /// Defaults to <see cref="BackgroundNeutralizationMethod.Mean"/> to preserve
    /// the behaviour expected by existing tests + call sites.</param>
    /// <param name="whiteBalance">Optional per-channel WB multiply applied AFTER bg-neut in the
    /// shader (<c>out = (val*g + (1-g)) * wb</c>). Only honoured for
    /// <see cref="BackgroundNeutralizationMethod.MinPivot"/>, where the gains are solved so the
    /// POST-WB background is neutral: pivot <c>K = min(bg_X*wb_X)</c>, per-channel target
    /// <c>t_X = K/wb_X</c>. Null (or a neutral triple) reduces to the WB-uncoupled MinPivot
    /// (<c>K = min(bg)</c>, one shared target) — bit-identical to the prior behaviour. Ignored by
    /// Mean / GreenPivot (no in-pipeline caller couples those to WB).</param>
    /// <returns>Per-channel gains where out = val * g + (1-g). Default (1,1,1) = no change.</returns>
    public static (float R, float G, float B) ComputeGains(
        ReadOnlySpan<float> perChannelBg,
        BackgroundNeutralizationMethod method = BackgroundNeutralizationMethod.Mean,
        (float R, float G, float B)? whiteBalance = null)
    {
        if (perChannelBg.Length < 3)
            return (1f, 1f, 1f);

        var mR = perChannelBg[0];
        var mG = perChannelBg[1];
        var mB = perChannelBg[2];

        if (method is BackgroundNeutralizationMethod.MinPivot)
        {
            // Post-WB MinPivot: choose gains so every channel's background lands on the same
            // post-WB level K. With wb=(1,1,1) the per-channel targets all collapse to min(bg) --
            // exactly the WB-uncoupled MinPivot. This is the in-pipeline preview/plate path
            // (MasterPreviewRenderer); the WB-coupling keeps the background neutral AFTER the
            // shader's per-channel WB multiply.
            var wb = whiteBalance ?? (1f, 1f, 1f);
            var k = MathF.Min(mR * wb.R, MathF.Min(mG * wb.G, mB * wb.B));
            return (ComputeChannelGain(mR, k / wb.R), ComputeChannelGain(mG, k / wb.G), ComputeChannelGain(mB, k / wb.B));
        }

        var t = method switch
        {
            BackgroundNeutralizationMethod.GreenPivot => mG,
            _                                         => (mR + mG + mB) / 3f,
        };

        var gR = ComputeChannelGain(mR, t);
        var gG = ComputeChannelGain(mG, t);
        var gB = ComputeChannelGain(mB, t);

        return (gR, gG, gB);
    }

    private static float ComputeChannelGain(float m, float t)
    {
        var denom = 1f - m;
        if (Math.Abs(denom) < 1e-8f)
            return 1f;
        var g = (1f - t) / denom;
        return Math.Clamp(g, 0f, 10f);
    }

    /// <summary>
    /// Applies background neutralization to image data on the CPU (for testing / non-GPU paths).
    /// Formula: out = max(val * g + (1-g), 0).
    /// </summary>
    public static void Apply(float[][,] data, (float R, float G, float B) gains)
    {
        Span<float> g = [gains.R, gains.G, gains.B];
        var maxC = Math.Min(data.Length, 3);
        for (var c = 0; c < maxC; c++)
        {
            var channel = data[c];
            var gc = g[c];
            var offset = 1f - gc;
            var h = channel.GetLength(0);
            var w = channel.GetLength(1);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var v = channel[y, x];
                    if (!float.IsNaN(v))
                        channel[y, x] = Math.Max(v * gc + offset, 0f);
                }
        }
    }

    /// <summary>
    /// Applies the GPU-equivalent transform to a single pixel value.
    /// Used by the GLSL stretchChannel() equivalent in tests.
    /// </summary>
    public static float ApplyToChannel(float val, float gain, float pedestal)
        => Math.Max((val - pedestal) * gain + (1f - gain), 0f);
}
