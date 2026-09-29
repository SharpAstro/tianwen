using System;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Optics;

/// <summary>
/// Kolmogorov phase screens by the FFT method with subharmonics (Schmidt, Numerical Simulation of Optical Wave Propagation,
/// 2010, chapter 9; Lane et al. 1992; Johansson and Gavel 1994): complex Gaussian amplitudes shaped by the phase power
/// spectrum <c>0.023 r0^(-5/3) f^(-11/3)</c> and transformed to the pupil plane, plus three levels of subharmonics below the
/// grid's lowest frequency. Its structure function is <c>6.88 (r / r0)^(5/3)</c>. The subharmonics are not a refinement:
/// the scales larger than the screen carry a tilt across ANY separation, so a plain FFT screen was 28 % short of
/// Kolmogorov at 4 cm on a 2.56 m screen (measured, <c>OpticsPrimitiveTests</c>), where the missing part, <c>2.5 (r f)^(1/3)</c>
/// of the whole for a lowest frequency f, predicts 38 %.
/// </summary>
public static class PhaseScreen
{
    /// <summary>
    /// Fills <paramref name="phase"/> (radians, row-major, <paramref name="n"/> by <paramref name="n"/>, a power of two) with a
    /// screen of Fried parameter <paramref name="r0M"/>, its samples <paramref name="spacingM"/> apart.
    /// </summary>
    public static void Kolmogorov(Span<double> phase, int n, double spacingM, double r0M, Random random, Complex[]? scratch = null)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(phase.Length, n * n);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spacingM);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(r0M);

        var c = scratch is { } s && s.Length == n * n ? s : new Complex[n * n];
        var df = 1.0 / (n * spacingM);
        // sqrt(PSD) * df, with PSD = 0.023 r0^(-5/3) f^(-11/3): the f^(-11/6) is applied per sample below, and the subharmonics
        // take the same amplitude with their own df.
        var scale = Math.Sqrt(0.023 * Math.Pow(r0M, -5.0 / 3.0)) * df;
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) * df;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) * df;
                var f = Math.Sqrt((fx * fx) + (fy * fy));
                c[(ky * n) + kx] = f == 0
                    ? Complex.Zero
                    : new Complex(Gaussian(random), Gaussian(random)) * (scale * Math.Pow(f, -11.0 / 6.0));
            }
        }

        // Schmidt's screen is the unnormalised sum over the spectrum; the inverse here divides by n squared.
        Fft2D.Inverse(c, n, n);
        var gain = (double)n * n;
        for (var i = 0; i < phase.Length; i++)
        {
            phase[i] = c[i].Real * gain;
        }

        AddSubharmonics(phase, n, spacingM, scale / df, random);
    }

    /// <summary>
    /// Three levels of a 3 by 3 grid of frequencies at a third, a ninth and a 27th of the screen's lowest (Johansson and
    /// Gavel 1994), each drawn as the main screen's are, summed and made zero-mean.
    /// </summary>
    private static void AddSubharmonics(Span<double> phase, int n, double spacingM, double amplitude, Random random)
    {
        // A term c e^(2 pi i (i df x + j df y)) is separable, so each level is Re sum_j e^(2 pi i j df y) row_j(x), with
        // row_j(x) = sum_i c_ij e^(2 pi i i df x): three phasors a sample instead of eight sines and cosines.
        var size = n * spacingM;
        var rows = new Complex[3 * n];
        var coefficients = new Complex[9];
        var sumAll = 0.0;
        for (var p = 1; p <= 3; p++)
        {
            var df = 1.0 / (Math.Pow(3, p) * size);
            for (var j = -1; j <= 1; j++)
            {
                for (var i = -1; i <= 1; i++)
                {
                    var f = Math.Sqrt((i * i) + (j * j)) * df;
                    coefficients[((j + 1) * 3) + i + 1] = f == 0
                        ? Complex.Zero
                        : new Complex(Gaussian(random), Gaussian(random)) * (amplitude * Math.Pow(f, -11.0 / 6.0) * df);
                }
            }
            for (var j = 0; j < 3; j++)
            {
                for (var x = 0; x < n; x++)
                {
                    var px = (x - (n / 2)) * spacingM;
                    var row = Complex.Zero;
                    for (var i = 0; i < 3; i++)
                    {
                        row += coefficients[(j * 3) + i] * Complex.FromPolarCoordinates(1, 2 * Math.PI * (i - 1) * df * px);
                    }
                    rows[(j * n) + x] = row;
                }
            }
            for (var y = 0; y < n; y++)
            {
                var py = (y - (n / 2)) * spacingM;
                var c0 = Complex.FromPolarCoordinates(1, -2 * Math.PI * df * py);
                var c2 = Complex.FromPolarCoordinates(1, 2 * Math.PI * df * py);
                for (var x = 0; x < n; x++)
                {
                    var value = ((c0 * rows[x]) + rows[n + x] + (c2 * rows[(2 * n) + x])).Real;
                    phase[(y * n) + x] += value;
                    sumAll += value;
                }
            }
        }
        // The subharmonics are made zero-mean over the screen, as the FFT part already is (its zero frequency is empty).
        var mean = sumAll / (n * n);
        for (var i = 0; i < phase.Length; i++)
        {
            phase[i] -= mean;
        }
    }

    private static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
