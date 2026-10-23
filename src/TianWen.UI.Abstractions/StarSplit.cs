using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// An enhance's star split as the viewer composes it: the LINEAR starless plate, shown through every display lever, and the
/// LINEAR stars plate, stretched by its own colour asinh as it is drawn (<see cref="ScreenedStars"/>, the shader's star screen)
/// and screened over the result (<see cref="Image.ScreenStars"/>). The stars take no display lever, so the stretch, the boost
/// and the finish cannot lift their wings; only their own strength (<see cref="ViewerState.StarStretch"/>) moves them.
/// </summary>
/// <param name="Starless">The starless plate, linear, on the document's scale.</param>
/// <param name="Stars">The stars plate, linear, on the document's scale.</param>
public sealed record StarSplit(Image Starless, Image Stars)
{
    /// <summary>
    /// The stars' default strength, an asinh beta: the CLI's <c>--dual-stretch --star-stretch-mode asinh --asinh-beta 300</c>,
    /// chosen by eye on the enhanced Sculptor master against StarStretch (which whitened every core) and betas 100 and 1000.
    /// </summary>
    public const float StarCurve = 300f;

    /// <summary>
    /// The stars' default saturation, the owner's choice on the enhanced Sculptor master against 1 and 2.2: with the hue kept,
    /// the colour asinh still left its stars pale (red/green and blue/green only 1.1 to 1.4 in the plate).
    /// </summary>
    public const float StarSaturation = 1.6f;

    /// <summary>The most the Star colour dial reaches.</summary>
    public const float MaxStarSaturation = 3f;

    /// <summary>The least and the most the Stars dial reaches, the CLI's <c>--asinh-beta</c> range.</summary>
    public const float MinBeta = 1f;

    /// <inheritdoc cref="MinBeta"/>
    public const float MaxBeta = 1000f;

    /// <summary>The stars plate as a CPU render screens it at an asinh <paramref name="beta"/> and a
    /// <paramref name="saturation"/>.</summary>
    public ScreenedStars ScreenAt(float beta, float saturation) => new(Stars, beta, saturation);
}
