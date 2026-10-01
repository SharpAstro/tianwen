using System;
using System.Collections.Immutable;
using System.Numerics;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// An isotropic transfer read ring by ring, in cycles a pixel (docs/plans/planetary-restoration.md, R7 part 4): its value between two
/// rings is interpolated, and past the last one it is zero.
/// </summary>
/// <param name="Values">The transfer at ring r, r / <paramref name="RingsPerCycle"/> cycles a pixel.</param>
/// <param name="RingsPerCycle">How many rings a cycle a pixel spans.</param>
public sealed record RadialTransfer(ImmutableArray<double> Values, double RingsPerCycle)
{
    /// <summary>The transfer at <paramref name="cyclesPerPixel"/>.</summary>
    public double At(double cyclesPerPixel)
    {
        var r = cyclesPerPixel * RingsPerCycle;
        var i = (int)Math.Floor(r);
        if (i >= Values.Length - 1)
        {
            return 0;
        }
        var t = r - i;
        return (Values[i] * (1 - t)) + (Values[i + 1] * t);
    }
}

/// <summary>
/// R7's inverse (docs/plans/planetary-restoration.md, R7 part 4): a stack's transfer read against a truth (the oracle's kernel), the
/// pupil's own diffraction transfer (which a limb kernel, the TOTAL blur, is divided by to restore toward the diffraction limit), and
/// Richardson-Lucy and Wiener with an isotropic transfer, in the Fourier domain on a grid padded to a power of two.
/// </summary>
public static class PlanetaryInverse
{
    /// <summary>
    /// <paramref name="stack"/>'s transfer against <paramref name="truth"/> ring by ring (both normalised, sky zero, registered): the
    /// real part of their cross spectrum over the truth's power, which the stack's noise leaves unbiased. A ring where the truth holds
    /// under <paramref name="minPowerFraction"/> of its power at the second ring reads zero, and every value is kept in [0, 1.5].
    /// </summary>
    public static RadialTransfer Measure(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, double minPowerFraction = 1e-6)
    {
        var n = GridFor(width, height, 0);
        var s = Transform(stack, width, height, n);
        var t = Transform(truth, width, height, n);
        var rings = (n / 2) + 1;
        var (cross, power) = (new double[rings], new double[rings]);
        for (var ky = 0; ky < n; ky++)
        {
            var sy = ky < n / 2 ? ky : ky - n;
            for (var kx = 0; kx < n; kx++)
            {
                var sx = kx < n / 2 ? kx : kx - n;
                var ring = (int)Math.Round(Math.Sqrt((sx * sx) + (sy * sy)));
                if (ring >= rings)
                {
                    continue;
                }
                var (a, b) = (s[(ky * n) + kx], t[(ky * n) + kx]);
                cross[ring] += (a.Real * b.Real) + (a.Imaginary * b.Imaginary);
                power[ring] += (b.Real * b.Real) + (b.Imaginary * b.Imaginary);
            }
        }
        var floor = minPowerFraction * power[Math.Min(2, rings - 1)];
        var values = ImmutableArray.CreateBuilder<double>(rings);
        for (var r = 0; r < rings; r++)
        {
            values.Add(power[r] > floor && power[r] > 0 ? Math.Clamp(cross[r] / power[r], 0, 1.5) : 0);
        }
        return new RadialTransfer(values.MoveToImmutable(), n);
    }

    /// <summary>
    /// The transfer a perfect telescope of <paramref name="pupil"/> gives at <paramref name="wavelengthM"/> on a detector of
    /// <paramref name="arcsecPerPixel"/>: its diffraction-limited PSF (<see cref="ShortExposurePsf"/>, sampled fine enough for its
    /// cutoff as a diffracted truth is rendered) taken to its transfer, ring by ring, one at zero frequency.
    /// </summary>
    public static RadialTransfer Diffraction(in Pupil pupil, double wavelengthM, double arcsecPerPixel)
    {
        const int psfSize = 128;
        var nyquistArcsec = wavelengthM / (2 * pupil.DiameterM) * ShortExposurePsf.ArcsecPerRadian;
        var factor = Math.Max(1, (int)Math.Ceiling(arcsecPerPixel / nyquistArcsec));
        var transmission = pupil.Rasterise(psfSize, ShortExposurePsf.PupilSpacingFor(wavelengthM, arcsecPerPixel / factor, psfSize));
        var psf = new double[psfSize * psfSize];
        ShortExposurePsf.Compute(transmission, [], psfSize, psf);
        var field = new Complex[psfSize * psfSize];
        for (var i = 0; i < psf.Length; i++)
        {
            field[i] = psf[i];
        }
        Fft2D.Forward(field, psfSize, psfSize);
        var rings = (psfSize / 2) + 1;
        var (sum, count) = (new double[rings], new int[rings]);
        for (var ky = 0; ky < psfSize; ky++)
        {
            var sy = ky < psfSize / 2 ? ky : ky - psfSize;
            for (var kx = 0; kx < psfSize; kx++)
            {
                var sx = kx < psfSize / 2 ? kx : kx - psfSize;
                var ring = (int)Math.Round(Math.Sqrt((sx * sx) + (sy * sy)));
                if (ring < rings)
                {
                    sum[ring] += field[(ky * psfSize) + kx].Magnitude;
                    count[ring]++;
                }
            }
        }
        var zero = sum[0] / Math.Max(1, count[0]);
        var values = ImmutableArray.CreateBuilder<double>(rings);
        for (var r = 0; r < rings; r++)
        {
            values.Add(count[r] > 0 && zero > 0 ? sum[r] / count[r] / zero : 0);
        }
        // A fine ring is factor / psfSize cycles a detector pixel.
        return new RadialTransfer(values.MoveToImmutable(), psfSize / (double)factor);
    }

