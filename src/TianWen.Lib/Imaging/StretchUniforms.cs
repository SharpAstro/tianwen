using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Stretch parameters ready to pass as GPU shader uniforms.
/// Each field is a 3-component vector (R/G/B or replicated for mono/linked).
/// </summary>
public readonly record struct StretchUniforms(
    StretchMode Mode,
    float NormFactor,
    (float R, float G, float B) Pedestal,
    (float R, float G, float B) Shadows,
    (float R, float G, float B) Midtones,
    (float R, float G, float B) Highlights,
    (float R, float G, float B) Rescale)
{
    /// <summary>Per-channel white balance multipliers from Tycho-2 color calibration. Default (1,1,1) = no adjustment.</summary>
    public (float R, float G, float B) WhiteBalance { get; init; } = (1f, 1f, 1f);

    /// <summary>Per-channel background neutralization gains from pivot1 background sampling. Default (1,1,1) = no adjustment.
    /// GPU applies: out = norm * g + (1-g) per channel, which neutralizes the background to the mean of its per-channel medians.</summary>
    public (float R, float G, float B) BackgroundNeutralization { get; init; } = (1f, 1f, 1f);

    /// <summary>Curve mode: 0 = power-law boost (ApplyBoost), 1 = Fritsch-Carlson spline LUT.</summary>
    public int CurvesMode { get; init; }

    /// <summary>33 Fritsch-Carlson knots at <c>i/32</c> for <c>i = 0..32</c>. The GPU uploads
    /// these into 9 std140 vec4 slots (the trailing 3 floats stay zero and are never read by
    /// the shader). Empty when <see cref="CurvesMode"/> is 0.</summary>
    public ImmutableArray<float> CurveData { get; init; } = [];

    /// <summary>RGB luminance weights used by the Luma stretch path and post-stretch luma
    /// recombination. Default Rec.709 = (0.2126, 0.7152, 0.0722). Resolved from
    /// <see cref="LumaWeighting"/> by the producer; left as a free triple so per-sensor
    /// weights (sensor QE x CFA throughput integrated over the visible) can replace the
    /// standard profile without UBO churn.</summary>
    public (float R, float G, float B) LumaWeights { get; init; } = (0.2126f, 0.7152f, 0.0722f);

    /// <summary>Scalar (Shadow, Midtones, Rescale) MTF parameters for the Luma stretch
    /// branch. Populated by the producer only when <see cref="Mode"/> is
    /// <see cref="StretchMode.Luma"/>; default is zero. Carried in a dedicated field so the
    /// per-channel <see cref="Shadows"/>/<see cref="Midtones"/>/<see cref="Rescale"/> can
    /// simultaneously hold the per-channel linked values, which the shader needs when
    /// <see cref="LumaBlend"/> is less than 1 (so it can compute the linked output and blend).</summary>
    public (float Shadow, float Midtones, float Rescale) LumaStretch { get; init; }

    /// <summary>Blend between the per-channel linked stretch and the luma stretch when
    /// <see cref="Mode"/> is <see cref="StretchMode.Luma"/>: 0 = pure linked output (luma
    /// stretch ignored), 1 = pure luma output (default, current behaviour). Mirrors the
    /// SetiAstro "Luma blend" slider; the in-between values tame the saturation punch
    /// without dropping back to plain linked mode.</summary>
    public float LumaBlend { get; init; } = 1f;

    /// <summary>Post-stretch global gain applied after curves + HDR but before the final
    /// clamp: <c>out = clamp(out * NormalizeScale)</c>. 1 = no-op (default).
    /// When normalisation is requested, the producer predicts the post-stretch max via
    /// <c>Image.PredictPostStretchMaxScale</c> and sets this to <c>1 / max</c> so the
    /// brightest pixel in the rendered output lands at 1.0.</summary>
    public float NormalizeScale { get; init; } = 1f;

    /// <summary>
    /// The finish's contrast against the background, 0 (off, the default) to 1: an S-curve on the stretched luminance
    /// between the background and white, through the signal mask (<see cref="Image.FinishMask"/>), the colour kept. The
    /// Affinity "masked contrast boost". Applied after the soft clip, by <see cref="Image.ApplyFinish"/> and the shader's
    /// <c>applyFinish</c>, which must agree.
    /// </summary>
    public float FinishContrast { get; init; }

    /// <summary>
    /// The finish's saturation, 1 (off, the default) and up: each channel pushed away from the luminance through the same
    /// mask, so the background's noise takes no colour. Ignored on a mono frame.
    /// </summary>
    /// <remarks>Stored as its distance from 1, so a <c>default</c> or <c>new StretchUniforms()</c>, which skip property
    /// initialisers, reads 1 (off) and not 0, which would turn every such render grey.</remarks>
    public float FinishSaturation
    {
        get => 1f + _finishSaturationOffset;
        init => _finishSaturationOffset = value - 1f;
    }

    private readonly float _finishSaturationOffset;

    /// <summary>Whether the finish changes anything: the renders skip it otherwise.</summary>
    public bool HasFinish => FinishContrast > 0f || FinishSaturation != 1f;

    /// <summary>
    /// Where the background reduce takes the stretched sky, as a share of the display range: 1 (off, the default) down
    /// toward 0. The sky is curved down to this level by <see cref="Image.ReduceBackgroundValue"/>, the CLI's
    /// <c>--dual-stretch</c> reduce, straight after the stretch and anchored on the post-stretch sky the render is given
    /// (its <c>curvesMidpoint</c>); a sky already at or below it is left alone. The render and the shader's
    /// <c>reduceBackground</c> must agree.
    /// </summary>
    /// <remarks>
    /// <para><b>A level, not a share of the sky, because a share multiplied with the stretch's own target</b> (#1792): at
    /// 0.55 of the sky, the default target 0.2 put it at 0.11 while the 0.1 preset put it at 0.055. A level puts it at
    /// the same place whatever the target, which is how Astro Pixel Processor's background level % reads too.</para>
    /// <para>Stored as its distance from 1, so a <c>default</c> or <c>new StretchUniforms()</c> reads 1 (off), as
    /// <see cref="FinishSaturation"/> does.</para>
    /// </remarks>
    public float BackgroundSky
    {
        get => 1f + _backgroundSkyOffset;
        init => _backgroundSkyOffset = value - 1f;
    }

    private readonly float _backgroundSkyOffset;

    /// <summary>Whether the background reduce is asked for: a stretched frame with a sky level below 1. Whether it moves
    /// a given sky is <see cref="BackgroundReduceCompression"/>'s answer.</summary>
    public bool HasBackgroundReduce => BackgroundSky < 1f && BackgroundSky > 0f && Mode is not StretchMode.None;

    /// <summary>
    /// The compression the curve applies to a post-stretch sky at <paramref name="background"/>: the level over the sky,
    /// at most 1, so a sky already at or below the level is not moved. 1 where there is no reduce or the sky is outside the
    /// curve's domain, (0, 0.5). The shader's <c>reduceCompression</c> is the same arithmetic.
    /// </summary>
    public float BackgroundReduceCompression(float background) => HasBackgroundReduce && background > 0f && background < 0.5f
        ? MathF.Min(1f, BackgroundSky / background)
        : 1f;

    /// <summary>Where a post-stretch sky at <paramref name="background"/> lands after the reduce: the level every later
    /// stage pivots on.</summary>
    public float ReducedBackground(float background) => background * BackgroundReduceCompression(background);
    /// <summary>
    /// Computes the post-stretch background level by stretching the measured
    /// background values through <see cref="Image.StretchValue"/>; the same pipeline as the GLSL shader.
    /// </summary>
    public readonly float ComputePostStretchBackground(float[] perChannelBackground, float lumaBackground)
    {
        if (Mode is StretchMode.None)
        {
            // No stretch: background is the raw luminance
            return Math.Clamp(lumaBackground * NormFactor, 0.01f, 0.99f);
        }

        if (Mode is StretchMode.Luma)
        {
            // Luma mode: stretch the luma background through the scalar Luma MTF, but through the same
            // pre-curve chain first -- weighted, because the luma curve itself was positioned against
            // the weighted WB and neutralisation (StretchSolver's Luma branch).
            var lumaBn = LumaWeights.R * BackgroundNeutralization.R
                + LumaWeights.G * BackgroundNeutralization.G
                + LumaWeights.B * BackgroundNeutralization.B;
            var lumaWb = LumaWeights.R * WhiteBalance.R + LumaWeights.G * WhiteBalance.G + LumaWeights.B * WhiteBalance.B;
            var lumaNorm = Math.Max((lumaBackground * lumaBn + (1f - lumaBn)) * lumaWb, 0f);
            var bg = Image.StretchValue(lumaNorm, 1f, 0f, LumaStretch.Shadow, LumaStretch.Midtones, LumaStretch.Rescale);
            return Math.Clamp(bg, 0.01f, 0.99f);
        }

        // Per-channel or linked: each channel's measured background through the SAME pre-curve chain
        // the render applies (neutralisation, then white balance, then the curve), via the one
        // definition of it. The measured background is already pedestal-subtracted and unit-scaled,
        // which is why this enters after the pedestal rather than at StretchChannelCpu.
        var r = Image.StretchNormalisedCpu(GetChannelBg(perChannelBackground, 0), 0, this);
        var g = Image.StretchNormalisedCpu(GetChannelBg(perChannelBackground, 1), 1, this);
        var b = Image.StretchNormalisedCpu(GetChannelBg(perChannelBackground, 2), 2, this);

        var Y = LumaWeights.R * r + LumaWeights.G * g + LumaWeights.B * b;
        return Math.Clamp(Y, 0.01f, 0.99f);
    }

    private static float GetChannelBg(float[] perChannelBackground, int ch)
        => ch < perChannelBackground.Length ? perChannelBackground[ch] : perChannelBackground[0];
}
