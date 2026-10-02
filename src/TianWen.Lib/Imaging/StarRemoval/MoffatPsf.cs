using System;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// A circular Moffat profile, <c>(1 + (r/alpha)^2)^-beta</c> with a peak of 1, evaluated as the mean over a
/// pixel rather than at its centre. A star of 1.5 px FWHM sampled at pixel centres is a different star from
/// the one the sensor recorded (a sub-2 px kernel does not blur by its label), so the core is supersampled:
/// 4x4 within 2 px of the centre, 2x2 within 4 px, a single sample beyond, where the profile is smooth across
/// a pixel. Pixel (i, j) covers [i - 0.5, i + 0.5] x [j - 0.5, j + 0.5], the centroid convention of
/// <see cref="ImagedStar"/>.
/// </summary>
internal readonly record struct MoffatPsf(double Alpha, double Beta)
{
    /// <summary>The alpha that gives a profile of <paramref name="fwhm"/> pixels at <paramref name="beta"/>.</summary>
    public static double AlphaFor(double fwhm, double beta) => fwhm / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / beta) - 1.0));

    /// <summary>The profile's full width at half maximum, in pixels.</summary>
    public double Fwhm => 2.0 * Alpha * Math.Sqrt(Math.Pow(2.0, 1.0 / Beta) - 1.0);

    /// <summary>The same profile with alpha scaled by <paramref name="scale"/> (the FWHM scales with it).</summary>
    public MoffatPsf Scaled(double scale) => new MoffatPsf(Alpha * scale, Beta);

    /// <summary>The point value at radius squared <paramref name="r2"/>.</summary>
    public double At(double r2) => Math.Pow(1.0 + r2 / (Alpha * Alpha), -Beta);

    /// <summary>
    /// The radius at which a star of peak <paramref name="amplitude"/> falls to <paramref name="level"/>, both
    /// in the plane's units; zero when the star never reaches the level.
    /// </summary>
    public double RadiusAtLevel(double amplitude, double level)
    {
        if (!(amplitude > level) || !(level > 0))
        {
            return 0.0;
        }
        return Alpha * Math.Sqrt(Math.Pow(amplitude / level, 1.0 / Beta) - 1.0);
    }

    // Gauss-Legendre nodes on [-1/2, 1/2] with weights summing to one: three points, then two.
    private static readonly double[] Nodes3 = { -0.5 * Math.Sqrt(0.6), 0.0, 0.5 * Math.Sqrt(0.6) };
    private static readonly double[] Weights3 = { 5.0 / 18.0, 8.0 / 18.0, 5.0 / 18.0 };
    private static readonly double[] Nodes2 = { -0.5 / Math.Sqrt(3.0), 0.5 / Math.Sqrt(3.0) };
    private static readonly double[] Weights2 = { 0.5, 0.5 };

    /// <summary>The mean of the profile over pixel (<paramref name="px"/>, <paramref name="py"/>) for a star centred at
    /// (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    /// <remarks>Gauss-Legendre per axis, three points within 4 px of the centre, two within 8, the centre beyond:
    /// measured against a 64x64 midpoint reference on a beta-3 Moffat of 1.5 and 3 px FWHM, it errs by about 1e-5 of the
    /// peak everywhere, where 4x4 midpoint sampling erred by up to 7e-3. A bright star needs that: an 800-sigma star
    /// modelled 0.1 percent wrong leaves 0.8 sigma behind.</remarks>
    public double PixelMean(int px, int py, double cx, double cy)
    {
        var dx = px - cx;
        var dy = py - cy;
        var r2 = dx * dx + dy * dy;
        if (r2 >= 64.0)
        {
            return At(r2);
        }
        var (nodes, weights) = r2 < 16.0 ? (Nodes3, Weights3) : (Nodes2, Weights2);
        var sum = 0.0;
        for (var j = 0; j < nodes.Length; j++)
        {
            var oy = dy + nodes[j];
            for (var i = 0; i < nodes.Length; i++)
            {
                var ox = dx + nodes[i];
                sum += weights[i] * weights[j] * At(ox * ox + oy * oy);
            }
        }
        return sum;
    }
}
