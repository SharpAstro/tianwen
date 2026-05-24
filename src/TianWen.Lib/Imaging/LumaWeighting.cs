namespace TianWen.Lib.Imaging;

/// <summary>
/// Standard luminance weighting profiles used by the Luma stretch path.
/// Resolved to an (R, G, B) weight triple in <see cref="StretchUniforms.LumaWeights"/>
/// before being passed to the CPU mirror and GPU shader, so per-sensor weight tables
/// derived from <c>FilterCurveDatabase.AllSensors</c> can replace the enum without
/// touching the UBO layout.
/// </summary>
public enum LumaWeighting
{
    /// <summary>Rec.709 / sRGB primaries: (0.2126, 0.7152, 0.0722).</summary>
    Rec709,
    /// <summary>Rec.601 / NTSC primaries: (0.299, 0.587, 0.114).</summary>
    Rec601,
    /// <summary>Rec.2020 wide-gamut primaries: (0.2627, 0.6780, 0.0593).</summary>
    Rec2020,
    /// <summary>Weights derived from the image's sensor QE x CFA filter throughput via
    /// <c>FilterCurveDatabase.TryComputeSensorLumaWeights</c>. Producer falls back to
    /// Rec.709 if the database is not loaded or the sensor cannot be resolved. Use this
    /// when you want the luma stretch matched to a specific OSC sensor's actual broadband
    /// response (e.g. IMX571/IMX533) rather than a standard photometric profile.</summary>
    SensorMatched,
}

public static class LumaWeightingExtensions
{
    /// <summary>Resolves the weighting profile to a normalised (R, G, B) weight triple.
    /// <see cref="LumaWeighting.SensorMatched"/> falls back to Rec.709 from this pure
    /// enum-to-triple lookup — the producer resolves the actual sensor-derived weights
    /// via <c>FilterCurveDatabase.TryComputeSensorLumaWeights(meta, ...)</c>.</summary>
    extension(LumaWeighting weighting)
    {
        public (float R, float G, float B) Weights => weighting switch
        {
            LumaWeighting.Rec601 => (0.299f, 0.587f, 0.114f),
            LumaWeighting.Rec2020 => (0.2627f, 0.6780f, 0.0593f),
            _ => (0.2126f, 0.7152f, 0.0722f),
        };

        /// <summary>Compute luma for one RGB pixel via this profile's weights.
        /// Inlined-on-hot-paths helper that replaces the dozen+ sites in the
        /// codebase that previously hardcoded <c>(0.2126f, 0.7152f, 0.0722f)</c>
        /// inline -- now the enum is the single source of truth.</summary>
        public float ToLuma(float r, float g, float b)
        {
            var (wR, wG, wB) = weighting.Weights;
            return wR * r + wG * g + wB * b;
        }
    }

    /// <summary>Compute luma for one RGB pixel from a pre-resolved
    /// <c>(R, G, B)</c> weight tuple. The tuple-taking form is for hot
    /// loops where the weights have already been pulled out of
    /// <c>StretchUniforms.LumaWeights</c> or computed via
    /// <c>FilterCurveDatabase.TryComputeSensorLumaWeights</c>.</summary>
    public static float ToLuma(this (float R, float G, float B) weights, float r, float g, float b)
        => weights.R * r + weights.G * g + weights.B * b;
}
