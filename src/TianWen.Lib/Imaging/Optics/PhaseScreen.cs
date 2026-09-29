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

    internal static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

/// <summary>
/// A Kolmogorov screen that evolves from one frame to the next, as the air over a telescope does between frames a few
/// milliseconds apart: every Fourier mode, and every subharmonic of <see cref="PhaseScreen"/>'s, is carried along by the
/// wind and partly renewed, <c>c(t) = alpha e^(-2 pi i f.v dt) c(t - dt) + sqrt(1 - alpha^2) n(t)</c> (Srinath, Poyneer, Rudy and
/// Ammons 2015, Opt. Express 23, 33335). Each step's screen has Kolmogorov's statistics, consecutive screens are the same air
/// moved on, and the renewal keeps the periodic screen from coming round again as the wind wraps it.
/// <para>
/// A finite outer scale L0 makes it von Karman's, <c>0.023 r0^(-5/3) (f^2 + 1/L0^2)^(-11/6)</c>: the eddies stop growing at L0,
/// which takes most from the tilt of a small aperture and almost nothing from its finer aberrations. Kolmogorov's infinite
/// scale moved 2022-09-03's synthetic disk 1.25 px RMS where the real one moved 0.96.
/// </para>
/// </summary>
public sealed class EvolvingPhaseScreen
{
    private readonly int _n;
    private readonly double _spacing;
    private readonly Random _random;
    private readonly Complex[] _modes;
    private readonly double[] _amplitude;
    private readonly Complex[] _scratch;
    private readonly Complex[] _rows;
    // The three subharmonic levels, each a 3 by 3 grid of coefficients (the centre empty), their amplitudes and spacings.
    private readonly Complex[] _sub = new Complex[27];
    private readonly double[] _subAmplitude = new double[27];
    private readonly double[] _subDf = new double[3];

