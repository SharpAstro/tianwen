using System;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A stars plate as a render screens it over the stretched picture (<see cref="Image.ScreenStars"/>): the LINEAR plate,
/// its magenta taken out, through the CLI's colour asinh stretch (<see cref="Image.AsinhStretch"/>, Rec.709 luminance, no
/// black point) at <paramref name="Beta"/>, saturated by <paramref name="Saturation"/> and clipped with its hue kept. Applied
/// per pixel, as the shader's star screen does, so both dials are uniforms and the plate is held once, unstretched.
/// </summary>
/// <remarks>
/// <para><b>Every step keeps a star's colour, because a per-channel step whitened it.</b> The asinh scales every channel of a
/// pixel by the one factor its luminance gives; a core it lifts past 1 is scaled down as a whole (<see cref="ClipKeepingHue"/>),
/// where clamping each channel to 1 left every bright core white.</para>
/// <para><b>Magenta is taken out first</b>, green raised to the lesser of red and blue where both exceed it
/// (<see cref="Demagenta"/>, PixInsight's inverted SCNR with maximum neutral): an APO not fully corrected leaves magenta
/// cores (#1777), and stars, which sit on the blackbody's red-to-blue line, carry no real magenta.</para>
/// </remarks>
/// <param name="Stars">The stars plate, linear, on the picture's grid.</param>
/// <param name="Beta">The asinh stretch strength, Siril's "stretch" and the CLI's <c>--asinh-beta</c>, at least 1.</param>
/// <param name="Saturation">How far each stretched channel is pushed from the pixel's luminance: 1 leaves the colour as
/// stretched.</param>
public sealed record ScreenedStars(Image Stars, float Beta, float Saturation)
{
    /// <summary>The luminance weights the factor and the saturation are read with, the CLI's <c>--asinh-luma</c> default.</summary>
    public static readonly (float R, float G, float B) LumaWeights = LumaWeighting.Rec709.Weights;

    /// <summary>One linear star pixel as screened: magenta out, the asinh, the saturation, the hue-keeping clip. Each
    /// channel in [0, 1].</summary>
    public (float R, float G, float B) Display(float r, float g, float b)
    {
        if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b))
        {
            return (0f, 0f, 0f);
        }
        g = Demagenta(r, g, b);
        var scale = Image.AsinhLumaScale(Luma(r, g, b), Beta, 1f / (float)Math.Asinh(Beta));
        (r, g, b) = (r * scale, g * scale, b * scale);
        var y = Luma(r, g, b);
        (r, g, b) = (y + ((r - y) * Saturation), y + ((g - y) * Saturation), y + ((b - y) * Saturation));
        return ClipKeepingHue(r, g, b);
    }

    /// <summary>Green raised to the lesser of red and blue: <c>max(G, min(R, B))</c>, which is SCNR's maximum neutral on the
    /// inverted pixel. Only a pixel whose red and blue BOTH exceed its green moves.</summary>
    public static float Demagenta(float r, float g, float b) => MathF.Max(g, MathF.Min(r, b));

    /// <summary>Negative channels to zero, then the whole pixel scaled down by its largest channel when that passes 1, so
    /// a core keeps its hue where a per-channel clamp would whiten it.</summary>
    public static (float R, float G, float B) ClipKeepingHue(float r, float g, float b)
    {
        (r, g, b) = (MathF.Max(r, 0f), MathF.Max(g, 0f), MathF.Max(b, 0f));
        var max = MathF.Max(r, MathF.Max(g, b));
        return max > 1f ? (r / max, g / max, b / max) : (r, g, b);
    }

    private static float Luma(float r, float g, float b) => (LumaWeights.R * r) + (LumaWeights.G * g) + (LumaWeights.B * b);
}