    /// <summary>
    /// Richardson-Lucy with the isotropic <paramref name="transfer"/> over <paramref name="iterations"/> steps, the plane lifted by
    /// <paramref name="offset"/> so its sky's noise stays positive and lowered again after: each step's estimate handed to
    /// <paramref name="each"/> (1-based step, the plane; the plane is the caller's).
    /// </summary>
    public static void RichardsonLucy(ReadOnlySpan<float> plane, int width, int height, Func<double, double> transfer, int iterations, Action<int, float[]> each, double offset = 0.1)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(each);
        var n = GridFor(width, height, 32);
        var kernel = KernelGrid(transfer, n);
        var observed = new double[width * height];
        var estimate = new double[width * height];
        for (var i = 0; i < observed.Length; i++)
        {
            observed[i] = Math.Max(plane[i] + offset, 1e-6);
            estimate[i] = observed[i];
        }
        var ratio = new float[width * height];
        var current = new float[width * height];
        for (var step = 1; step <= iterations; step++)
        {
            for (var i = 0; i < current.Length; i++)
            {
                current[i] = (float)estimate[i];
            }
            var blurred = Filter(current, width, height, kernel, n);
            for (var i = 0; i < ratio.Length; i++)
            {
                ratio[i] = (float)(observed[i] / Math.Max(blurred[i], 1e-6));
            }
            var correction = Filter(ratio, width, height, kernel, n);
            var result = new float[width * height];
            for (var i = 0; i < estimate.Length; i++)
            {
                estimate[i] = Math.Max(estimate[i] * correction[i], 1e-9);
                result[i] = (float)(estimate[i] - offset);
            }
            each(step, result);
        }
    }

    /// <summary>
    /// A Wiener filter with the isotropic <paramref name="transfer"/> and a flat noise-to-signal <paramref name="lambda"/>:
    /// H / (H^2 + lambda) in every frequency.
    /// </summary>
    public static float[] Wiener(ReadOnlySpan<float> plane, int width, int height, Func<double, double> transfer, double lambda)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var n = GridFor(width, height, 32);
        var gain = new double[n * n];
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
                var h = transfer(Math.Sqrt((fx * fx) + (fy * fy)));
                gain[(ky * n) + kx] = h / ((h * h) + lambda);
            }
        }
        return Filter(plane, width, height, gain, n);
    }

    /// <summary><paramref name="plane"/> through the isotropic <paramref name="transfer"/>: blurred by the kernel it describes.</summary>
    public static float[] Apply(ReadOnlySpan<float> plane, int width, int height, Func<double, double> transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var n = GridFor(width, height, 32);
        return Filter(plane, width, height, KernelGrid(transfer, n), n);
    }

    // The transfer on an n-by-n grid, frequency by frequency.
    private static double[] KernelGrid(Func<double, double> transfer, int n)
    {
        var grid = new double[n * n];
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
                grid[(ky * n) + kx] = transfer(Math.Sqrt((fx * fx) + (fy * fy)));
            }
        }
        return grid;
    }

    // A plane multiplied in frequency by a real grid, on the padded grid, cropped back.
    private static float[] Filter(ReadOnlySpan<float> plane, int width, int height, double[] grid, int n)
    {
        var field = Transform(plane, width, height, n);
        for (var i = 0; i < field.Length; i++)
        {
            field[i] *= grid[i];
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

    private static Complex[] Transform(ReadOnlySpan<float> plane, int width, int height, int n)
    {
        var field = new Complex[n * n];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                field[(y * n) + x] = plane[(y * width) + x];
            }
        }
        Fft2D.Forward(field, n, n);
        return field;
    }

    private static int GridFor(int width, int height, int margin)
    {
        var n = 1;
        while (n < Math.Max(width, height) + margin)
        {
            n <<= 1;
        }
        return n;
    }
}
