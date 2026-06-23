using System;
using System.Numerics;

namespace TianWen.Lib.Stat;

/// <summary>
/// Sub-pixel image registration by phase correlation. The normalised cross-power spectrum of two tiles
/// inverse-transforms to a sharp correlation peak whose offset is the translation between them; a
/// parabolic fit around the peak refines it to sub-pixel. Translation-only (rotation/scale are handled
/// upstream by the disk centroid + the AP mesh); both tiles must share the same power-of-two dimensions
/// (the caller crops / zero-pads a tile around the disk). This is the global-bootstrap + per-AP matcher
/// of the planetary aligner, kept in <c>Stat/</c> as a general primitive.
/// </summary>
public static class PhaseCorrelation
{
    /// <summary>
    /// The displacement of <c>moving</c> relative to <c>reference</c>: <c>moving(x, y) ~=
    /// reference(x - Dx, y - Dy)</c>. To register <c>moving</c> onto <c>reference</c>, translate it by
    /// <c>(-Dx, -Dy)</c>. <see cref="PeakValue"/> is the correlation peak height (a confidence proxy in
    /// [0, ~1] for the normalised spectrum).
    /// </summary>
    public readonly record struct Shift(double Dx, double Dy, double PeakValue);

    /// <summary>
    /// Estimates the translation between two equal-size, power-of-two tiles by phase correlation.
    /// </summary>
    /// <param name="reference">Reference tile, row-major, length <c>width*height</c>.</param>
    /// <param name="moving">Moving tile, same dimensions.</param>
    /// <param name="width">Tile width (power of two).</param>
    /// <param name="height">Tile height (power of two).</param>
    /// <param name="applyWindow">
    /// Apply a 2D Hann window before transforming, to suppress edge-discontinuity spectral leakage on
    /// real (non-periodic) imagery. Default true. Pass false when the shift is a genuine circular shift
    /// (a window would break the circular-shift relationship and bias the peak).
    /// </param>
    public static Shift Estimate(ReadOnlySpan<float> reference, ReadOnlySpan<float> moving, int width, int height, bool applyWindow = true)
    {
        if (!ComplexFft.IsPowerOfTwo(width) || !ComplexFft.IsPowerOfTwo(height))
        {
            throw new ArgumentException($"Phase correlation tile dimensions must each be a power of two, got {width}x{height}.");
        }

        var n = width * height;
        if (reference.Length != n || moving.Length != n)
        {
            throw new ArgumentException($"reference/moving must both be {n} samples ({width}x{height}).");
        }

        var f1 = new Complex[n];
        var f2 = new Complex[n];

        if (applyWindow)
        {
            // Separable Hann window, precomputed per axis.
            var wx = HannWindow(width);
            var wy = HannWindow(height);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width) + x;
                    var w = wx[x] * wy[y];
                    f1[i] = new Complex(reference[i] * w, 0);
                    f2[i] = new Complex(moving[i] * w, 0);
                }
            }
        }
        else
        {
            for (var i = 0; i < n; i++)
            {
                f1[i] = new Complex(reference[i], 0);
                f2[i] = new Complex(moving[i], 0);
            }
        }

        Fft2D.Forward(f1, width, height);
        Fft2D.Forward(f2, width, height);

        // Normalised cross-power spectrum R = F1 * conj(F2) / |F1 * conj(F2)|.
        for (var i = 0; i < n; i++)
        {
            var c = f1[i] * Complex.Conjugate(f2[i]);
            var mag = c.Magnitude;
            f1[i] = mag > 1e-12 ? c / mag : Complex.Zero;
        }

        Fft2D.Inverse(f1, width, height);

        // Integer peak over the real correlation surface.
        var peakIndex = 0;
        var peak = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            var v = f1[i].Real;
            if (v > peak)
            {
                peak = v;
                peakIndex = i;
            }
        }

        var px = peakIndex % width;
        var py = peakIndex / width;

        // Sub-pixel parabolic refinement with circular neighbours.
        var subX = ParabolicOffset(
            f1[(py * width) + Wrap(px - 1, width)].Real,
            f1[(py * width) + px].Real,
            f1[(py * width) + Wrap(px + 1, width)].Real);
        var subY = ParabolicOffset(
            f1[(Wrap(py - 1, height) * width) + px].Real,
            f1[(py * width) + px].Real,
            f1[(Wrap(py + 1, height) * width) + px].Real);

        // The correlation peak sits at -shift (mod N); wrap each index into [-N/2, N/2] then negate.
        var signedX = px <= width / 2 ? px : px - width;
        var signedY = py <= height / 2 ? py : py - height;
        var dx = -(signedX + subX);
        var dy = -(signedY + subY);

        return new Shift(dx, dy, peak);
    }

    private static double[] HannWindow(int length)
    {
        var w = new double[length];
        if (length == 1)
        {
            w[0] = 1;
            return w;
        }

        for (var i = 0; i < length; i++)
        {
            w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (length - 1)));
        }

        return w;
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;

    /// <summary>Vertex offset in [-0.5, 0.5] of the parabola through (-1, vm), (0, v0), (+1, vp).</summary>
    private static double ParabolicOffset(double vm, double v0, double vp)
    {
        var denom = vm - (2.0 * v0) + vp;
        if (Math.Abs(denom) < 1e-12)
        {
            return 0;
        }

        var offset = 0.5 * (vm - vp) / denom;
        return Math.Clamp(offset, -1.0, 1.0);
    }
}
