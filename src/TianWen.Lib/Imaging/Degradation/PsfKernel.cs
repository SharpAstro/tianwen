using System;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging.Degradation
{
    /// <summary>
    /// A normalised 2D point-spread kernel used to ADD blur to an already-linear frame, and the
    /// convolution that applies it. This is the degradation side of the deconvolver programme
    /// (docs/plans/deconvolver-training.md section 2): a training pair is made by blurring a sharp
    /// master, so the kernel here is the EXTRA blur, never the frame's total PSF. Two Gaussians of
    /// FWHM a and b compose to sqrt(a^2 + b^2), so a master whose stars measure 2.8 px convolved with
    /// a 3.0 px kernel lands at 4.1 px, and it is the 3.0 that is drawn and labelled.
    ///
    /// <para><b>The Moffat convention is <see cref="PsfProfileFit"/>'s</b>, so a drawn beta means what
    /// a measured beta means: the profile is <c>(1 + (r/alpha)^2)^-beta</c> with
    /// <c>alpha = fwhm / (2 sqrt(2^(1/beta) - 1))</c>, and LOWER beta is heavier wings. Drawing a
    /// (FWHM, beta) pair against a fitter that used another parameterisation would label every tile
    /// with a number the estimator cannot reproduce.</para>
    ///
    /// <para><b>Truncation is renormalised, deliberately.</b> A Moffat with beta near 1 still carries
    /// a percent of its flux past any radius worth convolving with, so the kernel is cut at
    /// <see cref="Radius"/> and its weights divided by their own sum. That keeps the operation
    /// flux-conserving (a flat field stays flat, which is what a background fit downstream assumes)
    /// at the cost of a slightly lighter wing than the analytic profile. The alternative, an
    /// unnormalised truncated kernel, darkens the whole frame by the missing fraction and would show
    /// up as a level shift the pair encodes as signal.</para>
    /// </summary>
    public sealed class PsfKernel
    {
        private readonly float[] _weights;

        private PsfKernel(int radius, float[] weights, bool separable, double fwhm, double beta)
        {
            Radius = radius;
            _weights = weights;
            IsSeparable = separable;
            Fwhm = fwhm;
            Beta = beta;
        }

        /// <summary>Half-width of the square kernel in pixels; the kernel is <c>2 * Radius + 1</c> on a side.</summary>
        public int Radius { get; }

        /// <summary>Kernel edge length in pixels.</summary>
        public int Size => (2 * Radius) + 1;

        /// <summary>The FWHM this kernel was built for, in pixels.</summary>
        public double Fwhm { get; }

        /// <summary>The Moffat exponent, or <see cref="double.PositiveInfinity"/> for a Gaussian.</summary>
        public double Beta { get; }

        /// <summary>Whether the kernel is a circular Gaussian, which convolves as two 1D passes.</summary>
        public bool IsSeparable { get; }

        /// <summary>Row-major kernel weights, summing to 1.</summary>
        public ReadOnlySpan<float> Weights => _weights;

        /// <summary>
        /// A circular or elongated Gaussian of the given FWHM in pixels.
        /// </summary>
        /// <param name="fwhmPx">Full width at half maximum along the MINOR axis, in pixels.</param>
        /// <param name="elongation">Major/minor axis ratio (1 = circular). Guiding smear and field
        /// aberration both show up here; the deconvolver draws it from the measured ellipticity bins.</param>
        /// <param name="positionAngleDeg">Major-axis angle in the pixel frame, degrees anticlockwise from +X.</param>
        public static PsfKernel Gaussian(double fwhmPx, double elongation = 1.0, double positionAngleDeg = 0.0)
            => Build(fwhmPx, double.PositiveInfinity, elongation, positionAngleDeg);

        /// <summary>
        /// A Moffat of the given FWHM and exponent, in <see cref="PsfProfileFit"/>'s parameterisation.
        /// </summary>
        /// <param name="fwhmPx">Full width at half maximum along the MINOR axis, in pixels.</param>
        /// <param name="beta">Moffat exponent; lower is heavier-winged. The archive's measured range is roughly 1.5 to 8.</param>
        /// <param name="elongation">Major/minor axis ratio (1 = circular).</param>
        /// <param name="positionAngleDeg">Major-axis angle in the pixel frame, degrees anticlockwise from +X.</param>
        public static PsfKernel Moffat(double fwhmPx, double beta, double elongation = 1.0, double positionAngleDeg = 0.0)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beta);
            return Build(fwhmPx, beta, elongation, positionAngleDeg);
        }

        private static PsfKernel Build(double fwhmPx, double beta, double elongation, double positionAngleDeg)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fwhmPx);
            ArgumentOutOfRangeException.ThrowIfLessThan(elongation, 1.0);

            var isGaussian = double.IsPositiveInfinity(beta);
            // A Gaussian is gone by 3 sigma; a Moffat's wings are not, and the lighter the beta the
            // further they run, so the radius grows as beta falls. Capped, because a beta near 1 would
            // otherwise ask for a kernel wider than the tile it is applied to.
            var sigma = fwhmPx / 2.3548200450309493;
            var radiusF = isGaussian
                ? 3.0 * sigma * elongation
                : Math.Min(6.0 * fwhmPx, fwhmPx * elongation * (1.0 + (4.0 / beta)));
            var radius = Math.Max(1, (int)Math.Ceiling(radiusF));

            var size = (2 * radius) + 1;
            var weights = new float[size * size];
            var paRad = positionAngleDeg * Math.PI / 180.0;
            var cos = Math.Cos(paRad);
            var sin = Math.Sin(paRad);
            var alpha = isGaussian ? 0.0 : fwhmPx / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / beta) - 1.0));
            var twoSigma2 = 2.0 * sigma * sigma;

            var sum = 0.0;
            for (var ky = -radius; ky <= radius; ky++)
            {
                for (var kx = -radius; kx <= radius; kx++)
                {
                    // Into the kernel's own frame, then squash the major axis so `elongation` widens
                    // it rather than narrowing the minor one (which would change the stated FWHM).
                    var u = (kx * cos) + (ky * sin);
                    var v = (-kx * sin) + (ky * cos);
                    u /= elongation;
                    var r2 = (u * u) + (v * v);
                    var w = isGaussian
                        ? Math.Exp(-r2 / twoSigma2)
                        : Math.Pow(1.0 + (r2 / (alpha * alpha)), -beta);
                    weights[((ky + radius) * size) + kx + radius] = (float)w;
                    sum += w;
                }
            }

            var inv = (float)(1.0 / sum);
            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] *= inv;
            }

            return new PsfKernel(radius, weights, isGaussian && elongation == 1.0, fwhmPx, beta);
        }

        /// <summary>
        /// Convolves one plane with this kernel, edge-clamping at the border.
        /// </summary>
        /// <remarks>
        /// The caller is expected to hand in a region cut with at least <see cref="Radius"/> pixels of
        /// margin around the part it keeps, so the clamped border never reaches the exported tile. A
        /// NaN in the source poisons its whole kernel footprint, which is why the dataset path cuts
        /// cells from the all-frames intersection where no NaN exists.
        /// </remarks>
        public float[] Convolve(ReadOnlySpan<float> src, int width, int height)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(src.Length, width * height);
            var dst = new float[width * height];
            Convolve(src, width, height, dst);
            return dst;
        }

        /// <inheritdoc cref="Convolve(ReadOnlySpan{float}, int, int)"/>
        public void Convolve(ReadOnlySpan<float> src, int width, int height, Span<float> dst)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(src.Length, width * height);
            ArgumentOutOfRangeException.ThrowIfLessThan(dst.Length, width * height);

            if (IsSeparable)
            {
                ConvolveSeparable(src, width, height, dst);
                return;
            }

            var size = Size;
            var radius = Radius;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var acc = 0.0;
                    for (var ky = -radius; ky <= radius; ky++)
                    {
                        var sy = Math.Clamp(y + ky, 0, height - 1);
                        var rowOff = sy * width;
                        var kOff = ((ky + radius) * size) + radius;
                        for (var kx = -radius; kx <= radius; kx++)
                        {
                            var sx = Math.Clamp(x + kx, 0, width - 1);
                            acc += src[rowOff + sx] * _weights[kOff + kx];
                        }
                    }
                    dst[(y * width) + x] = (float)acc;
                }
            }
        }

        private void ConvolveSeparable(ReadOnlySpan<float> src, int width, int height, Span<float> dst)
        {
            var radius = Radius;
            var size = Size;
            // The centre row of a circular Gaussian IS its 1D profile up to a constant, so take it and
            // renormalise rather than rebuilding: one array, and the two passes then compose to exactly
            // the 2D kernel this instance reports.
            var line = new double[size];
            var lineSum = 0.0;
            for (var i = 0; i < size; i++)
            {
                var w = _weights[(radius * size) + i];
                line[i] = w;
                lineSum += w;
            }
            for (var i = 0; i < size; i++)
            {
                line[i] /= lineSum;
            }

            var tmp = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                var rowOff = y * width;
                for (var x = 0; x < width; x++)
                {
                    var acc = 0.0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        acc += src[rowOff + Math.Clamp(x + k, 0, width - 1)] * line[k + radius];
                    }
                    tmp[rowOff + x] = (float)acc;
                }
            }
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var acc = 0.0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        acc += tmp[(Math.Clamp(y + k, 0, height - 1) * width) + x] * line[k + radius];
                    }
                    dst[(y * width) + x] = (float)acc;
                }
            }
        }

        /// <summary>
        /// The kernel's own FWHM measured back off its weights by walking the +Y profile to its
        /// half-maximum crossing. Used by the tests to prove that what a factory was asked for is what
        /// it built, which is the one property a labelled training pair rests on.
        /// <para>+Y is the minor axis only at position angle 0 (and any axis at all when circular), so
        /// this reports the stated FWHM for those and something between the two axes otherwise.</para>
        /// </summary>
        public double MeasureFwhm()
        {
            var radius = Radius;
            var size = Size;
            var peak = _weights[(radius * size) + radius];
            var half = peak / 2.0;
            // Walk out along +Y (the minor axis when the kernel is elongated along its own +X).
            for (var r = 1; r <= radius; r++)
            {
                var here = _weights[((radius + r) * size) + radius];
                if (here <= half)
                {
                    var prev = _weights[((radius + r - 1) * size) + radius];
                    var t = (prev - half) / (prev - here);
                    return 2.0 * (r - 1 + t);
                }
            }
            return 2.0 * radius;
        }

        /// <summary>The weights as an immutable array, for a caller that wants to keep or compare one.</summary>
        public ImmutableArray<float> ToImmutable() => ImmutableCollectionsMarshal.AsImmutableArray((float[])_weights.Clone());
    }
}
