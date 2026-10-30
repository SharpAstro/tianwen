using System;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// A star's outer halo (#1870): a second, wide, faint Moffat term added under the profile, <c>Fraction * (1 + r^2 / alpha^2)^-beta</c>
/// in units of the star's peak, so it scales with the star's amplitude as the profile does. Round (a halo this wide has lost
/// the core's elongation) and fixed in pixels (it is a property of the rig's optics and the sky, not of the frame's seeing).
/// <c>default</c> is none, so every profile drawn without one is unchanged to the bit. Fitted per rig by
/// <see cref="OuterHaloFit"/>; the one reference scale is the master's unit scale, where a clipped star's peak is about 1.
/// </summary>
/// <param name="Fraction">The term's value at the centre, a fraction of the star's peak.</param>
/// <param name="AlphaPx">The Moffat scale radius in pixels.</param>
/// <param name="Beta">The Moffat exponent; low, so the tail is long.</param>
public readonly record struct OuterHalo(double Fraction, double AlphaPx, double Beta) : IPointProfile
{
    /// <summary>The nearest a pixel centre may lie to the star's before the mean is a quadrature, not the centre value.</summary>
    private const double CentreOnlyBeyondPx = 8.0;

    /// <summary>True for <c>default</c> and for any term that draws nothing.</summary>
    public bool IsNone => !(Fraction > 0.0 && AlphaPx > 0.0 && Beta > 0.0);

    /// <summary>The point value at offset (<paramref name="dx"/>, <paramref name="dy"/>); 0 for none.</summary>
    public double At(double dx, double dy)
        => IsNone ? 0.0 : Fraction * Math.Pow(1.0 + (((dx * dx) + (dy * dy)) / (AlphaPx * AlphaPx)), -Beta);

    /// <summary>The value at radius <paramref name="r"/>.</summary>
    public double AtRadius(double r) => At(r, 0.0);

    /// <summary>
    /// The mean over the pixel whose centre lies at (<paramref name="dx"/>, <paramref name="dy"/>) from the star's, through
    /// <see cref="PixelQuadrature"/> at two points an axis near the centre and the centre value beyond
    /// <see cref="CentreOnlyBeyondPx"/>, where a term this smooth is flat across a pixel.
    /// </summary>
    public double PixelMean(double dx, double dy)
    {
        if (IsNone)
        {
            return 0.0;
        }
        return (dx * dx) + (dy * dy) >= CentreOnlyBeyondPx * CentreOnlyBeyondPx ? At(dx, dy) : PixelQuadrature.Mean(this, dx, dy, threePoints: false);
    }

    /// <summary>How far out the term stays at or above <paramref name="fraction"/> of the peak, pixels; 0 for none or when it never reaches it.</summary>
    public double RadiusAtFraction(double fraction)
    {
        if (IsNone || !(fraction > 0.0) || fraction >= Fraction)
        {
            return 0.0;
        }
        return AlphaPx * Math.Sqrt(Math.Pow(fraction / Fraction, -1.0 / Beta) - 1.0);
    }
}
