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
    /// The stars' default strength, PixInsight's ArcsinhStretch "stretch factor" at black point 0: the curve's slope at black,
    /// <c>beta / asinh(beta)</c> (<see cref="BetaFor"/>). 1000 is PixInsight's own maximum and the owner's choice on the enhanced
    /// Sculptor master against 250, 500 and 2000: the field's stars stand where they stood before the enhance (5,895 above
    /// three sigma of the sky against 5,045 before it), their halos small. The beta of 300 it replaces is a stretch of 47,
    /// which drew a median star six levels above the sky where the picture before the enhance drew it at 55, and half the
    /// field's stars vanished.
    /// </summary>
    public const float StarCurve = 1000f;

    /// <summary>
    /// The stars' default saturation, the owner's choice on the enhanced Sculptor master against 1 and 2.2: with the hue kept,
    /// the colour asinh still left its stars pale (red/green and blue/green only 1.1 to 1.4 in the plate).
    /// </summary>
    public const float StarSaturation = 1.6f;

    /// <summary>The most the Star colour dial reaches.</summary>
    public const float MaxStarSaturation = 3f;

    /// <summary>The least and the most the Stars dial reaches, PixInsight's ArcsinhStretch range: 1 is the identity.</summary>
    public const float MinStretch = 1f;

    /// <inheritdoc cref="MinStretch"/>
    public const float MaxStretch = 1000f;

    /// <summary>
    /// The asinh beta whose curve, <c>asinh(beta x) / asinh(beta)</c>, rises at black with the slope <paramref name="stretch"/>:
    /// PixInsight's stretch factor is that slope, <c>beta / asinh(beta)</c>, and not the beta (its 1000 is a beta of 9,893).
    /// The slope only grows with beta, so a bisection on its logarithm finds it; a stretch of 1 or less is the identity's limit,
    /// a beta near 0.
    /// </summary>
    public static float BetaFor(float stretch)
    {
        if (!(stretch > 1f))
        {
            return 1e-3f;
        }
        double lo = 1e-3, hi = 1e9;
        for (var i = 0; i < 100; i++)
        {
            var mid = System.Math.Sqrt(lo * hi);
            if (mid / System.Math.Asinh(mid) < stretch)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return (float)System.Math.Sqrt(lo * hi);
    }

    /// <summary>The least and the most the Star Stretch's amount dial reaches, SETI's slider range.</summary>
    public const float MinAmount = 0f;

    /// <inheritdoc cref="MinAmount"/>
    public const float MaxAmount = 8f;

    /// <summary>The stars plate as a CPU render screens it at an arcsinh <paramref name="stretch"/> (<see cref="BetaFor"/>)
    /// and a <paramref name="saturation"/>.</summary>
    public ScreenedStars ScreenAt(float stretch, float saturation) => new(Stars, BetaFor(stretch), saturation);

    /// <summary>The stars plate as a CPU render screens it for a <paramref name="rendition"/>: its method, its stretch or
    /// amount, its saturation. The one place a rendition's star dials become a screen, for every CPU render.</summary>
    public ScreenedStars ScreenFor(in DisplayRendition rendition)
        => new(Stars, BetaFor(rendition.StarStretch), rendition.StarSaturation, rendition.StarMethod, rendition.StarAmount);
}
