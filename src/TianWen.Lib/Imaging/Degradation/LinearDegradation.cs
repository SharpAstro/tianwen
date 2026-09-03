using System;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Degradation
{
    /// <summary>
    /// Degrading a LINEAR frame: blur it, then add noise at a level that depends on the signal. Both
    /// halves of the shared degradation exporter (docs/plans/model-training-roadmap.md section 1 item 3)
    /// bottom out here, and both are only correct in linear units, which is the whole reason the
    /// exporter works from retained masters rather than from the stretched P0 tiles.
    ///
    /// <para><b>Order is not a detail: blur first, noise after.</b> A real frame is blurred by the
    /// atmosphere and optics BEFORE the sensor counts photons, so its noise is not blurred. Adding
    /// noise first and convolving would hand the net a correlated noise field that no real frame has,
    /// and it would learn to undo the convolution from the noise rather than from the stars.</para>
    /// </summary>
    public static class LinearDegradation
    {
        /// <summary>
        /// How noisy one SUB of this session is, as a function of signal, calibrated from the master
        /// itself. There is no electron-domain constant to look up here: the retained masters carry no
        /// <c>EGAIN</c> card (the writer stamps a subset of cards, and the archive's own per-channel ADU
        /// scale is unresolved for one camera), so an assumed gain would silently set every injected
        /// level in the programme.
        ///
        /// <para><b>What is measured instead:</b> the master's own background noise, which
        /// <c>sqrt(StackedFrames)</c> converts into one sub's, and the shot-noise statement that
        /// VARIANCE grows linearly with collected signal. Together those give a per-pixel sigma without
        /// a gain: the gain cancels between the two.</para>
        /// </summary>
        /// <param name="PedestalAdu">The frame's zero point (<see cref="Image.Pedestal"/>), in image units.</param>
        /// <param name="BackgroundAdu">Robust background level of the region, image units.</param>
        /// <param name="OneSubSigmaAdu">One sub's noise sigma AT the background level, image units.</param>
        /// <param name="StackedFrames">Frames integrated into the master this was measured on.</param>
        public readonly record struct NoiseCalibration(
            double PedestalAdu,
            double BackgroundAdu,
            double OneSubSigmaAdu,
            int StackedFrames)
        {
            /// <summary>Below this fraction of the background variance the shot term is dominated by
            /// read noise and a dark-sky pixel does not get quieter; the floor stands in for it.</summary>
            public const double MinVarianceFraction = 0.25;

            /// <summary>A hard ceiling on the variance ramp, so a saturated core cannot ask for a noise
            /// sigma larger than the frame.</summary>
            public const double MaxVarianceFactor = 1000.0;

            /// <summary>
            /// The calibration anchored on a REAL SUB's measured noise, which is the honest one and the
            /// one the plan asks for: the training manifest already carries a per-cell <c>NoiseMad</c>
            /// for every exported sub, and a sub's noise is what "one sub" means.
            /// </summary>
            /// <remarks>
            /// Two conversions make it usable. The manifest's number is a STRETCHED-domain MAD, so it is
            /// divided by the MTF's local slope at the background to land in linear units
            /// (<see cref="Image.MidtonesTransferFunctionSlope"/>); the master and its subs are on one
            /// linear scale by construction (the master is integrated unnormalised), so they share a
            /// midtones balance and one slope serves both.
            /// <para>Why not measure the master instead: a MAD over a cell reads structure as noise, and
            /// on a MASTER the noise is small enough that a modest gradient dominates it. Measured on
            /// this project's own synthetic fixture the master-MAD anchor overstated one sub's noise by
            /// seven times, which would have injected seven times too much noise into every pair while
            /// looking entirely reasonable in the row. A sub carries sqrt(N) more noise than its master,
            /// so the same contamination matters an order of magnitude less there.</para>
            /// </remarks>
            /// <param name="region">The cell, linear, for its background level.</param>
            /// <param name="pedestal">The frame's zero point.</param>
            /// <param name="subNoiseMadStretched">Median of the cell's sub <c>NoiseMad</c> values.</param>
            /// <param name="midtonesBalance">The target frame's midtones balance for this channel.</param>
            /// <param name="origMin">The target frame's subtracted minimum for this channel.</param>
            /// <param name="stackedFrames">Frames integrated into the master.</param>
            public static NoiseCalibration FromStretchedSubNoise(
                ReadOnlySpan<float> region,
                double pedestal,
                double subNoiseMadStretched,
                double midtonesBalance,
                double origMin,
                int stackedFrames)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stackedFrames);
                var scratch = region.ToArray();
                var (median, _) = StatisticsHelper.MedianAndMad(scratch.AsSpan());
                var slope = Image.MidtonesTransferFunctionSlope(midtonesBalance, median - origMin);
                var oneSub = slope > 0 ? 1.4826 * subNoiseMadStretched / slope : 0.0;
                return new NoiseCalibration(pedestal, median, oneSub, stackedFrames);
            }

            /// <summary>
            /// Measures the calibration from one linear region (a cell, matching the convention P0's
            /// <c>NoiseMad</c> already uses: a plain MAD of the region). The fallback for a cell whose
            /// subs are not in the manifest; prefer <see cref="FromStretchedSubNoise"/>.
            /// </summary>
            /// <remarks>
            /// A MAD over a region carrying nebulosity reads that structure as noise and so
            /// OVERSTATES sigma; the adjacent-difference estimator does not, but on a stacked frame it
            /// UNDERSTATES it because the stack's noise is spatially correlated. Neither is free, and
            /// the choice here is the one that matches the manifest column every other consumer already
            /// conditions on. <see cref="AdjacentDifferenceSigma"/> reports the other one so a row can
            /// carry both and the bias stays visible rather than assumed away.
            /// </remarks>
            public static NoiseCalibration Measure(ReadOnlySpan<float> region, double pedestal, int stackedFrames)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stackedFrames);
                var scratch = region.ToArray();
                var (median, mad) = StatisticsHelper.MedianAndMad(scratch.AsSpan());
                var masterSigma = 1.4826 * mad;
                // The master averages N subs, so a sub is sqrt(N) noisier. This is the one place the
                // depth of the integration enters, and it is why STACK_N has to survive onto the
                // retained master rather than being recomputed from a file count.
                var oneSub = masterSigma * Math.Sqrt(stackedFrames);
                return new NoiseCalibration(pedestal, median, oneSub, stackedFrames);
            }

            /// <summary>
            /// One sub's noise sigma at <paramref name="signalAdu"/>, scaled by <paramref name="depthScale"/>
            /// (1.0 = a single sub, 1/sqrt(N) = the master's own depth).
            /// </summary>
            public double SigmaAt(double signalAdu, double depthScale)
            {
                var above = signalAdu - PedestalAdu;
                var backgroundAbove = BackgroundAdu - PedestalAdu;
                var ratio = backgroundAbove > 0 ? above / backgroundAbove : 1.0;
                if (!double.IsFinite(ratio))
                {
                    ratio = 1.0;
                }
                var variance = Math.Clamp(ratio, MinVarianceFraction, MaxVarianceFactor);
                return depthScale * OneSubSigmaAdu * Math.Sqrt(variance);
            }

            /// <summary>The structure-insensitive noise estimate, for the diagnostic column: MAD of the
            /// horizontal pixel-to-pixel difference over sqrt(2).</summary>
            public static double AdjacentDifferenceSigma(ReadOnlySpan<float> region, int width, int height)
            {
                if (width < 2 || height < 1)
                {
                    return double.NaN;
                }
                var diffs = new float[(width - 1) * height];
                var k = 0;
                for (var y = 0; y < height; y++)
                {
                    var row = y * width;
                    for (var x = 1; x < width; x++)
                    {
                        diffs[k++] = region[row + x] - region[row + x - 1];
                    }
                }
                var (_, mad) = StatisticsHelper.MedianAndMad(diffs.AsSpan());
                return 1.4826 * mad / Math.Sqrt(2.0);
            }
        }

        /// <summary>
        /// Adds noise to a linear plane in place: a unit-variance <paramref name="shape"/> field scaled
        /// per pixel by <see cref="NoiseCalibration.SigmaAt"/>.
        /// </summary>
        /// <param name="plane">The linear region, modified in place.</param>
        /// <param name="shape">Unit-variance noise field of the same length (<see cref="NoiseField"/>).</param>
        /// <param name="calibration">The session's measured noise calibration.</param>
        /// <param name="depthScale">Injected level as a multiple of one sub's noise.</param>
        public static void AddNoiseInPlace(Span<float> plane, ReadOnlySpan<float> shape, in NoiseCalibration calibration, double depthScale)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(shape.Length, plane.Length);
            for (var i = 0; i < plane.Length; i++)
            {
                var v = plane[i];
                if (float.IsNaN(v))
                {
                    continue;
                }
                plane[i] = (float)(v + (shape[i] * calibration.SigmaAt(v, depthScale)));
            }
        }

        /// <summary>
        /// Blurs a linear region with <paramref name="kernel"/> and then adds noise, which is the whole
        /// degradation for the deconvolver's pairs.
        /// </summary>
        public static float[] BlurThenNoise(
            ReadOnlySpan<float> region,
            int width,
            int height,
            PsfKernel kernel,
            ReadOnlySpan<float> shape,
            in NoiseCalibration calibration,
            double depthScale)
        {
            var blurred = kernel.Convolve(region, width, height);
            AddNoiseInPlace(blurred, shape, calibration, depthScale);
            return blurred;
        }
    }
}
