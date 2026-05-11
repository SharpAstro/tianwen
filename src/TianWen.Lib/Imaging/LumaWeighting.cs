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
}

public static class LumaWeightingExtensions
{
    /// <summary>Resolves the weighting profile to a normalised (R, G, B) weight triple.</summary>
    extension(LumaWeighting weighting)
    {
        public (float R, float G, float B) Weights => weighting switch
        {
            LumaWeighting.Rec601 => (0.299f, 0.587f, 0.114f),
            LumaWeighting.Rec2020 => (0.2627f, 0.6780f, 0.0593f),
            _ => (0.2126f, 0.7152f, 0.0722f),
        };
    }
}
