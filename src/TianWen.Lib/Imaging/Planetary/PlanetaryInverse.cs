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
    /// under <paramref name="minPowerFraction"/> of its power at the second ring reads zero, and every value is kept in [0,
    /// <paramref name="ceiling"/>] (1.5 for a blur; a sharpening's transfer is read with a higher one). With <paramref name="sectorDeg"/>,
    /// only the frequencies within <paramref name="sectorHalfWidthDeg"/> of that direction, either way along it, are read: an anisotropic
    /// kernel's transfer along one direction (R8 follow-up 4).
    /// </summary>
    public static RadialTransfer Measure(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, double minPowerFraction = 1e-6, double ceiling = 1.5,
        double? sectorDeg = null, double sectorHalfWidthDeg = 30)
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
                if (ring >= rings || (sectorDeg is { } sector && ring > 0 && !InSector(sx, sy, sector, sectorHalfWidthDeg)))
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
            values.Add(power[r] > floor && power[r] > 0 ? Math.Clamp(cross[r] / power[r], 0, ceiling) : 0);
        }
        return new RadialTransfer(values.MoveToImmutable(), n);
    }

    /// <summary>
    /// The transfer a perfect telescope of <paramref name="pupil"/> gives at <paramref name="wavelengthM"/> on a detector of
    /// <paramref name="arcsecPerPixel"/>: its diffraction-limited PSF (<see cref="ShortExposurePsf"/>, sampled fine enough for its
    /// cutoff as a diffracted truth is rendered) taken to its transfer, ring by ring, one at zero frequency.
    /// </summary>
    /// <remarks>
    /// <paramref name="reachPx"/> is the extent, in detector pixels, the transfer is applied over (a window's side): the PSF is computed
    /// on a grid twice that at the fine scale (<see cref="PlanetaryRender.DiffractionGridFor"/>), so the transfer's rings are fine enough
    /// near zero frequency to hold the wing that far (#1213). The 128-sample grid it was left the derived sharpening's model glow 12 to
    /// 25 % short past the limb; on a window's reach it matched a rendered truth within 2.5 % out to 3 radii. Without a reach, the
    /// 128-sample grid as before, which every mid-frequency use reads the same.
    /// </remarks>
    public static RadialTransfer Diffraction(in Pupil pupil, double wavelengthM, double arcsecPerPixel, int? reachPx = null)
    {
        var nyquistArcsec = wavelengthM / (2 * pupil.DiameterM) * ShortExposurePsf.ArcsecPerRadian;
        var factor = Math.Max(1, (int)Math.Ceiling(arcsecPerPixel / nyquistArcsec));
        var psfSize = reachPx is { } reach ? PlanetaryRender.DiffractionGridFor(reach * factor, reach * factor) : 128;
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

    /// <summary>Whether the frequency (<paramref name="sx"/>, <paramref name="sy"/>) lies within <paramref name="halfWidthDeg"/> of
    /// <paramref name="directionDeg"/> (from +x toward +y), either way along it.</summary>
    internal static bool InSector(double sx, double sy, double directionDeg, double halfWidthDeg)
    {
        var off = Math.Abs((((((Math.Atan2(sy, sx) * 180 / Math.PI) - directionDeg) % 180) + 270) % 180) - 90);
        return off <= halfWidthDeg;
    }

    /// <summary>
    /// Richardson-Lucy with the isotropic <paramref name="transfer"/> over <paramref name="iterations"/> steps, the plane lifted by
    /// <paramref name="offset"/> so its sky's noise stays positive and lowered again after: each step's estimate handed to
    /// <paramref name="each"/> (1-based step, the plane; the plane is the caller's).
    /// </summary>
    public static void RichardsonLucy(ReadOnlySpan<float> plane, int width, int height, Func<double, double> transfer, int iterations, Action<int, float[]> each, double offset = 0.1)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var n = GridFor(width, height, 32);
        RichardsonLucy(plane, width, height, KernelGrid(transfer, n), n, iterations, each, offset);
    }

    /// <summary>
    /// Richardson-Lucy as <see cref="RichardsonLucy(ReadOnlySpan{float}, int, int, Func{double, double}, int, Action{int, float[]}, double)"/>
    /// with a 2-D <paramref name="transfer"/> of (fx, fy), cycles a pixel: real and even, as an elongated kernel's is (R8 follow-up 4).
    /// </summary>
    public static void RichardsonLucy(ReadOnlySpan<float> plane, int width, int height, Func<double, double, double> transfer, int iterations, Action<int, float[]> each, double offset = 0.1)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var n = GridFor(width, height, 32);
        RichardsonLucy(plane, width, height, KernelGrid(transfer, n), n, iterations, each, offset);
    }

    private static void RichardsonLucy(ReadOnlySpan<float> plane, int width, int height, double[] kernel, int n, int iterations, Action<int, float[]> each, double offset)
    {
        ArgumentNullException.ThrowIfNull(each);
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

    /// <summary>
    /// The power a white noise puts on each Fourier coefficient of <paramref name="plane"/> (on the padded grid every filter here
    /// works on): the plane's mean power past <paramref name="fromCyclesPerPixel"/>, where a blurred planet holds no signal.
    /// </summary>
    public static double WhiteNoise(ReadOnlySpan<float> plane, int width, int height, double fromCyclesPerPixel = 0.4)
    {
        var n = GridFor(width, height, 32);
        var field = Transform(plane, width, height, n);
        double sum = 0;
        var count = 0;
        for (var i = 0; i < field.Length; i++)
        {
            if (Frequency(i, n) >= fromCyclesPerPixel)
            {
                sum += (field[i].Real * field[i].Real) + (field[i].Imaginary * field[i].Imaginary);
                count++;
            }
        }
        return count > 0 ? sum / count : 0;
    }

    /// <summary>
    /// Conan et al.'s object prior for <paramref name="plane"/>: a power law A f^-p fitted, in logs, to its ring power less
    /// <paramref name="noise"/> and over the kernel's power, ring by ring between <paramref name="from"/> and <paramref name="to"/>
    /// cycles a pixel where the power stands at least twice the noise and the transfer above a twentieth.
    /// </summary>
    public static (double Amplitude, double Exponent) PowerLawPrior(ReadOnlySpan<float> plane, int width, int height, Func<double, double> transfer, double noise,
        double from = 0.02, double to = 0.15)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var n = GridFor(width, height, 32);
        var field = Transform(plane, width, height, n);
        var rings = n;
        var (sum, count) = (new double[rings], new int[rings]);
        for (var i = 0; i < field.Length; i++)
        {
            var ring = PlanetaryCeilings.Ring(i, n);
            sum[ring] += (field[i].Real * field[i].Real) + (field[i].Imaginary * field[i].Imaginary);
            count[ring]++;
        }
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        var points = 0;
        for (var r = 1; r < rings; r++)
        {
            var f = r / (double)n;
            var h = transfer(f);
            if (f < from || f > to || count[r] == 0 || h < 0.05)
            {
                continue;
            }
            var power = sum[r] / count[r];
            if (power < 2 * noise)
            {
                continue;
            }
            var (x, y) = (Math.Log(f), Math.Log((power - noise) / (h * h)));
            (sx, sy, sxx, sxy) = (sx + x, sy + y, sxx + (x * x), sxy + (x * y));
            points++;
        }
        if (points < 3)
        {
            return (double.NaN, double.NaN);
        }
        var slope = ((points * sxy) - (sx * sy)) / ((points * sxx) - (sx * sx));
        var intercept = (sy - (slope * sx)) / points;
        return (Math.Exp(intercept), -slope);
    }

    /// <summary>
    /// A Wiener filter with the isotropic <paramref name="transfer"/> and Conan's power-law prior: H / (H^2 + scale N / (A f^-p)), the
    /// noise <paramref name="noise"/> per coefficient (<see cref="WhiteNoise"/>), the prior from <see cref="PowerLawPrior"/>.
    /// </summary>
    public static float[] WienerPowerLaw(ReadOnlySpan<float> plane, int width, int height, Func<double, double> transfer, double amplitude, double exponent,
        double noise, double scale)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var n = GridFor(width, height, 32);
        var gain = new double[n * n];
        for (var i = 0; i < gain.Length; i++)
        {
            var f = Frequency(i, n);
            var h = transfer(f);
            var prior = f > 0 ? amplitude * Math.Pow(f, -exponent) : double.PositiveInfinity;
            gain[i] = f > 0 ? h / ((h * h) + (scale * noise / prior)) : (h > 0 ? 1 / h : 0);
        }
        return Filter(plane, width, height, gain, n);
    }

    /// <summary>
    /// Restoration under an L1-L2 edge-preserving prior (Mugnier et al. 2004, MISTRAL's): the least squares to <paramref name="plane"/>
    /// through the isotropic <paramref name="transfer"/>, plus <paramref name="mu"/> times the sum over pixels of
    /// delta^2 (t / delta - ln(1 + t / delta)), t the gradient's length, quadratic below <paramref name="delta"/> and linear above, so an
    /// edge is not smoothed as a quadratic prior smooths it. Positivity at zero. Solved by lagged diffusivity: each of
    /// <paramref name="passes"/> solves the quadratic problem with weights delta / (delta + t) from the last pass by
    /// <paramref name="steps"/> steps of conjugate gradients, and clips at zero.
    /// </summary>
    public static float[] L1L2(ReadOnlySpan<float> plane, int width, int height, Func<double, double> transfer, double mu, double delta, int passes = 8, int steps = 30)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var n = GridFor(width, height, 32);
        var kernel = KernelGrid(transfer, n);
        var cells = width * height;
        var data = plane.ToArray();
        // H is real and even in frequency, so it is its own adjoint on the window: the right-hand side is H d.
        var rhs = ToDouble(Filter(data, width, height, kernel, n));
        var x = new double[cells];
        for (var i = 0; i < cells; i++)
        {
            x[i] = Math.Max(0, data[i]);
        }
        var weights = new double[cells];
        var (gx, gy) = (new double[cells], new double[cells]);

        double[] Operator(double[] v)
        {
            var hv = Filter(ToFloat(v), width, height, kernel, n);
            var hhv = Filter(hv, width, height, kernel, n);
            Gradient(v, width, height, gx, gy);
            for (var i = 0; i < cells; i++)
            {
                (gx[i], gy[i]) = (gx[i] * weights[i], gy[i] * weights[i]);
            }
            var divergence = Divergence(gx, gy, width, height);
            var result = new double[cells];
            for (var i = 0; i < cells; i++)
            {
                result[i] = hhv[i] - (mu * divergence[i]);
            }
            return result;
        }

        for (var pass = 0; pass < passes; pass++)
        {
            Gradient(x, width, height, gx, gy);
            for (var i = 0; i < cells; i++)
            {
                var t = Math.Sqrt((gx[i] * gx[i]) + (gy[i] * gy[i]));
                weights[i] = delta / (delta + t);
            }
            // Conjugate gradients from the last pass's x.
            var ax = Operator(x);
            var r = new double[cells];
            for (var i = 0; i < cells; i++)
            {
                r[i] = rhs[i] - ax[i];
            }
            var p = (double[])r.Clone();
            var rr = Dot(r, r);
            for (var step = 0; step < steps && rr > 1e-20; step++)
            {
                var ap = Operator(p);
                var alpha = rr / Math.Max(Dot(p, ap), 1e-300);
                for (var i = 0; i < cells; i++)
                {
                    x[i] += alpha * p[i];
                    r[i] -= alpha * ap[i];
                }
                var next = Dot(r, r);
                var beta = next / rr;
                rr = next;
                for (var i = 0; i < cells; i++)
                {
                    p[i] = r[i] + (beta * p[i]);
                }
            }
            for (var i = 0; i < cells; i++)
            {
                x[i] = Math.Max(0, x[i]);
            }
        }
        return ToFloat(x);
    }

    // Forward differences, zero across the far edge.
    private static void Gradient(double[] v, int width, int height, double[] gx, double[] gy)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                gx[i] = x + 1 < width ? v[i + 1] - v[i] : 0;
                gy[i] = y + 1 < height ? v[i + width] - v[i] : 0;
            }
        }
    }

    // The divergence by backward differences: minus the adjoint of Gradient.
    private static double[] Divergence(double[] gx, double[] gy, int width, int height)
    {
        var d = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var dx = (x + 1 < width ? gx[i] : 0) - (x > 0 ? gx[i - 1] : 0);
                var dy = (y + 1 < height ? gy[i] : 0) - (y > 0 ? gy[i - width] : 0);
                d[i] = dx + dy;
            }
        }
        return d;
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    private static double[] ToDouble(float[] v) => Array.ConvertAll(v, x => (double)x);

    private static float[] ToFloat(double[] v) => Array.ConvertAll(v, x => (float)x);

    // The frequency, cycles a pixel, of index i on an n-by-n grid.
    private static double Frequency(int i, int n)
    {
        var (ky, kx) = Math.DivRem(i, n);
        var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
        var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
        return Math.Sqrt((fx * fx) + (fy * fy));
    }

    /// <summary><paramref name="plane"/> through the isotropic <paramref name="transfer"/>: blurred by the kernel it describes.</summary>
    public static float[] Apply(ReadOnlySpan<float> plane, int width, int height, Func<double, double> transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var n = GridFor(width, height, 32);
        return Filter(plane, width, height, KernelGrid(transfer, n), n);
    }

    /// <summary><paramref name="plane"/> through the 2-D <paramref name="transfer"/> of (fx, fy), cycles a pixel, real and even.</summary>
    public static float[] Apply(ReadOnlySpan<float> plane, int width, int height, Func<double, double, double> transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var n = GridFor(width, height, 32);
        return Filter(plane, width, height, KernelGrid(transfer, n), n);
    }

    // A 2-D transfer on an n-by-n grid, frequency by frequency.
    private static double[] KernelGrid(Func<double, double, double> transfer, int n)
    {
        var grid = new double[n * n];
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                grid[(ky * n) + kx] = transfer((kx < n / 2 ? kx : kx - n) / (double)n, fy);
            }
        }
        return grid;
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

    internal static Complex[] Transform(ReadOnlySpan<float> plane, int width, int height, int n)
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

    internal static int GridFor(int width, int height, int margin)
    {
        var n = 1;
        while (n < Math.Max(width, height) + margin)
        {
            n <<= 1;
        }
        return n;
    }
}
