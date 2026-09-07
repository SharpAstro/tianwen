using System;

namespace TianWen.Lib.Imaging.Degradation
{
    /// <summary>
    /// The width of one Moffat convolved with another, and its inverse: the kernel width that turns a
    /// clean profile into an observed one.
    ///
    /// <para><b>Why a quadrature of FWHMs is not it.</b> Two Gaussians of FWHM a and b compose to
    /// <c>sqrt(a^2 + b^2)</c>, and that is the rule <c>PsfKernel</c>'s remarks quote for the training
    /// pairs. A Moffat is not a Gaussian: its wings carry flux the core does not, and the convolution's
    /// half-maximum crossing moves LESS than quadrature predicts. Read the other way, as an estimator
    /// does, <c>sqrt(obs^2 - clean^2)</c> over-reads the kernel: by 1.21 to 1.28 for a 2 px beta-4
    /// kernel on the archive's 2.15 to 2.8 px cores, measured numerically 2026-09-07, which is the 1.24
    /// the E1b probe found in its 1.3 to 1.6x band (deconvolver-training.md, E1b's results).</para>
    ///
    /// <para><b>Numeric, not closed-form.</b> The convolution of two Moffats has no closed form in
    /// general, so the observed profile is integrated directly along one radial line, on a 0.1 px grid
    /// out to five times the wider FWHM, and its half-maximum crossing read off with linear
    /// interpolation, the same way <c>PsfProfileFit</c> reads a stacked profile. A few hundred
    /// milliseconds per inversion, which an estimator calls once per frame.</para>
    /// </summary>
    public static class MoffatComposition
    {
        /// <summary>Grid step of the convolution integral and of the radial profile, in pixels.</summary>
        private const double Step = 0.1;

        /// <summary>The integral runs over the kernel's footprint out to this many of the wider FWHM.
        /// A beta-3 Moffat still holds a percent of its flux past it, and that percent is in the far
        /// wings where it moves the half-maximum crossing by nothing the grid can resolve.</summary>
        private const double FootprintFwhms = 5.0;

        /// <summary>Moffat profile, peak 1, in <c>PsfProfileFit</c>'s parameterisation.</summary>
        public static double Profile(double fwhm, double beta, double r)
        {
            var alpha = fwhm / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / beta) - 1.0));
            return Math.Pow(1.0 + ((r * r) / (alpha * alpha)), -beta);
        }

        /// <summary>
        /// FWHM of <c>Moffat(fwhmA, betaA) convolved with Moffat(fwhmB, betaB)</c>.
        /// </summary>
        public static double ComposedFwhm(double fwhmA, double betaA, double fwhmB, double betaB)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fwhmA);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fwhmB);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(betaA);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(betaB);

            // The integral is over the NARROWER profile's footprint (it is the one that can be truncated
            // sooner), with the wider one sampled at the shifted radius; convolution commutes.
            var (kernelFwhm, kernelBeta, wideFwhm, wideBeta) = fwhmA <= fwhmB
                ? (fwhmA, betaA, fwhmB, betaB)
                : (fwhmB, betaB, fwhmA, betaA);

            var reach = FootprintFwhms * Math.Max(kernelFwhm, wideFwhm);
            var n = (int)Math.Ceiling(reach / Step);

            // Both profiles as radius tables, so the inner loop does two lookups and no Pow.
            var tableStep = Step * 0.5;
            var tableLength = (int)Math.Ceiling((3.0 * reach) / tableStep) + 2;
            var kernelTable = new double[tableLength];
            var wideTable = new double[tableLength];
            for (var i = 0; i < tableLength; i++)
            {
                var r = i * tableStep;
                kernelTable[i] = Profile(kernelFwhm, kernelBeta, r);
                wideTable[i] = Profile(wideFwhm, wideBeta, r);
            }

            double Lookup(double[] table, double r)
            {
                var t = r / tableStep;
                var i = (int)t;
                if (i + 1 >= table.Length)
                {
                    return 0.0;
                }

                var f = t - i;
                return table[i] + (f * (table[i + 1] - table[i]));
            }

            // Kernel weights on the grid, once.
            var weights = new double[(2 * n) + 1, (2 * n) + 1];
            for (var iy = -n; iy <= n; iy++)
            {
                for (var ix = -n; ix <= n; ix++)
                {
                    weights[iy + n, ix + n] = Lookup(kernelTable, Math.Sqrt((ix * ix) + (iy * iy)) * Step);
                }
            }

            double Composed(double x)
            {
                var acc = 0.0;
                for (var iy = -n; iy <= n; iy++)
                {
                    var dy = iy * Step;
                    for (var ix = -n; ix <= n; ix++)
                    {
                        var w = weights[iy + n, ix + n];
                        if (w == 0.0)
                        {
                            continue;
                        }

                        var dx = x - (ix * Step);
                        acc += w * Lookup(wideTable, Math.Sqrt((dx * dx) + (dy * dy)));
                    }
                }

                return acc;
            }

            // Walk out from the centre to the half-maximum crossing, interpolating between samples.
            var peak = Composed(0.0);
            var half = peak * 0.5;
            var prev = peak;
            for (var k = 1; k * Step <= reach; k++)
            {
                var here = Composed(k * Step);
                if (here <= half)
                {
                    var t = (prev - half) / (prev - here);
                    return 2.0 * ((k - 1) + t) * Step;
                }

                prev = here;
            }

            return double.NaN;
        }

        /// <summary>
        /// The width of the beta-<paramref name="kernelBeta"/> Moffat that takes a clean profile of
        /// (<paramref name="cleanFwhm"/>, <paramref name="cleanBeta"/>) to <paramref name="observedFwhm"/>,
        /// by bisection on <see cref="ComposedFwhm"/>. NaN when the observed profile is no wider than the
        /// clean one, which is the estimator saying "no blur" and not a width of zero.
        /// </summary>
        /// <param name="tolerancePx">Bisection stops when the bracket is this narrow.</param>
        public static double DifferenceFwhm(double cleanFwhm, double cleanBeta, double observedFwhm, double kernelBeta, double tolerancePx = 0.005)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cleanFwhm);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tolerancePx);
            if (!(observedFwhm > cleanFwhm))
            {
                return double.NaN;
            }

            // The composed width is monotone in the kernel width and never below either input, so the
            // kernel is bracketed by (nothing) and (a width that alone would exceed the observation).
            var lo = 0.0;
            var hi = observedFwhm;
            while (ComposedFwhm(cleanFwhm, cleanBeta, hi, kernelBeta) < observedFwhm)
            {
                hi *= 1.5;
                if (hi > 8.0 * observedFwhm)
                {
                    return double.NaN;
                }
            }

            while (hi - lo > tolerancePx)
            {
                var mid = 0.5 * (lo + hi);
                if (mid <= 0.0)
                {
                    break;
                }

                if (ComposedFwhm(cleanFwhm, cleanBeta, mid, kernelBeta) < observedFwhm)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            return 0.5 * (lo + hi);
        }
    }
}
