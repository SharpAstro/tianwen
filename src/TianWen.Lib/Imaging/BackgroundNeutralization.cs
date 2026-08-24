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
    /// is "thrown away": useful when one channel is significantly cleaner.</summary>
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
    /// <param name="method">Pivot target choice: affects which channel(s) stay fixed.
    /// Defaults to <see cref="BackgroundNeutralizationMethod.Mean"/> to preserve
    /// the behaviour expected by existing tests + call sites.</param>
    /// <param name="whiteBalance">Per-channel WB multiply applied AFTER bg-neut in the shader
    /// (<c>out = (val*g + (1-g)) * wb</c>). Honoured by EVERY method: the gains are solved so the
    /// POST-WB background is neutral, with the pivot level <c>K</c> chosen per method over the
    /// WB-applied backgrounds and the per-channel target then <c>t_X = K/wb_X</c>. Null or a neutral
    /// triple reduces exactly to the WB-uncoupled form, so an uncalibrated image is unchanged.
    /// <para>
    /// Passing null when a calibration IS active is a bug, not an optimisation -- it neutralises the
    /// background the WB is about to re-tint. See the remarks in the body.
    /// </para></param>
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

        // EVERY method solves for a neutral POST-WB background, because the WB multiply happens
        // AFTER these gains in both the shader and the CPU mirror (pedestal -> bg-neut -> WB -> ...).
        // Neutralising the pre-WB background and then multiplying by a non-neutral triple simply
        // re-tints the thing that was just flattened.
        //
        // That is not hypothetical: on an SMC master whose background APP had already equalised
        // (bg = 0.0019 / 0.0020 / 0.0018) the Mean gains came out (1.00, 1.00, 1.00) -- a correct
        // answer to the wrong question -- and the SPCC triple (0.464, 1.000, 1.301) then took the
        // post-WB background to 0.00088 / 0.0020 / 0.00234, a 2.7x blue-over-red cast. The image
        // rendered visibly, wrongly blue while every individual step reported success.
        //
        // The pivot LEVEL is what the method chooses; the per-channel target is then that level
        // divided back through the WB, so the multiply lands on it. MinPivot already worked this
        // way; Mean and GreenPivot ignored the argument entirely, and Mean is the default.
        var wb = whiteBalance ?? (1f, 1f, 1f);
        var pR = mR * wb.R;
        var pG = mG * wb.G;
        var pB = mB * wb.B;

        var k = method switch
        {
            BackgroundNeutralizationMethod.GreenPivot => pG,
            BackgroundNeutralizationMethod.MinPivot => MathF.Min(pR, MathF.Min(pG, pB)),
            _ => (pR + pG + pB) / 3f,
        };

        // With a neutral WB every target collapses to the method's own pivot over the raw
        // backgrounds, so this is bit-identical to the WB-uncoupled form it replaces. GreenPivot
        // still passes green through untouched (t_G = pG/wb_G = mG, so g_G = 1) whatever the WB is.
        return (ComputeChannelGain(mR, k / wb.R),
                ComputeChannelGain(mG, k / wb.G),
                ComputeChannelGain(mB, k / wb.B));
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
