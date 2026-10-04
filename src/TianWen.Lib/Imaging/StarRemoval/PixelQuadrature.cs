using System;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>A profile's point value at an offset from its centre, which <see cref="PixelQuadrature"/> integrates over a pixel.</summary>
internal interface IPointProfile
{
    /// <summary>The value at offset (<paramref name="dx"/>, <paramref name="dy"/>) from the centre, peak 1.</summary>
    double At(double dx, double dy);
}

/// <summary>
/// The one pixel integration of a star's profile, the plate builder's (<see cref="MoffatPsf"/>) and the injector's
/// (<see cref="StarProfile"/>): Gauss-Legendre per axis on [-1/2, 1/2], three points or two, weights summing to one, summed
/// row by row in a fixed order so a subtracted star and an injected one are integrated alike to the bit. Each caller keeps
/// its own choice of how many points at what distance.
/// </summary>
internal static class PixelQuadrature
{
    private static readonly double[] Nodes3 = { -0.5 * Math.Sqrt(0.6), 0.0, 0.5 * Math.Sqrt(0.6) };
    private static readonly double[] Weights3 = { 5.0 / 18.0, 8.0 / 18.0, 5.0 / 18.0 };
    private static readonly double[] Nodes2 = { -0.5 / Math.Sqrt(3.0), 0.5 / Math.Sqrt(3.0) };
    private static readonly double[] Weights2 = { 0.5, 0.5 };

    /// <summary>
    /// The mean of <paramref name="profile"/> over the pixel whose centre lies at (<paramref name="dx"/>, <paramref name="dy"/>)
    /// from the star's, at three points an axis (<paramref name="threePoints"/>) or two.
    /// </summary>
    public static double Mean<T>(in T profile, double dx, double dy, bool threePoints) where T : struct, IPointProfile
    {
        var (nodes, weights) = threePoints ? (Nodes3, Weights3) : (Nodes2, Weights2);
        var sum = 0.0;
        for (var j = 0; j < nodes.Length; j++)
        {
            var oy = dy + nodes[j];
            for (var i = 0; i < nodes.Length; i++)
            {
                sum += weights[i] * weights[j] * profile.At(dx + nodes[i], oy);
            }
        }
        return sum;
    }
}
