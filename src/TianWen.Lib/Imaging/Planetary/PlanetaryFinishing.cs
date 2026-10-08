using System;
using System.Collections.Generic;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A finishing step after <see cref="PlanetarySharpening"/> (#1279), from Con Kolivas's <c>PlanetaryTools</c> and <c>AdaptiveSharpen</c>,
/// read for their methods only (GPL-3.0 and unlicensed: reimplemented from the description, never copied). Flags, so steps combine; the
/// adaptive weighting runs before the low-pass, which comes last.
/// </summary>
[Flags]
public enum PlanetaryFinish
{
    /// <summary>The sharpening as derived.</summary>
    None = 0,

    /// <summary>A low-pass at the pupil's own diffraction cutoff (<see cref="PlanetaryFinishing.LowPassAtCutoff"/>): past it the scene holds nothing.</summary>
    Cutoff = 1,

    /// <summary>The sharpening's change from the stack weighted by the stack's local contrast, at matched noise (<see cref="PlanetaryFinishing.ContrastWeighted"/>).</summary>
    Adaptive = 2,

    /// <summary>Kolivas's own single contrast-damped Richardson-Lucy step (<see cref="PlanetaryFinishing.KolivasStep"/>), a reference only.</summary>
    Kolivas = 4,

    /// <summary>Kolivas's FFT denoise: a smooth low-pass fitted to the sharpened window's Wiener target (<see cref="PlanetaryFinishing.WienerLowPass"/>).</summary>
    Wiener = 8,
}

/// <summary>
/// The finishing steps of <see cref="PlanetaryFinish"/>, each on one channel's window as <see cref="PlanetarySharpening"/> cuts it: a square
/// of <c>size</c> pixels normalised on the disk (sky 0, disk 1), the planet described by a <see cref="MetricDisk"/> in its own coordinates.
/// docs/plans/planetary-restoration.md, "Contrast-adaptive deconvolution (Kolivas's PlanetaryTools)".
/// </summary>
public static class PlanetaryFinishing
{
    /// <summary>The low-pass is one up to this fraction of the cutoff, and falls by a smootherstep to zero at the cutoff.</summary>
    public const double CutoffStart = 0.85;

    /// <summary>The side of the square the local contrast is read over (Kolivas's 7).</summary>
    public const int ContrastWindow = 7;

    /// <summary>The adaptive weight is the contrast's alone inside this fraction of the outline, and one from the outline out.</summary>
    public const double AdaptiveInside = 0.9;

    /// <summary>The matched noise is read over this flattest fraction of the interior, by the stack's local contrast.</summary>
    public const double FlattestFraction = 0.2;

    /// <summary>
    /// The pupil's diffraction cutoff in cycles a pixel: its diameter over the wavelength, cycles a radian, times the radians a pixel
    /// (<c>p / (lambda N)</c> for a pixel pitch p at focal ratio N). Past it a telescope passes nothing of the scene.
    /// </summary>
    public static double CutoffCyclesPerPixel(Pupil pupil, double wavelengthNm, double arcsecPerPixel)
        => pupil.DiameterM / (wavelengthNm * 1e-9) / ShortExposurePsf.ArcsecPerRadian * arcsecPerPixel;

    /// <summary>The low-pass's transfer at <paramref name="cyclesPerPixel"/>: one to <see cref="CutoffStart"/> of the cutoff, zero past it.</summary>
    public static double CutoffTransfer(double cyclesPerPixel, double cutoff) => Smootherstep(cyclesPerPixel, CutoffStart * cutoff, cutoff);

    /// <summary>
    /// <paramref name="window"/> low-passed at the pupil's cutoff (<see cref="CutoffTransfer"/>), radially: what a sharpening raised past it
    /// is noise, since the scene holds nothing there (the idea of Kolivas's FFT denoise, its cutoff set by the optics instead of fitted).
    /// </summary>
    public static float[] LowPassAtCutoff(ReadOnlySpan<float> window, int size, double cutoff)
        => cutoff >= 0.5 / CutoffStart ? window.ToArray() : PlanetaryInverse.Apply(window, size, size, f => CutoffTransfer(f, cutoff));

    /// <summary>
    /// Kolivas's FFT denoise as #1279's rule C has it: a low-pass falling by a smootherstep from <c>From</c> to <c>To</c> cycles a pixel,
    /// fitted by least squares, ring by ring up to 0.5 cycles a pixel and each ring alike, to the sharpened window's Wiener target
    /// <c>clip(1 - N' / P', 0, 1)</c>. P' is <paramref name="sharpened"/>'s power inside the disk
    /// (<see cref="PlanetaryWaveletGains.StackPower"/>), N' the stack's white floor <paramref name="white"/> (in
    /// <see cref="PlanetaryInverse.WhiteNoise"/>'s units) through the sharpening's transfer squared, so the noise is never read off what the
    /// filter takes out. A <c>From</c> of 0.5 is no cut. The window is filtered radially, whole.
    /// </summary>
    public static (float[] Window, double From, double To) WienerLowPass(ReadOnlySpan<float> sharpened, int size, in MetricDisk disk, double white,
        Func<double, double> sharpening)
    {
        ArgumentNullException.ThrowIfNull(sharpening);
        var power = PlanetaryWaveletGains.StackPower(sharpened, size, size, disk);
        var n = power.Length;
        var (frequencies, target) = (new List<double>(), new List<double>());
        for (var r = 1; r < n && r / (double)n <= 0.5; r++)
        {
            var f = r / (double)n;
            var h = sharpening(f);
            var noise = white * h * h;
            frequencies.Add(f);
            if (noise <= 0)
            {
                target.Add(1);
            }
            else
            {
                target.Add(power[r] > 0 ? Math.Clamp(1 - (noise / power[r]), 0, 1) : 0);
            }
        }
        // On a grid of hundredths, so a start of exactly 0.5 (no cut) is one of the candidates.
        var (bestFrom, bestTo, bestError) = (0.5, 0.52, double.PositiveInfinity);
        for (var i = 2; i <= 50; i++)
        {
            for (var j = i + 2; j <= 62; j++)
            {
                var (from, to) = (i / 100.0, j / 100.0);
                double error = 0;
                for (var k = 0; k < frequencies.Count; k++)
                {
                    var d = Smootherstep(frequencies[k], from, to) - target[k];
                    error += d * d;
                }
                if (error < bestError)
                {
                    (bestFrom, bestTo, bestError) = (from, to, error);
                }
            }
        }
        var (cutFrom, cutTo) = (bestFrom, bestTo);
        var filtered = cutFrom >= 0.5 ? sharpened.ToArray() : PlanetaryInverse.Apply(sharpened, size, size, f => Smootherstep(f, cutFrom, cutTo));
        return (filtered, cutFrom, cutTo);
    }

    // One to from, zero from to, a smootherstep between.
    private static double Smootherstep(double f, double from, double to)
    {
        var t = Math.Clamp((f - from) / (to - from), 0, 1);
        return 1 - (t * t * t * ((t * ((6 * t) - 15)) + 10));
    }

    /// <summary>
    /// The sharpening's change from the stack, weighted per pixel by the square root of the STACK's local contrast (Kolivas's adaptive
    /// strength): <paramref name="contrastFrom"/>'s spread over <see cref="ContrastWindow"/> pixels, scaled to 0 to 1 by its least and
    /// greatest over the interior inside <see cref="AdaptiveInside"/> of the outline (so the limb does not set the scale, as his whole-frame
    /// scale lets it), the weight the contrast's alone inside that and one from the outline out (the limb fix's drawing outside the planet
    /// kept). The weights are then scaled together so the noise over the interior's flattest <see cref="FlattestFraction"/> (by the stack's
    /// contrast, never the output's, so the mask does not see the noise it measures) equals the uniform sharpening's there: the RMS of each
    /// image less its 3x3 mean. A new window.
    /// </summary>
    public static float[] ContrastWeighted(ReadOnlySpan<float> stack, ReadOnlySpan<float> sharpened, ReadOnlySpan<float> contrastFrom, int size,
        in MetricDisk disk)
    {
        var n = size * size;
        var spread = LocalSpread(contrastFrom, size, ContrastWindow);
        double least = double.PositiveInfinity, most = double.NegativeInfinity;
        var interiorCount = 0;
        for (var i = 0; i < n; i++)
        {
            if (disk.ClearRadiiAt(i % size, i / size) < AdaptiveInside)
            {
                (least, most) = (Math.Min(least, spread[i]), Math.Max(most, spread[i]));
                interiorCount++;
            }
        }
        if (interiorCount == 0 || !(most > least))
        {
            return sharpened.ToArray();
        }

        // The contrast's own weight, and how far each pixel is from being the planet's interior (0 inside, 1 from the outline out).
        var weight = new double[n];
        var outward = new double[n];
        for (var i = 0; i < n; i++)
        {
            var c = Math.Clamp((spread[i] - least) / (most - least), 0, 1);
            weight[i] = Math.Sqrt(c);
            var r = disk.ClearRadiiAt(i % size, i / size);
            var t = Math.Clamp((r - AdaptiveInside) / (1 - AdaptiveInside), 0, 1);
            outward[i] = t * t * (3 - (2 * t));
        }

        // The flattest fifth of the interior, by the stack's contrast.
        var interiorSpread = new float[interiorCount];
        for (int i = 0, k = 0; i < n; i++)
        {
            if (disk.ClearRadiiAt(i % size, i / size) < AdaptiveInside)
            {
                interiorSpread[k++] = spread[i];
            }
        }
        var flatBelow = StatisticsHelper.PercentileFast(interiorSpread, FlattestFraction);

        // The noise of stack + k * weight * change over the flat pixels is quadratic in k: solve it for the uniform sharpening's.
        var change = new float[n];
        var weighted = new float[n];
        for (var i = 0; i < n; i++)
        {
            change[i] = sharpened[i] - stack[i];
            weighted[i] = (float)(weight[i] * change[i]);
        }
        var a = HighPass(stack, size);
        var b = HighPass(weighted, size);
        var u = HighPass(sharpened, size);
        double aa = 0, ab = 0, bb = 0, uu = 0;
        var flat = 0;
        for (var i = 0; i < n; i++)
        {
            if (spread[i] <= flatBelow && disk.ClearRadiiAt(i % size, i / size) < AdaptiveInside)
            {
                (aa, ab, bb, uu) = (aa + (a[i] * a[i]), ab + (a[i] * b[i]), bb + (b[i] * b[i]), uu + (u[i] * u[i]));
                flat++;
            }
        }
        if (flat == 0 || !(bb > 0))
        {
            return sharpened.ToArray();
        }
        var (meanAa, meanAb, meanBb, meanUu) = (aa / flat, ab / flat, bb / flat, uu / flat);
        var discriminant = (meanAb * meanAb) - (meanBb * (meanAa - meanUu));
        var scale = discriminant > 0 ? (-meanAb + Math.Sqrt(discriminant)) / meanBb : 1;

        var result = new float[n];
        for (var i = 0; i < n; i++)
        {
            var w = (scale * weight[i] * (1 - outward[i])) + outward[i];
            result[i] = (float)(stack[i] + (w * change[i]));
        }
        return result;
    }

    /// <summary>
    /// Kolivas's adaptive deconvolution as his tool runs it, a reference to read beside ours (never a default): ONE Richardson-Lucy step
    /// with a Moffat PSF of FWHM 1.29 px (beta 2, 5x5), its correction damped per pixel by the square root of the local contrast scaled
    /// by the whole window's least and greatest, at a strength of <paramref name="amount"/> over pi, cubed as his luminance model has it:
    /// <c>d = 1 + s sqrt(C) (c - 1)</c>, <c>Y' = Y d^3</c>. On the window alone (a mono master's, or one channel's).
    /// </summary>
    public static float[] KolivasStep(ReadOnlySpan<float> window, int size, double amount)
    {
        var n = size * size;
        var psf = Moffat(1.29, 2, 2);
        var blurred = Convolve(window, size, psf, 2);
        var ratio = new float[n];
        for (var i = 0; i < n; i++)
        {
            ratio[i] = blurred[i] > 1e-6f ? window[i] / blurred[i] : 1;
        }
        var correction = Convolve(ratio, size, psf, 2);
        var spread = LocalSpread(window, size, ContrastWindow);
        float least = float.PositiveInfinity, most = float.NegativeInfinity;
        foreach (var s in spread)
        {
            (least, most) = (MathF.Min(least, s), MathF.Max(most, s));
        }
        var strength = amount / Math.PI;
        var result = new float[n];
        for (var i = 0; i < n; i++)
        {
            var contrast = most > least ? (spread[i] - least) / (most - least) : 0;
            var d = Math.Max(0, 1 + (strength * Math.Sqrt(contrast) * (correction[i] - 1)));
            result[i] = (float)(window[i] * d * d * d);
        }
        return result;
    }

    /// <summary>Each pixel's standard deviation over the <paramref name="side"/>-square about it, the edges mirrored.</summary>
    internal static float[] LocalSpread(ReadOnlySpan<float> plane, int size, int side)
    {
        var half = side / 2;
        var result = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double sum = 0, squares = 0;
                for (var dy = -half; dy <= half; dy++)
                {
                    var row = ATrousWaveletTransform.Reflect(y + dy, size) * size;
                    for (var dx = -half; dx <= half; dx++)
                    {
                        double v = plane[row + ATrousWaveletTransform.Reflect(x + dx, size)];
                        (sum, squares) = (sum + v, squares + (v * v));
                    }
                }
                var count = side * side;
                var mean = sum / count;
                result[(y * size) + x] = (float)Math.Sqrt(Math.Max(0, (squares / count) - (mean * mean)));
            }
        }
        return result;
    }

    // The plane less its 3x3 mean, the edges mirrored.
    private static float[] HighPass(ReadOnlySpan<float> plane, int size)
    {
        var result = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double sum = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var row = ATrousWaveletTransform.Reflect(y + dy, size) * size;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        sum += plane[row + ATrousWaveletTransform.Reflect(x + dx, size)];
                    }
                }
                result[(y * size) + x] = (float)(plane[(y * size) + x] - (sum / 9));
            }
        }
        return result;
    }

    // A Moffat profile of the given FWHM and beta on a square of 2*radius+1, normalised to unit sum.
    private static double[] Moffat(double fwhm, double beta, int radius)
    {
        var alpha = fwhm / (2 * Math.Sqrt(Math.Pow(2, 1 / beta) - 1));
        var side = (2 * radius) + 1;
        var kernel = new double[side * side];
        double total = 0;
        for (var y = -radius; y <= radius; y++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                var v = Math.Pow(1 + (((x * x) + (y * y)) / (alpha * alpha)), -beta);
                kernel[((y + radius) * side) + x + radius] = v;
                total += v;
            }
        }
        for (var i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= total;
        }
        return kernel;
    }

    // The plane convolved with a symmetric square kernel, the edges mirrored.
    private static float[] Convolve(ReadOnlySpan<float> plane, int size, double[] kernel, int radius)
    {
        var side = (2 * radius) + 1;
        var result = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double sum = 0;
                for (var dy = -radius; dy <= radius; dy++)
                {
                    var row = ATrousWaveletTransform.Reflect(y + dy, size) * size;
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        sum += kernel[((dy + radius) * side) + dx + radius] * plane[row + ATrousWaveletTransform.Reflect(x + dx, size)];
                    }
                }
                result[(y * size) + x] = (float)sum;
            }
        }
        return result;
    }
}
