using System;
using System.Collections.Generic;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A filter read as a kernel (docs/plans/planetary-restoration.md, R8 follow-up 1): the shift-invariant kernel that takes one plane to
/// another by least squares (another program's sharpening of its own stack, say), applied by correlation, its transfer, and the share
/// of a composite kernel below zero. A non-negative unit-sum kernel has a transfer of at most one at every frequency, so a filter that
/// lifts any frequency past the truth has a negative lobe, which over a bright disk on a dark sky is a ring.
/// </summary>
public static class PlanetaryKernelFit
{
    /// <summary>
    /// The kernel of (2 <paramref name="radius"/> + 1) squared taps, row-major and centred, and a constant, that bring
    /// <paramref name="source"/> nearest <paramref name="target"/> by least squares over <paramref name="pixels"/> (row-major indices whose
    /// whole kernel lies inside the plane; every such pixel when null): target(x, y) = constant + the sum over (u, v) of
    /// kernel(u, v) source(x + u, y + v).
    /// </summary>
    public static (double[] Kernel, double Constant) Fit(ReadOnlySpan<float> source, ReadOnlySpan<float> target, int width, int height, int radius,
        IReadOnlyList<int>? pixels = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        var side = (2 * radius) + 1;
        var taps = side * side;
        var unknowns = taps + 1;
        var a = new double[unknowns, unknowns];
        var b = new double[unknowns];
        var row = new double[unknowns];
        foreach (var i in pixels ?? Interior(width, height, radius))
        {
            var (y, x) = Math.DivRem(i, width);
            if (x < radius || y < radius || x >= width - radius || y >= height - radius)
            {
                continue;
            }
            var t = 0;
            for (var v = -radius; v <= radius; v++)
            {
                var start = ((y + v) * width) + x - radius;
                for (var u = 0; u < side; u++)
                {
                    row[t++] = source[start + u];
                }
            }
            row[taps] = 1;
            double value = target[i];
            for (var j = 0; j < unknowns; j++)
            {
                var rj = row[j];
                b[j] += rj * value;
                for (var k = j; k < unknowns; k++)
                {
                    a[j, k] += rj * row[k];
                }
            }
        }
        for (var j = 0; j < unknowns; j++)
        {
            for (var k = 0; k < j; k++)
            {
                a[j, k] = a[k, j];
            }
        }
        var solved = PlanetaryCeilings.SolveInPlace(a, b);
        return (solved[..taps], solved[taps]);
    }

    /// <summary><paramref name="plane"/> through <paramref name="kernel"/> and <paramref name="constant"/> as <see cref="Fit"/> defines them; the edge the kernel cannot reach is copied.</summary>
    public static float[] Apply(ReadOnlySpan<float> plane, int width, int height, ReadOnlySpan<double> kernel, int radius, double constant = 0)
    {
        var side = (2 * radius) + 1;
        var result = plane.ToArray();
        for (var y = radius; y < height - radius; y++)
        {
            for (var x = radius; x < width - radius; x++)
            {
                var sum = constant;
                var t = 0;
                for (var v = -radius; v <= radius; v++)
                {
                    var start = ((y + v) * width) + x - radius;
                    for (var u = 0; u < side; u++)
                    {
                        sum += kernel[t++] * plane[start + u];
                    }
                }
                result[(y * width) + x] = (float)sum;
            }
        }
        return result;
    }

    /// <summary><paramref name="kernel"/>'s transfer at (<paramref name="fx"/>, <paramref name="fy"/>) cycles a pixel of its own grid, under <see cref="Apply"/>'s correlation.</summary>
    public static Complex TransferAt(ReadOnlySpan<double> kernel, int radius, double fx, double fy)
    {
        var sum = Complex.Zero;
        var t = 0;
        for (var v = -radius; v <= radius; v++)
        {
            for (var u = -radius; u <= radius; u++)
            {
                sum += kernel[t++] * Complex.FromPolarCoordinates(1, 2 * Math.PI * ((fx * u) + (fy * v)));
            }
        }
        return sum;
    }

    /// <summary>
    /// <paramref name="plane"/> through the transfer <paramref name="filter"/> (cycles a pixel in x and y), in the Fourier domain on the
    /// padded grid the inverses use, cropped back.
    /// </summary>
    public static float[] Filter(ReadOnlySpan<float> plane, int width, int height, Func<double, double, Complex> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var n = PlanetaryInverse.GridFor(width, height, 32);
        var field = PlanetaryInverse.Transform(plane, width, height, n);
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
                field[(ky * n) + kx] *= filter(fx, fy);
            }
        }
        Fft2D.Inverse(field, n, n);
        var result = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result[(y * width) + x] = (float)field[(y * n) + x].Real;
            }
        }
        return result;
    }

    /// <summary>
    /// The share of a composite kernel's sum that lies below zero within <paramref name="reach"/> pixels of its centre, its transfer
    /// <paramref name="filter"/> times the isotropic <paramref name="blur"/>, taken to the image on an <paramref name="n"/> grid: zero
    /// for a kernel that cannot ring. The reach keeps it to the ring a limb shows (R3 reads 1.0 to 1.3 radii, about 15 px): summed over
    /// the whole grid, the faint ripple a hard cut in a transfer leaves far out grows with the area and swamps it.
    /// </summary>
    public static double NegativeMass(Func<double, double, Complex> filter, Func<double, double> blur, int reach = 15, int n = 256)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(blur);
        var field = new Complex[n * n];
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
                field[(ky * n) + kx] = filter(fx, fy) * blur(Math.Sqrt((fx * fx) + (fy * fy)));
            }
        }
        Fft2D.Inverse(field, n, n);
        double negative = 0, sum = 0;
        for (var y = 0; y < n; y++)
        {
            var dy = y < n / 2 ? y : y - n;
            for (var x = 0; x < n; x++)
            {
                var dx = x < n / 2 ? x : x - n;
                var c = field[(y * n) + x].Real;
                sum += c;
                if (c < 0 && (dx * dx) + (dy * dy) <= reach * reach)
                {
                    negative -= c;
                }
            }
        }
        return sum > 0 ? negative / sum : double.NaN;
    }

    /// <summary>The a trous filter of <paramref name="gains"/> (finest first, the residual at 1) at (fx, fy) cycles a pixel.</summary>
    public static double WaveletTransfer(ReadOnlySpan<double> gains, double fx, double fy)
    {
        var g = PlanetaryWaveletGains.Scaling(gains.Length, fx, fy);
        var previous = 1.0;
        for (var j = 0; j < gains.Length; j++)
        {
            var next = PlanetaryWaveletGains.Scaling(j + 1, fx, fy);
            g += gains[j] * (previous - next);
            previous = next;
        }
        return g;
    }

    private static IEnumerable<int> Interior(int width, int height, int radius)
    {
        for (var y = radius; y < height - radius; y++)
        {
            for (var x = radius; x < width - radius; x++)
            {
                yield return (y * width) + x;
            }
        }
    }
}
