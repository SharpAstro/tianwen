using System;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Optics;

/// <summary>
/// The image of a point through a pupil and a phase screen: <c>|FT(pupil exp(i phase))|^2</c>, normalised to a unit sum and
/// centred on sample (n/2, n/2). With pupil samples <c>spacing</c> apart, one PSF sample subtends <c>wavelength / (n spacing)</c>
/// radians, so a render picks the spacing for the image scale it wants (<see cref="PupilSpacingFor"/>). The pupil must fit in
/// half the grid for the PSF to be sampled at Nyquist, and its transfer function is then exactly zero past
/// <c>D / (n spacing)</c> cycles a sample, the aperture's cutoff.
/// </summary>
public static class ShortExposurePsf
{
    /// <summary>Radians in an arcsecond's reciprocal: 180 * 3600 / pi.</summary>
    public const double ArcsecPerRadian = 206264.80624709636;

    /// <summary>The pupil sample spacing that makes an <paramref name="n"/>-sample PSF grid's sample subtend <paramref name="arcsecPerSample"/>.</summary>
    public static double PupilSpacingFor(double wavelengthM, double arcsecPerSample, int n)
        => wavelengthM / (n * (arcsecPerSample / ArcsecPerRadian));

    /// <summary>
    /// Writes the PSF of <paramref name="pupil"/> (transmission) under <paramref name="phase"/> (radians, or empty for none) into
    /// <paramref name="psf"/>, all <paramref name="n"/> by <paramref name="n"/> row-major, n a power of two.
    /// </summary>
    public static void Compute(ReadOnlySpan<float> pupil, ReadOnlySpan<double> phase, int n, Span<double> psf, Complex[]? scratch = null)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(pupil.Length, n * n);
        ArgumentOutOfRangeException.ThrowIfNotEqual(psf.Length, n * n);
        if (!phase.IsEmpty)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(phase.Length, n * n);
        }

        var field = scratch is { } s && s.Length == n * n ? s : new Complex[n * n];
        for (var i = 0; i < field.Length; i++)
        {
            field[i] = phase.IsEmpty ? new Complex(pupil[i], 0) : Complex.FromPolarCoordinates(pupil[i], phase[i]);
        }
        Fft2D.Forward(field, n, n);

        // Shift the zero frequency to the grid's centre, so the PSF's peak sits on sample (n/2, n/2).
        var half = n / 2;
        var sum = 0.0;
        for (var y = 0; y < n; y++)
        {
            var sy = (y + half) % n;
            for (var x = 0; x < n; x++)
            {
                var sx = (x + half) % n;
                var value = field[(y * n) + x];
                var power = (value.Real * value.Real) + (value.Imaginary * value.Imaginary);
                psf[(sy * n) + sx] = power;
                sum += power;
            }
        }
        if (sum > 0)
        {
            for (var i = 0; i < psf.Length; i++)
            {
                psf[i] /= sum;
            }
        }
    }
}