    /// <summary>
    /// A screen of <paramref name="n"/> by <paramref name="n"/> samples (a power of two) <paramref name="spacingM"/> apart, of
    /// Fried parameter <paramref name="r0M"/> and outer scale <paramref name="outerScaleM"/> (infinite for Kolmogorov's), drawn
    /// from <paramref name="random"/>.
    /// </summary>
    public EvolvingPhaseScreen(int n, double spacingM, double r0M, Random random, double outerScaleM = double.PositiveInfinity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outerScaleM);
        var f0Squared = double.IsPositiveInfinity(outerScaleM) ? 0 : 1 / (outerScaleM * outerScaleM);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spacingM);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(r0M);
        ArgumentNullException.ThrowIfNull(random);
        _n = n;
        _spacing = spacingM;
        _random = random;
        _modes = new Complex[n * n];
        _amplitude = new double[n * n];
        _scratch = new Complex[n * n];
        _rows = new Complex[3 * n];

        var df = 1.0 / (n * spacingM);
        var scale = Math.Sqrt(0.023 * Math.Pow(r0M, -5.0 / 3.0)) * df;
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) * df;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) * df;
                var f = Math.Sqrt((fx * fx) + (fy * fy));
                _amplitude[(ky * n) + kx] = f == 0 ? 0 : scale * Math.Pow((f * f) + f0Squared, -11.0 / 12.0);
            }
        }
        var size = n * spacingM;
        var subScale = scale / df;
        for (var p = 0; p < 3; p++)
        {
            var sdf = 1.0 / (Math.Pow(3, p + 1) * size);
            _subDf[p] = sdf;
            for (var j = -1; j <= 1; j++)
            {
                for (var i = -1; i <= 1; i++)
                {
                    var f = Math.Sqrt((i * i) + (j * j)) * sdf;
                    _subAmplitude[(p * 9) + ((j + 1) * 3) + i + 1] = f == 0 ? 0 : subScale * Math.Pow((f * f) + f0Squared, -11.0 / 12.0) * sdf;
                }
            }
        }
        for (var k = 0; k < _modes.Length; k++)
        {
            _modes[k] = Draw(_amplitude[k]);
        }
        for (var k = 0; k < _sub.Length; k++)
        {
            _sub[k] = Draw(_subAmplitude[k]);
        }
    }

    /// <summary>The screen's side, in metres.</summary>
    public double SizeM => _n * _spacing;

    /// <summary>
    /// Moves the screen on by <paramref name="dtSeconds"/> in a wind of (<paramref name="windXMps"/>, <paramref name="windYMps"/>),
    /// keeping <paramref name="alpha"/> of each mode and renewing the rest.
    /// </summary>
    public void Step(double windXMps, double windYMps, double dtSeconds, double alpha)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(alpha, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(alpha, 1);
        var renew = Math.Sqrt(1 - (alpha * alpha));
        var df = 1.0 / (_n * _spacing);
        var (sx, sy) = (windXMps * dtSeconds, windYMps * dtSeconds);
        for (var ky = 0; ky < _n; ky++)
        {
            var fy = (ky < _n / 2 ? ky : ky - _n) * df;
            for (var kx = 0; kx < _n; kx++)
            {
                var fx = (kx < _n / 2 ? kx : kx - _n) * df;
                var k = (ky * _n) + kx;
                _modes[k] = (alpha * _modes[k] * Complex.FromPolarCoordinates(1, -2 * Math.PI * ((fx * sx) + (fy * sy)))) + (renew * Draw(_amplitude[k]));
            }
        }
        for (var p = 0; p < 3; p++)
        {
            for (var j = -1; j <= 1; j++)
            {
                for (var i = -1; i <= 1; i++)
                {
                    var k = (p * 9) + ((j + 1) * 3) + i + 1;
                    var shift = -2 * Math.PI * _subDf[p] * ((i * sx) + (j * sy));
                    _sub[k] = (alpha * _sub[k] * Complex.FromPolarCoordinates(1, shift)) + (renew * Draw(_subAmplitude[k]));
                }
            }
        }
    }

    /// <summary>Writes the screen as it is now into <paramref name="phase"/> (radians, row-major, zero-mean).</summary>
    public void Fill(Span<double> phase)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(phase.Length, _n * _n);
        Array.Copy(_modes, _scratch, _modes.Length);
        Fft2D.Inverse(_scratch, _n, _n);
        var gain = (double)_n * _n;
        for (var i = 0; i < phase.Length; i++)
        {
            phase[i] = _scratch[i].Real * gain;
        }

        // The subharmonics, as PhaseScreen's (each level separable in x and y), then the whole made zero-mean.
        var rows = _rows;
        for (var p = 0; p < 3; p++)
        {
            var df = _subDf[p];
            for (var j = 0; j < 3; j++)
            {
                for (var x = 0; x < _n; x++)
                {
                    var px = (x - (_n / 2)) * _spacing;
                    var row = Complex.Zero;
                    for (var i = 0; i < 3; i++)
                    {
                        row += _sub[(p * 9) + (j * 3) + i] * Complex.FromPolarCoordinates(1, 2 * Math.PI * (i - 1) * df * px);
                    }
                    rows[(j * _n) + x] = row;
                }
            }
            for (var y = 0; y < _n; y++)
            {
                var py = (y - (_n / 2)) * _spacing;
                var c0 = Complex.FromPolarCoordinates(1, -2 * Math.PI * df * py);
                var c2 = Complex.FromPolarCoordinates(1, 2 * Math.PI * df * py);
                for (var x = 0; x < _n; x++)
                {
                    var value = ((c0 * rows[x]) + rows[_n + x] + (c2 * rows[(2 * _n) + x])).Real;
                    phase[(y * _n) + x] += value;
                }
            }
        }
        double mean = 0;
        foreach (var v in phase)
        {
            mean += v;
        }
        mean /= phase.Length;
        for (var i = 0; i < phase.Length; i++)
        {
            phase[i] -= mean;
        }
    }

    private Complex Draw(double amplitude)
        => amplitude == 0 ? Complex.Zero : new Complex(PhaseScreen.Gaussian(_random), PhaseScreen.Gaussian(_random)) * amplitude;
}
