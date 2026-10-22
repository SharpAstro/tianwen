using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// An enhance's star split as the viewer composes it: the LINEAR starless plate, shown through every display lever, and the
/// LINEAR stars plate, stretched by its own curve as it is drawn (<see cref="ScreenedStars"/>, the shader's star screen) and
/// screened over the result (<see cref="Image.ScreenStars"/>). The stars take no display lever, so the stretch, the boost and
/// the finish cannot lift their wings; only their own strength (<see cref="ViewerState.StarStretch"/>) moves them.
/// </summary>
/// <param name="Starless">The starless plate, linear, on the document's scale.</param>
/// <param name="Stars">The stars plate, linear, on the document's scale.</param>
/// <param name="Peak">The recombined frame's brightest sample: the stars' curve runs on the frame's own full scale, one over it
/// (<see cref="ScreenedStars.ScaleFor"/>).</param>
public sealed record StarSplit(Image Starless, Image Stars, float Peak)
{
    /// <summary>
    /// The stars' default strength: Frank Sackenheim's StarStretch at the CLI's <c>--dual-stretch</c> default amount, so the
    /// viewer and the command line show one picture until the Tone popover's Stars dial moves it.
    /// </summary>
    public const float StarCurve = 2.0f;

    /// <summary>What a linear star sample is multiplied by before the curve (<see cref="ScreenedStars.ScaleFor"/>).</summary>
    public float Scale => ScreenedStars.ScaleFor(Peak);

    /// <summary>The stars plate as a CPU render screens it at a StarStretch <paramref name="amount"/>.</summary>
    public ScreenedStars ScreenAt(double amount) => ScreenedStars.At(Stars, Peak, amount);
}
