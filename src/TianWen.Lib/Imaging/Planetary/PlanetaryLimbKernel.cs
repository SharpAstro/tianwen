using System;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A stack's blur as its limb reads it with a wing that can widen (docs/plans/planetary-restoration.md, R7 part 3): a Gaussian core
/// convolved with a delta and a wing of the telescope's scattered light, (1 + (r/a)^2)^(-3/2), whose transfer is
/// exp(-2 pi^2 s^2 f^2) ((1 - h) + h exp(-2 pi a f)).
/// </summary>
/// <param name="CoreSigma">The core's sigma, pixels.</param>
/// <param name="WingFraction">The share of the light in the wing.</param>
/// <param name="WingScale">The wing's a, pixels.</param>
/// <param name="Brightness">The disk's brightness over the sharp model's unit.</param>
/// <param name="Sky">The sky's level.</param>
/// <param name="RmsResidual">The fit's RMS residual over the pixels it used.</param>
public readonly record struct LimbKernel(double CoreSigma, double WingFraction, double WingScale, double Brightness, double Sky, double RmsResidual)
{
    /// <summary>The kernel's transfer at <paramref name="cyclesPerPixel"/>.</summary>
    public double TransferAt(double cyclesPerPixel)
        => Math.Exp(-2 * Math.PI * Math.PI * CoreSigma * CoreSigma * cyclesPerPixel * cyclesPerPixel)
            * ((1 - WingFraction) + (WingFraction * Math.Exp(-2 * Math.PI * WingScale * cyclesPerPixel)));
}

/// <summary>
/// Fits a <see cref="LimbKernel"/> to a stack around the limb fit's sharp model (<see cref="PlanetaryLimbFit.SharpModel"/>): the
/// geometry, the limb darkening and the zonal albedo the limb fit found, and the kernel, the brightness and the sky refitted over 0.8 to
/// 2 radii, where the wing's light shows in the sky. The limb fit's own kernel, two Gaussians with the wing's share bounded at a half,
/// read 2022-09-03's finest bands 17 to 39 % too blurred with its wing at that bound (R7 part 2).
/// </summary>
public static class PlanetaryLimbKernel
{
    /// <summary>
    /// <paramref name="plane"/> blurred by <paramref name="kernel"/>, through its transfer on a grid padded to hold the wing (zero
    /// outside the plane, so a sky of zero stays zero).
    /// </summary>
    public static float[] Blur(ReadOnlySpan<float> plane, int width, int height, in LimbKernel kernel)
    {
        var margin = (int)Math.Ceiling(8 * Math.Max(kernel.WingScale, 3 * kernel.CoreSigma));
        var n = 1;
        while (n < Math.Max(width, height) + margin)
        {
            n <<= 1;
        }
        var field = new Complex[n * n];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                field[(y * n) + x] = plane[(y * width) + x];
            }
        }
        Fft2D.Forward(field, n, n);
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
                field[(ky * n) + kx] *= kernel.TransferAt(Math.Sqrt((fx * fx) + (fy * fy)));
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
    /// The kernel, brightness and sky that best fit <paramref name="stack"/> (one plane) over <paramref name="innerRadii"/> to
    /// <paramref name="outerRadii"/> of <paramref name="fit"/>'s disk, around its sharp model: Levenberg-Marquardt, started at the fit's
    /// core with a small wing. Null when too few pixels lie in the frame.
    /// </summary>
    public static LimbKernel? Fit(Image stack, in LimbFit fit, LimbFitOptions options, double innerRadii = 0.8, double outerRadii = 2)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(options);
        var (width, height) = (stack.Width, stack.Height);
        // A window about the disk, reaching past the annulus by the widest wing's reach, where the sharp model is convolved.
        var half = (int)Math.Ceiling((outerRadii * fit.EquatorialRadius) + 64);
        var (x0, y0) = ((int)Math.Round(fit.CenterX) - half, (int)Math.Round(fit.CenterY) - half);
        var size = 2 * half;
        var sharpFrame = PlanetaryLimbFit.SharpModel(fit, options, width, height);
        var window = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            var fy = y + y0;
            if (fy < 0 || fy >= height)
            {
                continue;
            }
            for (var x = 0; x < size; x++)
            {
                var fx = x + x0;
                if (fx >= 0 && fx < width)
                {
                    window[(y * size) + x] = sharpFrame[(fy * width) + fx];
                }
            }
        }
        var disk = MetricDisk.From(fit, options.AxisRatio);
        var plane = stack.GetChannelSpan(0);
        var pixels = new System.Collections.Generic.List<(int Window, float Value)>();
        for (var y = Math.Max(0, y0); y < Math.Min(height, y0 + size); y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(width, x0 + size); x++)
            {
                var r = disk.RadiiAt(x, y);
                if (r >= innerRadii && r <= outerRadii)
                {
                    pixels.Add((((y - y0) * size) + x - x0, plane[(y * width) + x]));
                }
            }
        }
        if (pixels.Count < 200)
        {
            return null;
        }

        static LimbKernel KernelOf(ReadOnlySpan<double> p, double rms = 0)
            => new(Math.Abs(p[0]), Math.Clamp(p[1], 0, 0.99), Math.Max(Math.Abs(p[2]), 0.5), p[3], p[4], rms);
        Span<double> start = [fit.PsfSigma, 0.05, 8, fit.Brightness, fit.Sky];
        Span<double> step = [1e-3, 1e-3, 1e-2, 1e-4 * Math.Max(Math.Abs(fit.Brightness), 1e-6), 1e-4 * Math.Max(Math.Abs(fit.Brightness), 1e-6)];
        var result = LevenbergMarquardt.Fit(start, pixels.Count, (p, residuals) =>
        {
            var kernel = KernelOf(p);
            var blurred = Blur(window, size, size, kernel with { Brightness = 1, Sky = 0 });
            for (var i = 0; i < pixels.Count; i++)
            {
                residuals[i] = kernel.Sky + (kernel.Brightness * blurred[pixels[i].Window]) - pixels[i].Value;
            }
        }, step, maxIterations: 40);
        return KernelOf(result.Parameters, Math.Sqrt(2 * result.Cost / pixels.Count));
    }
}
