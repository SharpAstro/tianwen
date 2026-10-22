using System;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A stars plate as a render screens it over the stretched picture (<see cref="Image.ScreenStars"/>): the LINEAR plate
/// through the CLI's colour asinh stretch (<see cref="Image.AsinhStretch"/>, Rec.709 luminance, no black point) at
/// <paramref name="Beta"/>, applied per pixel as the shader's star screen does, so the strength is a uniform and the plate is
/// held once, unstretched. Every channel of a pixel takes the one factor its luminance gives, so a star keeps its colour to
/// the core; a per-channel curve whitened the cores.
/// </summary>
/// <param name="Stars">The stars plate, linear, on the picture's grid.</param>
/// <param name="Beta">The asinh stretch strength, Siril's "stretch" and the CLI's <c>--asinh-beta</c>, at least 1.</param>
public sealed record ScreenedStars(Image Stars, float Beta)
{
    /// <summary>The luminance weights the factor is read with, the CLI's <c>--asinh-luma</c> default.</summary>
    public static readonly (float R, float G, float B) LumaWeights = LumaWeighting.Rec709.Weights;

    /// <summary>One linear star pixel as screened: each channel times the factor its luminance gives
    /// (<see cref="Image.AsinhLumaScale"/>). Unclipped, as the CLI's stretch is; the screen clamps.</summary>
    public (float R, float G, float B) Display(float r, float g, float b)
    {
        if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b))
        {
            return (0f, 0f, 0f);
        }
        var luma = (LumaWeights.R * r) + (LumaWeights.G * g) + (LumaWeights.B * b);
        var scale = Image.AsinhLumaScale(luma, Beta, 1f / (float)Math.Asinh(Beta));
        return (r * scale, g * scale, b * scale);
    }
}
