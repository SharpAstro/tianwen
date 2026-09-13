using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// Measures the SHAPE of a frame's point-spread function: the stacked radial profile of its
    /// stars, and the Moffat exponent that describes its wings.
    ///
    /// <para><b>Why the wings and not just the width.</b> The deconvolver's synthetic-PSF sweep has
    /// to generate degradation that looks like this archive, and a width alone does not pin a
    /// profile. Measured on real session masters, the wings carry 39x to 160x the flux a Gaussian of
    /// the same FWHM predicts at twice the FWHM out, so a Gaussian PSF is not an approximation here,
    /// it is the wrong function. What the wings cost is ringing and halo, which is precisely the
    /// artefact class a deconvolver is judged on.</para>
    /// </summary>
    public static class PsfProfileFit
    {
        /// <summary>Radial bin width in pixels. Quarter-pixel bins resolve a ~2.5 px FWHM core
        /// without leaving the wing bins too sparse to take a median over.</summary>
        private const double BinWidth = 0.25;

        /// <summary>Radial bins, i.e. profile sampled out to <c>Bins * BinWidth</c> = 12 px.</summary>
        private const int Bins = 48;

        /// <summary>
        /// The Moffat is fitted over the bins where the stacked profile is above this fraction of the
        /// peak, about two FWHM: the core, which is what a deconvolution kernel is built from. The
        /// fainter wing is reported beside it (<see cref="Result.WingAt2Fwhm"/>,
        /// <see cref="Result.WingAt3Fwhm"/>) rather than fitted.
        /// </summary>
        /// <remarks>
        /// <para>Until E1g-2 (2026-09-07) the fit ran over every bin above 0.2 percent of the peak, out to
        /// 12 px, equal weight per bin in log space, and it REFUSED every sharp input: the R1 Lanczos
        /// master at 2.3 px (log rms 0.83), a two-frame stack (0.76), a VNG sub at 1.8 px (0.77). Their
        /// stacked star is Gaussian to within 0.02 at every bin out to 2 px and then carries a wing of
        /// half a percent to two percent from 3 to 5 px, and no Moffat with the half-maximum width fixed
        /// follows both across three decades: the exponent that reaches the wing overshoots the core
        /// (0.159 where the profile has 0.106 at 2.1 px), the one that fits the core has no wing, and
        /// the search settled between them. Blurrier masters sit closer to the family and passed, so
        /// the estimator step was refusing exactly the frames the deconvolver most wants to measure,
        /// and the exponents it accepted leaned toward the wing. A floor relative to the profile's own
        /// outer level was tried first and withdrawn within the hour (E1g): the per-star annulus already
        /// leaves the outer bins at 0.03 to 0.07 percent, so it changed no bin, and on a detached halo
        /// it turned a <see cref="Refusal.PoorFit"/> into <see cref="Refusal.TooFewFitBins"/>. The
        /// <see cref="Diagnostics.Profile"/> is exposed so the shape is read rather than inferred.
        /// docs/plans/deconvolver-training.md, E1g and E1g-2.</para>
        /// </remarks>
        private const double CoreFitFloor = 0.02;

        /// <summary>
        /// Largest log-space residual a reported Moffat may have. Above it <see cref="Measure"/>
        /// returns <see langword="null"/> rather than a shape nobody should use.
        ///
        /// <para>Set from the measured separation, not by taste: repeat measurements on real masters
        /// give 0.07 to 0.22 when the fit describes the profile and 0.77 to 0.98 when it does not, so
        /// anything in between is already an order of magnitude from healthy. The failures are also
        /// self-identifying in a second way, which is why one threshold is enough: they come with beta
        /// collapsed toward the bottom of the search grid.</para>
        /// </summary>
        private const double MaxAcceptableLogRms = 0.5;

        /// <summary>Nothing else detected within this radius, so the wings being fitted belong to
        /// the star rather than to a neighbour.</summary>
        private const double IsolationRadius = 16.0;

        /// <summary>Annulus used for the local background, outside the profile but inside the
        /// isolation radius.</summary>
        private const double BackgroundInner = 12.0;
        private const double BackgroundOuter = 15.0;

        /// <summary>
        /// One frame's measured PSF shape.
        /// </summary>
        /// <param name="Fwhm">FWHM of the STACKED profile, in pixels. Deliberately distinct from a
        /// median of per-star FWHM: it is brightness-controlled and measured once on a
        /// high-signal-to-noise stack, so it does not carry the brightness bias described below.</param>
        /// <param name="MoffatBeta">Best-fit Moffat exponent, where the profile is
        /// <c>(1 + (r/alpha)^2)^-beta</c>. LOWER beta means HEAVIER wings; beta to infinity is a
        /// Gaussian.</param>
        /// <param name="MoffatLogRms">Log-space residual of that fit.</param>
        /// <param name="GaussianLogRms">Log-space residual of a Gaussian of the same FWHM, for
        /// comparison. Moffat winning by a wide margin is the expected result; the two being close
        /// would mean this frame really is Gaussian-cored.</param>
        /// <param name="StarsStacked">How many stars went into the stack.</param>
        /// <param name="WingAt2Fwhm">The stacked profile at two FWHM from the centre, as a fraction of
        /// the peak (interpolated between bins; NaN beyond the sampled 12 px). The wing the core fit does
        /// not reach: a Gaussian of the same width would put 6e-5 here, a beta-4 Moffat 0.7 percent.</param>
        /// <param name="WingAt3Fwhm">The same at three FWHM.</param>
        public sealed record Result(
            double Fwhm,
            double MoffatBeta,
            double MoffatLogRms,
            double GaussianLogRms,
            int StarsStacked,
            double WingAt2Fwhm = double.NaN,
            double WingAt3Fwhm = double.NaN);

        /// <summary>How the stars to stack are chosen from the detections.</summary>
        public enum StarSelection
        {
            /// <summary>The 55th to 75th percentile of the frame's own peak distribution, brightest first
            /// up to the cap. The archive survey (E0) was measured with this and it stays the default.</summary>
            PercentileBand,

            /// <summary>Every star whose peak stands at least <see cref="SignalFloorMads"/> background
            /// MADs over the frame median, excluding the brightest percent as a clipping guard, brightest
            /// first up to the cap. The E1c probe found the percentile band is what refuses a RICH field:
            /// with 4,600 to 7,000 detections the 60th-percentile star is faint, its wings reach the noise
            /// floor within a few pixels, and the log-space fit over the remaining 15 to 28 bins reads a
            /// residual of 0.76 to 1.29 where a healthy fit reads 0.07 to 0.22. An absolute floor stacks
            /// the same bright isolated stars on a rich field as on a sparse one.</summary>
            SignalFloor,
        }

        /// <summary>The <see cref="StarSelection.SignalFloor"/> bar, in background MADs over the frame
        /// median. Fifty is ten times the detector's floor and, on the archive's masters, keeps a star's
        /// wings above the profile's own noise floor out to the radii the fit needs; chosen, not tuned.</summary>
        public const double SignalFloorMads = 50.0;

        /// <summary>Why <see cref="Measure(Image, int, IReadOnlyCollection{ImagedStar}, out Diagnostics, int, StarSelection)"/>
        /// answered null, in the order the checks run. <see cref="None"/> is a measurement.</summary>
        public enum Refusal
        {
            /// <summary>A profile was fitted and reported.</summary>
            None,

            /// <summary>Fewer than 40 stars were offered at all.</summary>
            TooFewStars,

            /// <summary>Fewer than 40 of the brightness band's stars survived the edge, isolation and
            /// local-background checks to be stacked.</summary>
            TooFewStacked,

            /// <summary>The stacked profile never fell through half its peak inside the sampled radius.</summary>
            NoHalfMaximum,

            /// <summary>Fewer than eight radial bins sat above the noise floor, too few to fit a shape.</summary>
            TooFewFitBins,

            /// <summary>The best Moffat's log-space residual exceeded the acceptance bound, so no shape
            /// describes the stack and the minimising beta would be an artefact.</summary>
            PoorFit,
        }

        /// <summary>
        /// What the measurement saw on the way to its answer, whichever way it went. Exists because the
        /// null return said nothing for two years and then a deconvolution probe (E1b) found the fit
        /// refusing half its rows on noisy narrowband frames with no way to say which of the five
        /// checks was firing; the counts here are the ones each check tests.
        /// </summary>
        /// <param name="Refusal">Which check refused, or <see cref="Refusal.None"/>.</param>
        /// <param name="StarsOffered">Stars handed in.</param>
        /// <param name="InBrightnessBand">Stars inside the 55th to 75th percentile peak band.</param>
        /// <param name="Stacked">Band stars that passed the edge, isolation and background checks and
        /// were accumulated.</param>
        /// <param name="FitBins">Radial bins above the noise floor the fit used, or 0 before that point.</param>
        /// <param name="Fwhm">The stacked profile's half-maximum width, or NaN before that point.</param>
        /// <param name="MoffatBeta">The best-fit exponent, whether or not it was accepted, or NaN before
        /// that point.</param>
        /// <param name="MoffatLogRms">That fit's log-space residual, or NaN before that point.</param>
        /// <param name="Profile">The stacked profile itself, one median per quarter-pixel bin from the
        /// centre out to 12 px, normalised to each star's peak, or null before it was stacked. What a
        /// refusal was refusing, so a probe can print it beside the model.</param>
        /// <param name="Floor">The level a bin had to clear to be fitted (the larger of the fixed floor and
        /// the wing-residue multiple of the profile's outer level), or NaN before that point.</param>
        /// <param name="FittedBins">The indices into <paramref name="Profile"/> the fit used, or null.</param>
        public sealed record Diagnostics(
            Refusal Refusal,
            int StarsOffered,
            int InBrightnessBand,
            int Stacked,
            int FitBins,
            double Fwhm,
            double MoffatBeta,
            double MoffatLogRms,
            double[]? Profile = null,
            double Floor = double.NaN,
            IReadOnlyList<int>? FittedBins = null)
        {
            /// <summary>Radial bin width in pixels of <see cref="Profile"/>.</summary>
            public double BinWidthPx => BinWidth;
        }

        /// <summary>
        /// Stacks the radial profiles of isolated, brightness-controlled stars and fits a Moffat to
        /// the result. Returns null when the frame cannot support a measurement (too few usable
        /// stars, or a stack with no half-maximum crossing); the overload with a
        /// <see cref="Diagnostics"/> parameter says which.
        /// </summary>
        /// <remarks>
        /// <para><b>Brightness is controlled, and that is not optional.</b> Measured FWHM depends
        /// strongly on how bright the star is: across peak-ADU deciles on real masters it runs
        /// 2.613 -> 1.914 px, so faint stars read 25-30% WIDER than bright ones in the same frame.
        /// The mechanism is that the half-maximum level is set relative to the star's OWN peak, so
        /// whatever background survives subtraction is a larger fraction of a faint star's peak and
        /// pushes the crossing outward. Stacking whatever the detector returned would therefore
        /// measure the frame's magnitude distribution as much as its optics.</para>
        /// <para><b>The fit is in LOG space, and that changes the answer.</b> An unweighted
        /// least-squares fit on a peak-normalised profile is dominated by the core, where the values
        /// are near 1, and effectively ignores the wings, where they are near 0.001. Fitting the
        /// same three real masters both ways flipped the verdict from "Gaussian fits better" to
        /// "Moffat fits better" in every one, by a factor of 4 to 15 in residual. Since the wings are
        /// the reason to measure a PSF at all, weighting each decade equally is the honest choice.</para>
        /// <para><b>Alpha is tied to the measured FWHM rather than fitted.</b> The width is already
        /// known from the stack's own half-maximum crossing, so letting alpha float would trade
        /// width against exponent and report a shape that only fits because it also moved the width.
        /// Constraining it makes beta answer one question: how heavy are the wings for THIS
        /// width.</para>
        /// </remarks>
        /// <param name="image">Frame to measure.</param>
        /// <param name="channel">Channel index; callers use 0 to match the rest of the PSF report.</param>
        /// <param name="stars">Already-detected stars for this channel.</param>
        /// <param name="maxStars">Cap on stars stacked; the profile converges well before this.</param>
        public static Result? Measure(
            Image image,
            int channel,
            IReadOnlyCollection<ImagedStar> stars,
            int maxStars = 400)
            => Measure(image, channel, stars, out _, maxStars);

        /// <inheritdoc cref="Measure(Image, int, IReadOnlyCollection{ImagedStar}, int)"/>
        /// <param name="diagnostics">What the measurement saw, and which check refused when it did.</param>
        /// <param name="selection">Which stars are stacked; the percentile band unless a caller has a
        /// reason (see <see cref="StarSelection.SignalFloor"/>).</param>
        /// <summary>How the qualifying stars are ordered before the brightest <c>maxStars</c> are stacked.</summary>
        public enum StackRanking
        {
            /// <summary>By the star's peak sample (the shipped rule).</summary>
            Peak,

            /// <summary>By the detector's flux. A warm photosite has the peak of a bright star and the
            /// flux of a faint one, so a flux ranking pushes the class the share guard does not reach
            /// (docs/known-limitations.md, the detector entry) to the bottom of the stack instead of
            /// the top. A candidate beside <see cref="SpikeGuard.NeighbourSignificance"/> for the
            /// pre-registered readout.</summary>
            Flux,
        }

        public static Result? Measure(
            Image image,
            int channel,
            IReadOnlyCollection<ImagedStar> stars,
            out Diagnostics diagnostics,
            int maxStars = 400,
            StarSelection selection = StarSelection.PercentileBand,
            StackRanking ranking = StackRanking.Peak)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(stars);

            var (_, width, height) = image.Shape;
            if (stars.Count < 40)
            {
                diagnostics = new Diagnostics(Refusal.TooFewStars, stars.Count, 0, 0, 0, double.NaN, double.NaN, double.NaN);
                return null;
            }

            // Copied once rather than held as a span: the accumulation below spans several helper
            // calls and a span cannot cross them (CS8175), and one copy per frame is negligible
            // beside the star detection that produced the input.
            var plane = image.GetChannelSpan(channel).ToArray();
            var starArray = stars.ToArray();

            var peaks = new float[starArray.Length];
            for (var i = 0; i < starArray.Length; i++)
            {
                peaks[i] = PeakNear(plane, width, height, starArray[i].XCentroid, starArray[i].YCentroid);
            }

            // A band around the middle of the brightness distribution: bright enough that the
            // background residue is a small fraction of the peak, faint enough to be far from any
            // clipping, and populous enough to stack.
            float lowPeak;
            float highPeak;
            if (selection == StarSelection.SignalFloor)
            {
                var (frameMedian, frameMad) = FrameBackground(plane);
                lowPeak = frameMedian + (float)(SignalFloorMads * frameMad);
                highPeak = Percentile(peaks, 0.99);
            }
            else
            {
                lowPeak = Percentile(peaks, 0.55);
                highPeak = Percentile(peaks, 0.75);
            }

            var samples = new List<float>[Bins];
            for (var b = 0; b < Bins; b++)
            {
                samples[b] = new List<float>();
            }

            // Qualifying stars are collected FIRST and then sampled deterministically, rather than
            // taking the first maxStars the input happens to yield.
            //
            // This was a real defect, not tidiness. Repeating the measurement on bit-identical pixels
            // (max abs diff exactly 0 between two independent drizzle runs) gave FWHM stable to
            // +/-0.02 px but beta swinging 5.20 / 5.25 / 2.45 with the residual jumping 0.07 / 0.10 /
            // 0.97, about one run in three, because star DETECTION returns the same stars in a
            // different order and this loop then stacked a different 400 of them. FWHM survives that
            // (it comes from the high-signal half-maximum crossing) but the fit does not: the outer
            // bins sit near NoiseFloor, so a slightly different subset flips a marginal bin into or
            // out of fitBins below, and that bin's log-residual dominates the sum. Ordering by peak
            // and striding also removes a second hazard the old form had: with more candidates than
            // maxStars it took a PREFIX of the detection order, which is spatially correlated, so the
            // stack could be weighted toward one part of a field whose PSF varies with field radius.
            var candidates = new List<int>();
            for (var i = 0; i < starArray.Length; i++)
            {
                if (peaks[i] >= lowPeak && peaks[i] <= highPeak)
                {
                    candidates.Add(i);
                }
            }
            candidates.Sort((a, b) =>
            {
                var cmp = ranking is StackRanking.Flux
                    ? starArray[b].Flux.CompareTo(starArray[a].Flux)
                    : peaks[b].CompareTo(peaks[a]);
                if (cmp != 0) return cmp;
                cmp = starArray[a].YCentroid.CompareTo(starArray[b].YCentroid);
                return cmp != 0 ? cmp : starArray[a].XCentroid.CompareTo(starArray[b].XCentroid);
            });
            // Brightest first, and take the top maxStars rather than spreading evenly across the band.
            // Measured, because the even spread was tried first and was worse: striding the whole band
            // made the red channel of an emission-nebula master unfittable on BOTH an AHD and a
            // drizzled master (residual over 0.5, so rejected), where taking the bright end fits at
            // 0.07 to 0.2. The band is only the 55th to 75th percentile of peaks, but even inside it
            // SNR matters: whatever background survives the annulus subtraction is a larger fraction
            // of a fainter star's peak, which is the same mechanism that makes faint stars measure
            // 25-30% wider, and in the wings that residue is what a log-space fit sees.
            var stacked = 0;
            for (var k = 0; k < candidates.Count && stacked < maxStars; k++)
            {
                var i = candidates[k];
                var sx = starArray[i].XCentroid;
                var sy = starArray[i].YCentroid;
                if (sx < IsolationRadius || sy < IsolationRadius
                    || sx > width - IsolationRadius - 1 || sy > height - IsolationRadius - 1)
                {
                    continue;
                }
                if (!IsIsolated(starArray, i, sx, sy))
                {
                    continue;
                }
                if (!TryLocalBackground(plane, width, height, sx, sy, out var background))
                {
                    continue;
                }

                var amplitude = peaks[i] - background;
                if (amplitude <= 0f)
                {
                    continue;
                }

                Accumulate(plane, width, height, sx, sy, background, amplitude, samples);
                stacked++;
            }

            if (stacked < 40)
            {
                diagnostics = new Diagnostics(Refusal.TooFewStacked, starArray.Length, candidates.Count, stacked, 0, double.NaN, double.NaN, double.NaN);
                return null;
            }

            var profile = new double[Bins];
            var radii = new double[Bins];
            for (var b = 0; b < Bins; b++)
            {
                radii[b] = (b + 0.5) * BinWidth;
                profile[b] = samples[b].Count > 0 ? Median(samples[b]) : double.NaN;
            }

            var fwhm = HalfMaximumWidth(profile, radii);
            if (double.IsNaN(fwhm) || fwhm <= 0)
            {
                diagnostics = new Diagnostics(Refusal.NoHalfMaximum, starArray.Length, candidates.Count, stacked, 0, double.NaN, double.NaN, double.NaN);
                return null;
            }

            var floor = CoreFitFloor;
            var fitBins = new List<int>();
            for (var b = 0; b < Bins; b++)
            {
                if (!double.IsNaN(profile[b]) && profile[b] > floor)
                {
                    fitBins.Add(b);
                }
            }
            if (fitBins.Count < 8)
            {
                diagnostics = new Diagnostics(Refusal.TooFewFitBins, starArray.Length, candidates.Count, stacked, fitBins.Count, fwhm, double.NaN, double.NaN, profile, floor, fitBins);
                return null;
            }

            var (beta, moffatRms) = FitMoffatBeta(profile, radii, fitBins, fwhm);
            diagnostics = new Diagnostics(
                moffatRms > MaxAcceptableLogRms ? Refusal.PoorFit : Refusal.None,
                starArray.Length, candidates.Count, stacked, fitBins.Count, fwhm, beta, moffatRms, profile, floor, fitBins);
            if (moffatRms > MaxAcceptableLogRms)
            {
                // Refuse rather than report. The beta search is an exhaustive grid from 1 to 25, so a
                // large residual is never a search that got stuck: it means the STACKED PROFILE could
                // not be described by any Moffat, and the beta minimising it is then a fitting
                // artifact rather than a shape. Measured on real masters, a converged fit lands at
                // 0.07 to 0.22 while these land at 0.77 to 0.98, and the bad ones come with beta
                // collapsed near the bottom of the grid, so the number looks like a plausible
                // heavy-winged PSF while being meaningless.
                //
                // A silently reported one is worse than none, and had already done damage: the
                // archive-wide beta survey was one measurement per master, so an unknown share of it
                // is failure draws, which is what "the plan's assumed beta 2.5-4.5 is wrong for the
                // sessions that dominate" was partly built on. Callers already handle null (the
                // report prints "not measured for this train"), and this keeps the OTHER channels of
                // the same session, which are fitted independently.
                return null;
            }
            var gaussRms = GaussianLogRms(profile, radii, fitBins, fwhm);
            return new Result(fwhm, beta, moffatRms, gaussRms, stacked, ProfileAt(profile, 2 * fwhm), ProfileAt(profile, 3 * fwhm));
        }

        /// <summary>The stacked profile at radius <paramref name="r"/> px, interpolated linearly
        /// between bin centres; NaN outside the sampled range or where a bin is empty.</summary>
        private static double ProfileAt(double[] profile, double r)
        {
            var position = (r / BinWidth) - 0.5;
            if (position < 0 || position >= profile.Length - 1)
            {
                return double.NaN;
            }

            var b = (int)position;
            var t = position - b;
            var lo = profile[b];
            var hi = profile[b + 1];
            return double.IsNaN(lo) || double.IsNaN(hi) ? double.NaN : lo + (t * (hi - lo));
        }

        private static bool IsIsolated(ImagedStar[] stars, int self, float sx, float sy)
        {
            var r2 = IsolationRadius * IsolationRadius;
            for (var j = 0; j < stars.Length; j++)
            {
                if (j == self)
                {
                    continue;
                }
                var dx = stars[j].XCentroid - sx;
                var dy = stars[j].YCentroid - sy;
                if (dx * dx + dy * dy < r2)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryLocalBackground(
            float[] plane, int width, int height, float sx, float sy, out float background)
        {
            var ring = new List<float>();
            var inner = BackgroundInner * BackgroundInner;
            var outer = BackgroundOuter * BackgroundOuter;
            var cx = (int)sx;
            var cy = (int)sy;
            for (var dy = -(int)BackgroundOuter; dy <= (int)BackgroundOuter; dy++)
            {
                for (var dx = -(int)BackgroundOuter; dx <= (int)BackgroundOuter; dx++)
                {
                    var d2 = dx * dx + dy * dy;
                    if (d2 < inner || d2 > outer)
                    {
                        continue;
                    }
                    var x = cx + dx;
                    var y = cy + dy;
                    if (x < 0 || y < 0 || x >= width || y >= height)
                    {
                        continue;
                    }
                    ring.Add(plane[y * width + x]);
                }
            }
            if (ring.Count < 40)
            {
                background = 0f;
                return false;
            }
            background = Median(ring);
            return true;
        }

        private static void Accumulate(
            float[] plane, int width, int height, float sx, float sy,
            float background, float amplitude, List<float>[] samples)
        {
            var reach = (int)Math.Ceiling(Bins * BinWidth * 0.5) + 1;
            var cx = (int)Math.Round(sx);
            var cy = (int)Math.Round(sy);
            for (var dy = -reach; dy <= reach; dy++)
            {
                for (var dx = -reach; dx <= reach; dx++)
                {
                    var x = cx + dx;
                    var y = cy + dy;
                    if (x < 0 || y < 0 || x >= width || y >= height)
                    {
                        continue;
                    }
                    // Radius from the SUB-PIXEL centroid, not from the rounded centre, or the
                    // innermost bins smear by up to half a pixel.
                    var ddx = x - sx;
                    var ddy = y - sy;
                    var b = (int)(Math.Sqrt(ddx * ddx + ddy * ddy) / BinWidth);
                    if (b >= Bins)
                    {
                        continue;
                    }
                    samples[b].Add((plane[y * width + x] - background) / amplitude);
                }
            }
        }

        /// <summary>Interpolated half-maximum crossing of the peak-normalised stack, doubled.</summary>
        private static double HalfMaximumWidth(double[] profile, double[] radii)
        {
            for (var b = 1; b < profile.Length; b++)
            {
                if (double.IsNaN(profile[b]) || double.IsNaN(profile[b - 1]))
                {
                    continue;
                }
                if (profile[b] < 0.5 && profile[b - 1] >= 0.5)
                {
                    var drop = profile[b - 1] - profile[b];
                    var hwhm = drop > 0
                        ? radii[b - 1] + BinWidth * (profile[b - 1] - 0.5) / drop
                        : radii[b - 1];
                    return 2 * hwhm;
                }
            }
            return double.NaN;
        }

        private static (double Beta, double Rms) FitMoffatBeta(
            double[] profile, double[] radii, List<int> fitBins, double fwhm)
        {
            var bestBeta = 0.0;
            var bestRms = double.MaxValue;
            // 1 to 25 covers heavy-winged through effectively Gaussian; the real archive lands
            // between about 5 and 11, so both ends are comfortably outside the observed range.
            for (var beta = 1.0; beta <= 25.0; beta += 0.05)
            {
                var alpha = AlphaFor(fwhm, beta);
                var se = 0.0;
                foreach (var b in fitBins)
                {
                    var model = Math.Pow(1 + (radii[b] * radii[b]) / (alpha * alpha), -beta);
                    var d = Math.Log(model) - Math.Log(profile[b]);
                    se += d * d;
                }
                var rms = Math.Sqrt(se / fitBins.Count);
                if (rms < bestRms)
                {
                    bestRms = rms;
                    bestBeta = beta;
                }
            }
            return (bestBeta, bestRms);
        }

        /// <summary>Moffat FWHM is <c>2*alpha*sqrt(2^(1/beta) - 1)</c>; inverted so a candidate beta
        /// reproduces the measured width exactly and only its shape is under test.</summary>
        private static double AlphaFor(double fwhm, double beta)
            => fwhm / (2 * Math.Sqrt(Math.Pow(2, 1.0 / beta) - 1));

        private static double GaussianLogRms(double[] profile, double[] radii, List<int> fitBins, double fwhm)
        {
            var sigma = fwhm / 2.3548200450309493; // FWHM = 2*sqrt(2*ln2)*sigma
            var se = 0.0;
            foreach (var b in fitBins)
            {
                var model = Math.Exp(-(radii[b] * radii[b]) / (2 * sigma * sigma));
                var d = Math.Log(model) - Math.Log(profile[b]);
                se += d * d;
            }
            return Math.Sqrt(se / fitBins.Count);
        }

        private static float PeakNear(float[] plane, int width, int height, float fx, float fy)
        {
            var x0 = Math.Max(0, (int)fx - 2);
            var x1 = Math.Min(width - 1, (int)fx + 2);
            var y0 = Math.Max(0, (int)fy - 2);
            var y1 = Math.Min(height - 1, (int)fy + 2);
            var peak = float.MinValue;
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var v = plane[y * width + x];
                    if (v > peak)
                    {
                        peak = v;
                    }
                }
            }
            return peak;
        }

        private static float Median(List<float> values)
        {
            var copy = values.ToArray();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }

        private static float Percentile(float[] values, double p)
        {
            var copy = (float[])values.Clone();
            Array.Sort(copy);
            return copy[Math.Clamp((int)(copy.Length * p), 0, copy.Length - 1)];
        }

        /// <summary>Frame median and background MAD from every seventh finite pixel, which stars are
        /// far too sparse to move; the <see cref="StarSelection.SignalFloor"/> bar is set from it.</summary>
        private static (float Median, float Mad) FrameBackground(float[] plane)
        {
            var sample = new List<float>((plane.Length / 7) + 1);
            for (var i = 0; i < plane.Length; i += 7)
            {
                if (float.IsFinite(plane[i]))
                {
                    sample.Add(plane[i]);
                }
            }
            if (sample.Count == 0)
            {
                return (0f, 0f);
            }

            var median = Median(sample);
            var deviations = new List<float>(sample.Count);
            foreach (var v in sample)
            {
                deviations.Add(Math.Abs(v - median));
            }
            return (median, Median(deviations));
        }
    }
}
