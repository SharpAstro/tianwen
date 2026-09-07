using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Coverage for <see cref="PsfProfileFit"/>: it must recover the exponent of a synthetic Moffat
    /// field, and must not mistake a Moffat for a Gaussian. The second is the real risk -- an
    /// unweighted fit reports "Gaussian" for every one of these frames, because the core dominates
    /// and the wings that distinguish them are three orders of magnitude smaller.
    /// </summary>
    public class PsfProfileFitTests
    {
        // Big enough that the fit's brightness band (55th-75th percentile of peaks) still leaves
        // well over its 40-star floor: 17x17 grid positions -> 289 stars -> ~58 in band.
        private const int Size = 700;
        private const int Pitch = 40;
        private const int Inset = 30;
        private const float Background = 200f;
        private const float Amplitude = 3000f;

        [Theory]
        [InlineData(2.5)]
        [InlineData(4.0)]
        [InlineData(7.0)]
        public void Measure_RecoversTheMoffatExponentItWasGiven(double trueBeta)
        {
            const double trueFwhm = 3.2;
            var image = RenderMoffatField(trueFwhm, trueBeta, seed: 7);

            var stars = DetectSyntheticStars(image);
            var result = PsfProfileFit.Measure(image, channel: 0, stars).ShouldNotBeNull();

            result.Fwhm.ShouldBe(trueFwhm, tolerance: 0.35);
            // Beta is a wing-shape parameter fitted over a finite radius, so it is recovered to
            // within a fraction rather than exactly; the sweep only needs the right neighbourhood.
            result.MoffatBeta.ShouldBe(trueBeta, tolerance: Math.Max(1.0, trueBeta * 0.35));
        }

        [Fact]
        public void Measure_PrefersMoffatOverGaussian_OnAMoffatField()
        {
            // beta 3 has wings a Gaussian cannot express. The log-space residual has to say so.
            var image = RenderMoffatField(fwhm: 3.0, beta: 3.0, seed: 11);

            var result = PsfProfileFit.Measure(image, channel: 0, DetectSyntheticStars(image)).ShouldNotBeNull();

            result.MoffatLogRms.ShouldBeLessThan(result.GaussianLogRms);
            result.GaussianLogRms.ShouldBeGreaterThan(result.MoffatLogRms * 2,
                $"a Gaussian should fit a beta-3 Moffat far worse, but got moffat={result.MoffatLogRms:F4} gauss={result.GaussianLogRms:F4}");
        }

        [Fact]
        public void Measure_OnAGaussianField_DoesNotClaimHeavyWings()
        {
            // The converse guard: a genuinely Gaussian field must not report a low beta, or the
            // sweep would synthesise wings that are not in the data.
            var image = RenderGaussianField(fwhm: 3.0, seed: 13);

            var result = PsfProfileFit.Measure(image, channel: 0, DetectSyntheticStars(image)).ShouldNotBeNull();

            result.MoffatBeta.ShouldBeGreaterThan(8.0,
                $"a Gaussian is the beta -> infinity limit, so the fit should run high, got {result.MoffatBeta:F2}");
        }

        /// <summary>
        /// The measurement must not depend on the ORDER the stars arrive in. It did, and it cost real
        /// conclusions: repeating it on bit-identical master pixels gave FWHM stable to +/-0.02 px
        /// while beta swung 5.20 / 5.25 / 2.45 and the residual jumped 0.07 / 0.10 / 0.97, roughly one
        /// run in three. Star detection returns the same stars in a different order, the fit stacked
        /// the first 400 it was handed, and near the noise floor a different subset flips a marginal
        /// outer bin into or out of the fitted set, where its log-residual dominates.
        ///
        /// <para>An AHD-versus-drizzle comparison had been reported off single measurements on this
        /// basis, and the number called decisive there ("the Moffat never described the AHD red
        /// profile, residual 0.957 against 0.130") turned out to be a draw from this coin: the same
        /// failure then showed up on a drizzled master at 0.9745.</para>
        /// </summary>
        [Fact]
        public void Measure_IsIndependentOfTheOrderTheStarsArriveIn()
        {
            var image = RenderMoffatField(fwhm: 3.2, beta: 4.0, seed: 21);
            var stars = DetectSyntheticStars(image);
            var shuffled = new List<ImagedStar>(stars);
            var rng = new Random(99);
            for (var i = shuffled.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }
            shuffled.ShouldNotBe(stars, "the shuffle has to actually reorder, or this proves nothing");

            var a = PsfProfileFit.Measure(image, channel: 0, stars).ShouldNotBeNull();
            var b = PsfProfileFit.Measure(image, channel: 0, shuffled).ShouldNotBeNull();

            // Exactly equal, not approximately: the same pixels and the same stars are the same
            // measurement, and a tolerance here would hide precisely the drift this pins.
            b.Fwhm.ShouldBe(a.Fwhm);
            b.MoffatBeta.ShouldBe(a.MoffatBeta);
            b.MoffatLogRms.ShouldBe(a.MoffatLogRms);
            b.GaussianLogRms.ShouldBe(a.GaussianLogRms);
            b.StarsStacked.ShouldBe(a.StarsStacked);
        }

        /// <summary>
        /// A profile no Moffat can describe must be refused, not reported. The beta search is an
        /// exhaustive grid, so a large residual never means a stuck search; it means the stacked
        /// profile is not of that family, and the beta minimising it is a fitting artifact. The
        /// artifact is dangerous rather than merely useless, because it collapses toward the bottom of
        /// the grid and so reads as a plausible heavy-winged PSF, which is exactly the value the
        /// deconvolution sweep would be calibrated on.
        /// </summary>
        [Fact]
        public void Measure_OnAProfileNoMoffatCanDescribe_ReturnsNullInsteadOfAnArtifactBeta()
        {
            // A core plus a detached halo: real contamination (scattered light, a reflection, or a
            // stack of mixed star widths) puts flux in the wings that no single Moffat reaches.
            var image = RenderField(seed: 31,
                shape: (r, alpha) => Math.Pow(1 + (r * r) / (alpha * alpha), -4.0)
                    + (r > 6 && r < 10 ? 0.45 : 0.0),
                alpha: 3.2 / (2 * Math.Sqrt(Math.Pow(2, 1.0 / 4.0) - 1)));

            var stars = DetectSyntheticStars(image);
            var result = PsfProfileFit.Measure(image, channel: 0, stars, out var diagnostics);
            result.ShouldBeNull(
                $"accepted with fwhm {diagnostics.Fwhm:F2}, beta {diagnostics.MoffatBeta:F2}, rms {diagnostics.MoffatLogRms:F3}, floor {diagnostics.Floor:F4}, "
                + $"{diagnostics.FitBins} bins; profile {string.Join(" ", (diagnostics.Profile ?? []).Select((p, b) => $"{(b + 0.5) * diagnostics.BinWidthPx:F2}:{p:F4}"))}");

            // And the refusal SAYS it was the fit: the stack was fine (enough stars, a half-maximum, bins
            // to fit), the shape was not. A caller tallying refusals over an archive needs that distinction.
            diagnostics.Refusal.ShouldBe(PsfProfileFit.Refusal.PoorFit,
                $"floor {diagnostics.Floor:F4}, {diagnostics.FitBins} bins, fwhm {diagnostics.Fwhm:F2}; profile "
                + string.Join(" ", (diagnostics.Profile ?? []).Select((p, b) => $"{(b + 0.5) * diagnostics.BinWidthPx:F2}:{p:F4}")));
            diagnostics.Stacked.ShouldBeGreaterThanOrEqualTo(40);
            diagnostics.FitBins.ShouldBeGreaterThanOrEqualTo(8);
            diagnostics.MoffatLogRms.ShouldBeGreaterThan(0.5);
            double.IsFinite(diagnostics.Fwhm).ShouldBeTrue();
        }

        [Fact]
        public void Measure_WithTooFewStars_ReturnsNullRatherThanAGuess()
        {
            var image = RenderMoffatField(fwhm: 3.0, beta: 4.0, seed: 3);

            PsfProfileFit.Measure(image, channel: 0, new List<ImagedStar>()).ShouldBeNull();
            PsfProfileFit.Measure(image, channel: 0, new List<ImagedStar>(), out var diagnostics).ShouldBeNull();
            diagnostics.Refusal.ShouldBe(PsfProfileFit.Refusal.TooFewStars);
            diagnostics.StarsOffered.ShouldBe(0);
        }

        [Theory]
        [InlineData(PsfProfileFit.StarSelection.PercentileBand)]
        [InlineData(PsfProfileFit.StarSelection.SignalFloor)]
        public void Measure_RecoversTheExponentUnderEitherStarSelection(PsfProfileFit.StarSelection selection)
        {
            // The synthetic field's stars are all equally bright, so both selections stack the same
            // population and must agree; the point is that the signal-floor path runs the whole fit.
            var image = RenderMoffatField(fwhm: 3.2, beta: 4.0, seed: 7);
            var result = PsfProfileFit.Measure(image, channel: 0, DetectSyntheticStars(image), out var diagnostics, selection: selection)
                .ShouldNotBeNull();
            diagnostics.Refusal.ShouldBe(PsfProfileFit.Refusal.None);
            result.Fwhm.ShouldBe(3.2, tolerance: 0.35);
            result.MoffatBeta.ShouldBeInRange(4.0 / 1.6, 4.0 * 1.6);
        }

        [Fact]
        public void SignalFloor_TakesEveryStarOverTheBar_WhereThePercentileBandTakesAFifth()
        {
            var image = RenderMoffatField(fwhm: 3.2, beta: 4.0, seed: 7);
            var stars = DetectSyntheticStars(image);

            PsfProfileFit.Measure(image, channel: 0, stars, out var band).ShouldNotBeNull();
            PsfProfileFit.Measure(image, channel: 0, stars, out var floor, selection: PsfProfileFit.StarSelection.SignalFloor).ShouldNotBeNull();

            // Every synthetic star peaks at ~3000 over a 200 background with MAD ~1, so all of them
            // clear fifty MADs and only the brightest percent is guarded off.
            floor.InBrightnessBand.ShouldBeGreaterThan(band.InBrightnessBand);
            floor.InBrightnessBand.ShouldBeGreaterThanOrEqualTo((int)(stars.Count * 0.95));
        }

        [Fact]
        public void Measure_OnAGoodField_ReportsNoRefusalAndTheCountsBehindTheFit()
        {
            var image = RenderMoffatField(fwhm: 3.2, beta: 4.0, seed: 7);
            var stars = DetectSyntheticStars(image);

            var result = PsfProfileFit.Measure(image, channel: 0, stars, out var diagnostics).ShouldNotBeNull();
            diagnostics.Refusal.ShouldBe(PsfProfileFit.Refusal.None);
            diagnostics.StarsOffered.ShouldBe(stars.Count);
            diagnostics.InBrightnessBand.ShouldBeGreaterThanOrEqualTo(diagnostics.Stacked);
            diagnostics.Stacked.ShouldBe(result.StarsStacked);
            diagnostics.Fwhm.ShouldBe(result.Fwhm);
            diagnostics.MoffatBeta.ShouldBe(result.MoffatBeta);
            diagnostics.MoffatLogRms.ShouldBe(result.MoffatLogRms);
        }

        /// <summary>Grid of well-separated identical stars on a flat background, so the stacked
        /// profile has a known answer. Spacing is wider than the isolation radius the fit enforces.</summary>
        private static Image RenderMoffatField(double fwhm, double beta, int seed)
            => RenderField(seed, (r, alpha) => Math.Pow(1 + (r * r) / (alpha * alpha), -beta),
                alpha: fwhm / (2 * Math.Sqrt(Math.Pow(2, 1.0 / beta) - 1)));

        private static Image RenderGaussianField(double fwhm, int seed)
            => RenderField(seed, (r, sigma) => Math.Exp(-(r * r) / (2 * sigma * sigma)),
                alpha: fwhm / 2.3548200450309493);

        private static Image RenderField(int seed, Func<double, double, double> shape, double alpha)
        {
            var data = new float[Size, Size];
            var rng = new Random(seed);
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    data[y, x] = Background + (float)((rng.NextDouble() - 0.5) * 4.0);
                }
            }

            // Pitch comfortably beyond the 16 px isolation radius, inset clear of the edge guard.
            // Amplitudes VARY: the fit selects a brightness band, so a field of identical stars
            // would leave it only the band's own width to work with (and did, when this test first
            // ran: 100 identical stars gave 20 in band against a 40-star floor, and Measure
            // correctly returned null).
            for (var gy = Inset; gy < Size - Inset; gy += Pitch)
            {
                for (var gx = Inset; gx < Size - Inset; gx += Pitch)
                {
                    // Sub-pixel offsets so the stack is not a single phase repeated.
                    var cx = gx + rng.NextDouble() - 0.5;
                    var cy = gy + rng.NextDouble() - 0.5;
                    var amp = Amplitude * (0.4 + 1.4 * rng.NextDouble());
                    for (var dy = -14; dy <= 14; dy++)
                    {
                        for (var dx = -14; dx <= 14; dx++)
                        {
                            var x = (int)Math.Round(cx) + dx;
                            var y = (int)Math.Round(cy) + dy;
                            if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                            var r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                            data[y, x] += (float)(amp * shape(r, alpha));
                        }
                    }
                }
            }

            var max = 0f;
            var min = float.MaxValue;
            foreach (var v in data)
            {
                if (v > max) max = v;
                if (v < min) min = v;
            }
            var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(60),
                FrameType.Light, "", 3.76f, 3.76f, 100, -1, Filter.Luminance, 1, 1,
                float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            return new Image([data], BitDepth.Int16, max, min, 0, meta);
        }

        /// <summary>The grid positions the renderer used, as detections. Built directly rather than
        /// through FindStarsAsync so the test exercises the PROFILE fit and not star detection.</summary>
        private static List<ImagedStar> DetectSyntheticStars(Image image)
        {
            var stars = new List<ImagedStar>();
            for (var gy = Inset; gy < Size - Inset; gy += Pitch)
            {
                for (var gx = Inset; gx < Size - Inset; gx += Pitch)
                {
                    // Centroid the local neighbourhood so the fit gets sub-pixel centres, as it
                    // would from real detection.
                    double sw = 0, sx = 0, sy = 0;
                    for (var dy = -6; dy <= 6; dy++)
                    {
                        for (var dx = -6; dx <= 6; dx++)
                        {
                            var x = gx + dx;
                            var y = gy + dy;
                            if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                            var v = Math.Max(0f, image.GetChannelSpan(0)[y * Size + x] - Background);
                            sw += v;
                            sx += v * x;
                            sy += v * y;
                        }
                    }
                    if (sw <= 0) continue;
                    stars.Add(new ImagedStar(3f, 3f, 100f, (float)sw, (float)(sx / sw), (float)(sy / sw), 0.1f));
                }
            }
            return stars;
        }
    }
}
