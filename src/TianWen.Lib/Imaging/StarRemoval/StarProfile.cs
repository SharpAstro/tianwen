using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>The radial family of an injected star (docs/plans/star-remover-training.md, H2's arms).</summary>
public enum StarProfileFamily
{
    /// <summary>A Moffat at the master's measured FWHM and beta: the default.</summary>
    Moffat = 0,

    /// <summary>A Gaussian at the same FWHM: the arm that asks whether the wings matter.</summary>
    Gaussian = 1,

    /// <summary>
    /// The plate builder's own profile (<see cref="StarlessFieldProfile"/>): the Moffat it subtracted every star with and
    /// its residual table, the profile each catalogued amplitude was fitted with and the one that holds a real star's halo.
    /// </summary>
    Field = 2,
}

/// <summary>
/// One star's profile, peak 1, possibly elongated, evaluated as the mean over a pixel. The radius is the elliptical one,
/// r^2 = q u^2 + v^2 / q with u along the major axis at <see cref="PositionAngleRad"/> from +x and q the axis ratio, so
/// the area of a contour, and with it the FWHM's geometric mean, is the round profile's: <see cref="FwhmPx"/> is the
/// round FWHM the master's profile fit reports, and the elongation changes the shape, not the size.
/// </summary>
/// <param name="Family">Moffat or Gaussian.</param>
/// <param name="FwhmPx">Full width at half maximum, the geometric mean of the two axes', in pixels.</param>
/// <param name="Beta">The Moffat exponent (unused for a Gaussian).</param>
/// <param name="AxisRatio">Minor over major axis, in (0, 1]; 1 is round.</param>
/// <param name="PositionAngleRad">The major axis's angle from +x, radians.</param>
/// <param name="Halo">A <see cref="StarProfileFamily.Field"/> profile's residual table, a fraction of the peak per
/// <see cref="RadialCorrection.BinWidth"/> pixels at <paramref name="HaloAlpha"/> (read at the elliptical radius times
/// <paramref name="HaloAlpha"/> over the profile's own alpha, so it scales with the width); empty for none.</param>
/// <param name="HaloAlpha">The alpha the halo table was measured at.</param>
public readonly record struct StarProfile(
    StarProfileFamily Family, double FwhmPx, double Beta, double AxisRatio, double PositionAngleRad,
    ImmutableArray<float> Halo = default, double HaloAlpha = 0.0) : IPointProfile
{
    /// <summary>A round profile.</summary>
    public static StarProfile Round(StarProfileFamily family, double fwhmPx, double beta) => new StarProfile(family, fwhmPx, beta, 1.0, 0.0);

    private double Alpha => FwhmPx / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / Beta) - 1.0));

    private double GaussSigma => FwhmPx / (2.0 * Math.Sqrt(2.0 * Math.Log(2.0)));

    /// <summary>The same profile with its FWHM scaled by <paramref name="scale"/>.</summary>
    public StarProfile Scaled(double scale) => this with { FwhmPx = FwhmPx * scale };

    /// <summary>The point value at offset (<paramref name="dx"/>, <paramref name="dy"/>) from the centre.</summary>
    public double At(double dx, double dy)
    {
        var r2 = EllipticalR2(dx, dy);
        if (Family == StarProfileFamily.Gaussian)
        {
            var s = GaussSigma;
            return Math.Exp(-r2 / (2.0 * s * s));
        }
        var a = Alpha;
        return Math.Pow(1.0 + r2 / (a * a), -Beta);
    }

    private double EllipticalR2(double dx, double dy)
    {
        var q = Math.Clamp(AxisRatio, 1e-3, 1.0);
        if (q >= 1.0)
        {
            return dx * dx + dy * dy;
        }
        var (sin, cos) = Math.SinCos(PositionAngleRad);
        var u = dx * cos + dy * sin;
        var v = -dx * sin + dy * cos;
        return q * u * u + v * v / q;
    }

    /// <summary>
    /// How far along the major axis the profile stays at or above <paramref name="fraction"/> of its peak, in pixels; 0
    /// for a fraction of 1 or more. A halo reaches as far as its table stands at the fraction or more.
    /// </summary>
    public double RadiusAtFraction(double fraction)
    {
        if (!(fraction < 1.0) || !(fraction > 0.0))
        {
            return fraction >= 1.0 ? 0.0 : double.PositiveInfinity;
        }
        var reff = Family == StarProfileFamily.Gaussian
            ? GaussSigma * Math.Sqrt(-2.0 * Math.Log(fraction))
            : Alpha * Math.Sqrt(Math.Pow(fraction, -1.0 / Beta) - 1.0);
        if (HasHalo)
        {
            var halo = Halo.AsSpan();
            for (var b = halo.Length - 1; b >= 0; b--)
            {
                if (halo[b] >= fraction)
                {
                    reff = Math.Max(reff, (b + 1) * RadialCorrection.BinWidth * Alpha / HaloAlpha);
                    break;
                }
            }
        }
        return reff / Math.Sqrt(Math.Clamp(AxisRatio, 1e-3, 1.0));
    }

    private bool HasHalo => !Halo.IsDefaultOrEmpty && HaloAlpha > 0;

    /// <summary>
    /// The mean of the profile over pixel (<paramref name="px"/>, <paramref name="py"/>) for a star centred at
    /// (<paramref name="cx"/>, <paramref name="cy"/>), pixel (i, j) covering [i - 1/2, i + 1/2]: the builder's own quadrature
    /// (<see cref="PixelQuadrature"/>, which <see cref="MoffatPsf.PixelMean"/> integrates with) at three points an axis within
    /// two FWHM of the centre (and never under 4 px), two within four (8 px), the centre beyond, so an injected star and a
    /// subtracted one are integrated alike. A halo is added at the pixel's centre, as the builder's residual table is
    /// (<see cref="RadialCorrection.Model"/>), and the sum never goes below zero.
    /// </summary>
    public double PixelMean(int px, int py, double cx, double cy)
    {
        var dx = px - cx;
        var dy = py - cy;
        var d2 = dx * dx + dy * dy;
        var near = Math.Max(4.0, 2.0 * FwhmPx);
        var far = Math.Max(8.0, 4.0 * FwhmPx);
        var sum = d2 >= far * far ? At(dx, dy) : PixelQuadrature.Mean(this, dx, dy, threePoints: d2 < near * near);
        return HasHalo
            ? Math.Max(0.0, sum + RadialCorrection.Interpolate(Halo.AsSpan(), Math.Sqrt(EllipticalR2(dx, dy)) * HaloAlpha / Alpha))
            : sum;
    }
}
