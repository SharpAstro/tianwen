using System;
using TianWen.Lib.Imaging.Degradation;

namespace TianWen.Lib.Imaging.Deconvolution
{
    /// <summary>
    /// Richardson-Lucy deconvolution with a KNOWN kernel: the classical maximum-likelihood iteration
    /// for Poisson data, <c>u_{k+1} = u_k * (P^T [d / (P u_k)])</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>What this is for, stated because it bounds what it may be used for.</b> It exists to
    /// measure the ORACLE ceiling of P2's deconvolver
    /// (<c>docs/plans/deconvolver-training.md</c>, H1): given a synthetic pair whose blur kernel is
    /// known exactly, how much of that blur is recoverable at all, and at what ringing cost. A trained
    /// net that appears to beat this number on the same pair is fabricating detail, not recovering it,
    /// because the oracle is handed the one thing inference never has.</para>
    ///
    /// <para><b>It is NOT a deployable deconvolver, and the gap is the kernel.</b> On a real frame the
    /// kernel is unknown and must be estimated, and an RL run with a kernel that is even slightly wrong
    /// converges confidently to the wrong image. Nothing here estimates a PSF, and this type is
    /// deliberately not wired to <c>INonStellarDeconvolver</c>.</para>
    ///
    /// <para><b>Non-negativity is a precondition, not a preference.</b> The update is multiplicative,
    /// so a negative sample does not merely bias the result, it flips the sign of that pixel's
    /// correction and the iteration walks away. Calibrated astronomical frames carry a pedestal and are
    /// non-negative in the ordinary case, but sky-subtracted or over-subtracted ones are not, so the
    /// input is clamped at zero and the caller is told how much was clamped
    /// (<see cref="Result.ClampedFraction"/>) rather than left to assume none of it was.</para>
    ///
    /// <para><b>Flux is conserved to the extent the edges allow.</b> The kernel sums to one and
    /// <see cref="PsfKernel.Convolve(ReadOnlySpan{float}, int, int)"/> clamps at the border rather than
    /// wrapping or zeroing, so interior flux is preserved by construction while the outermost
    /// <see cref="PsfKernel.Radius"/> pixels are not; measure a crop's interior, never its rim.</para>
    /// </remarks>
    public static class RichardsonLucy
    {
        /// <summary>Below this the denominator is treated as empty and the pixel takes no correction,
        /// rather than dividing by a number that is only nonzero by rounding.</summary>
        private const float DenominatorFloor = 1e-12f;

        /// <summary>One deconvolution: the estimate, and what the run had to do to its input.</summary>
        /// <param name="Estimate">The deconvolved plane, row-major, same dimensions as the input.</param>
        /// <param name="Iterations">Iterations actually run.</param>
        /// <param name="ClampedFraction">Fraction of input samples that were negative and were clamped
        /// to zero. Anything but a small number means the input was not the non-negative image this
        /// iteration assumes, and the result should be distrusted rather than interpreted.</param>
        public readonly record struct Result(float[] Estimate, int Iterations, double ClampedFraction);

        /// <summary>
        /// Deconvolve <paramref name="observed"/> by <paramref name="psf"/>, which must be the kernel
        /// that produced it.
        /// </summary>
        /// <param name="observed">Row-major observed image.</param>
        /// <param name="width">Image width.</param>
        /// <param name="height">Image height.</param>
        /// <param name="psf">The known blur kernel.</param>
        /// <param name="iterations">Number of RL iterations. RL does not converge to a fixed point on
        /// noisy data: it fits the noise, so the iteration count IS a regularisation parameter and the
        /// right value is a measurement, not a default.</param>
        public static Result Deconvolve(
            ReadOnlySpan<float> observed,
            int width,
            int height,
            PsfKernel psf,
            int iterations)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(observed.Length, width * height);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
            ArgumentNullException.ThrowIfNull(psf);

            var n = width * height;
            var estimate = new float[n];
            var clamped = 0;
            for (var i = 0; i < n; i++)
            {
                var v = observed[i];
                if (!float.IsFinite(v) || v < 0f)
                {
                    // A NaN is not a measurement and cannot seed a multiplicative iteration; treating
                    // it as zero keeps it out of the estimate instead of poisoning its whole kernel
                    // footprint on the first convolution.
                    estimate[i] = 0f;
                    clamped++;
                }
                else
                {
                    estimate[i] = v;
                }
            }

            var data = estimate.AsSpan().ToArray();
            var adjoint = psf.Mirrored();
            var blurred = new float[n];
            var ratio = new float[n];
            var correction = new float[n];

            for (var it = 0; it < iterations; it++)
            {
                psf.Convolve(estimate, width, height, blurred);

                for (var i = 0; i < n; i++)
                {
                    var d = blurred[i];
                    ratio[i] = d > DenominatorFloor ? data[i] / d : 1f;
                }

                adjoint.Convolve(ratio, width, height, correction);

                for (var i = 0; i < n; i++)
                {
                    var next = estimate[i] * correction[i];
                    // The iteration is non-negative by construction; the guard is against a rounding
                    // excursion below zero becoming a sign flip that never recovers.
                    estimate[i] = next > 0f && float.IsFinite(next) ? next : 0f;
                }
            }

            return new Result(estimate, iterations, n == 0 ? 0.0 : (double)clamped / n);
        }
    }
}
