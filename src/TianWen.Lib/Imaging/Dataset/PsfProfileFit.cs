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

        /// <summary>Below this the stacked profile is background residue, not signal, and including
        /// it would let the noise floor drive a log-space fit.</summary>
        private const double NoiseFloor = 0.002;

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
        public sealed record Result(
            double Fwhm,
            double MoffatBeta,
            double MoffatLogRms,
            double GaussianLogRms,
            int StarsStacked);

        /// <summary>
        /// Stacks the radial profiles of isolated, brightness-controlled stars and fits a Moffat to
        /// the result. Returns null when the frame cannot support a measurement (too few usable
        /// stars, or a stack with no half-maximum crossing).
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
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(stars);

            var (_, width, height) = image.Shape;
            if (stars.Count < 40)
            {
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
            var lowPeak = Percentile(peaks, 0.55);
            var highPeak = Percentile(peaks, 0.75);

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
                var cmp = peaks[b].CompareTo(peaks[a]);
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
                return null;
            }

            var fitBins = new List<int>();
            for (var b = 0; b < Bins; b++)
            {
                if (!double.IsNaN(profile[b]) && profile[b] > NoiseFloor)
                {
                    fitBins.Add(b);
                }
            }
            if (fitBins.Count < 8)
            {
                return null;
            }

            var (beta, moffatRms) = FitMoffatBeta(profile, radii, fitBins, fwhm);
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
            return new Result(fwhm, beta, moffatRms, gaussRms, stacked);
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
    }
}
