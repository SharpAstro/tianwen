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
    /// Computes the post-stretch background level by stretching the measured
    /// background values through <see cref="Image.StretchValue"/> — the same pipeline as the GLSL shader.
    /// </summary>
    public readonly float ComputePostStretchBackground(float[] perChannelBackground, float lumaBackground)
    {
        if (Mode is StretchMode.None)
        {
            // No stretch — background is the raw luminance
            return Math.Clamp(lumaBackground * NormFactor, 0.01f, 0.99f);
        }

        if (Mode is StretchMode.Luma)
        {
            // Luma mode: stretch the luma background value through the scalar Luma MTF.
            var bg = Image.StretchValue(lumaBackground, 1f, 0f, LumaStretch.Shadow, LumaStretch.Midtones, LumaStretch.Rescale);
            return Math.Clamp(bg, 0.01f, 0.99f);
        }

        // Per-channel or linked: stretch each channel's measured background, then weighted luminance
        var r = Image.StretchValue(GetChannelBg(perChannelBackground, 0), 1f, 0f, Shadows.R, Midtones.R, Rescale.R);
        var g = Image.StretchValue(GetChannelBg(perChannelBackground, 1), 1f, 0f, Shadows.G, Midtones.G, Rescale.G);
        var b = Image.StretchValue(GetChannelBg(perChannelBackground, 2), 1f, 0f, Shadows.B, Midtones.B, Rescale.B);

        var Y = LumaWeights.R * r + LumaWeights.G * g + LumaWeights.B * b;
        return Math.Clamp(Y, 0.01f, 0.99f);
    }

    private static float GetChannelBg(float[] perChannelBackground, int ch)
        => ch < perChannelBackground.Length ? perChannelBackground[ch] : perChannelBackground[0];
}
