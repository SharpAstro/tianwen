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

    /// <summary>Curve mode: 0 = power-law boost (ApplyBoost), 1 = Fritsch-Carlson spline LUT.</summary>
    public int CurvesMode { get; init; }

    /// <summary>36 floats packed as 9 vec4s (33 knots + 3 pad) for the Fritsch-Carlson LUT. Empty when mode=0.</summary>
    public ImmutableArray<float> CurveData { get; init; } = [];
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
            // Luma mode: stretch the luma background value
            var bg = Image.StretchValue(lumaBackground, 1f, 0f, Shadows.R, Midtones.R, Rescale.R);
            return Math.Clamp(bg, 0.01f, 0.99f);
        }

        // Per-channel or linked: stretch each channel's measured background, then Rec.709 luminance
        var r = Image.StretchValue(GetChannelBg(perChannelBackground, 0), 1f, 0f, Shadows.R, Midtones.R, Rescale.R);
        var g = Image.StretchValue(GetChannelBg(perChannelBackground, 1), 1f, 0f, Shadows.G, Midtones.G, Rescale.G);
        var b = Image.StretchValue(GetChannelBg(perChannelBackground, 2), 1f, 0f, Shadows.B, Midtones.B, Rescale.B);

        var Y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        return Math.Clamp(Y, 0.01f, 0.99f);
    }

    private static float GetChannelBg(float[] perChannelBackground, int ch)
        => ch < perChannelBackground.Length ? perChannelBackground[ch] : perChannelBackground[0];
}
