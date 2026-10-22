using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// An enhance's star split as the viewer composes it: the LINEAR starless plate, shown through every display lever, and the
/// stars plate STRETCHED once by <see cref="StarCurve"/>, screened over the result (<see cref="Image.ScreenStars"/>, the
/// shader's star screen). The stars take no display lever, so the stretch, the boost and the finish cannot lift their wings.
/// </summary>
/// <param name="Starless">The starless plate, linear, on the document's scale.</param>
/// <param name="Stars">The stars plate, stretched to display values in [0, 1].</param>
public sealed record StarSplit(Image Starless, Image Stars)
{
    /// <summary>
    /// The stars' fixed curve: Frank Sackenheim's StarStretch at the CLI's <c>--dual-stretch</c> default amount, so the viewer
    /// and the command line show one picture.
    /// </summary>
    public const double StarCurve = 2.0;

    /// <summary>
    /// The split from an enhance's linear plates: the starless as it is, the stars through <see cref="StarCurve"/> on the
    /// FRAME'S OWN full scale, <paramref name="peak"/> (the recombined frame's brightest sample). A fixed curve means a fixed
    /// scale: an enhanced master can top out far below 1 (the Sculptor's at 0.017, its balance and the deblur's ceiling
    /// applied), and its stars stretched as they were sat at the curve's toe and all but vanished.
    /// </summary>
    public static StarSplit FromLinear(Image starless, Image stars, float peak)
        => new(starless, (peak > 0f && float.IsFinite(peak) ? stars.Affine(1.0 / peak, 0.0) : stars).StarStretch(StarCurve));
}
