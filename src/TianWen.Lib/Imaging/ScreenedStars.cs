using System;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A stars plate as a render screens it over the stretched picture (<see cref="Image.ScreenStars"/>): the LINEAR plate, scaled
/// onto the frame's own full scale by <paramref name="Scale"/> and stretched by StarStretch's fixed-midtones curve at
/// <paramref name="Midtones"/>. The render applies the curve per pixel, as the shader's star screen does, so the stars' strength
/// is a uniform and the plate is held once, unstretched.
/// </summary>
/// <param name="Stars">The stars plate, linear, on the picture's grid.</param>
/// <param name="Scale">What a linear star sample is multiplied by before the curve: one over the recombined frame's peak.</param>
/// <param name="Midtones">The curve's midtones balance, <see cref="MidtonesFor"/> of a StarStretch amount.</param>
public sealed record ScreenedStars(Image Stars, float Scale, float Midtones)
{
    /// <summary>StarStretch's midtones balance for a slider <paramref name="amount"/>: <c>1 / (3^amount + 1)</c>, so 0 is the
    /// identity (0.5) and each step of one triples the lift.</summary>
    public static double MidtonesFor(double amount) => 1.0 / (Math.Pow(3.0, amount) + 1.0);

    /// <summary>
    /// The plate at a StarStretch <paramref name="amount"/> on a frame whose brightest sample is <paramref name="peak"/>. A
    /// fixed curve needs a fixed scale: an enhanced master can top out far below 1 (the Sculptor's at 0.017), and its stars
    /// stretched on the file's own scale sat at the curve's toe and all but vanished.
    /// </summary>
    public static ScreenedStars At(Image stars, float peak, double amount) => new(stars, ScaleFor(peak), (float)MidtonesFor(amount));

    /// <summary>What a linear star sample is multiplied by on a frame whose brightest sample is <paramref name="peak"/>: one
    /// over it, or one where the peak says nothing.</summary>
    public static float ScaleFor(float peak) => peak > 0f && float.IsFinite(peak) ? 1f / peak : 1f;

    /// <summary>One linear star sample as screened: scaled, then through the curve. The shader's star screen does the same.</summary>
    public float Display(float linear)
        => float.IsNaN(linear) ? 0f : (float)Image.MidtonesTransferFunction(Midtones, linear * Scale);
}
