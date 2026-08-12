using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins <see cref="PhotometricRepeatability"/>, which supplies the number the AI photometric gate
    /// is calibrated against. Synthetic star lists throughout: the point is that the measurement
    /// recovers a KNOWN injected perturbation, which a real frame pair cannot demonstrate because its
    /// true scatter is the unknown being measured.
    /// </summary>
    public sealed class PhotometricRepeatabilityTests
    {
        private const int Seed = 20260812;

        [Fact]
        public void Compare_RecoversTheDitherOffset_SoResidualShiftIsNotTheDither()
        {
            // A 23.5 / -17.25 px dither is entirely ordinary between subs. Without offset removal the
            // reported centroid shift would be ~29 px and the gate would be meaningless.
            var a = SyntheticField(400, jitterPx: 0f, fluxJitter: 0f);
            var b = Shift(a, 23.5f, -17.25f);

            var result = PhotometricRepeatability.Compare(AsSpan(a), AsSpan(b));

            result.ShouldNotBeNull();
            result.OffsetX.ShouldBe(23.5f, tolerance: 0.01f);
            result.OffsetY.ShouldBe(-17.25f, tolerance: 0.01f);
            result.MatchedStars.ShouldBeGreaterThan(350);
            result.Overall.CentroidShiftP95.ShouldBeLessThan(0.01f);
            result.Overall.FluxDeltaP95.ShouldBeLessThan(0.001f);
        }

        [Fact]
        public void Compare_MeasuresAnInjectedFluxScatter_RatherThanReportingZero()
        {
            // 4% one-sigma flux scatter injected. The p50 of |delta| for a normal distribution sits at
            // about 0.674 sigma, so ~2.7%, and p95 at about 1.96 sigma, so ~7.8%. Generous windows:
            // this asserts the measurement tracks the injection, not that the RNG hit its mean.
            var a = SyntheticField(600, jitterPx: 0f, fluxJitter: 0f);
            var b = Perturb(Shift(a, 5f, 5f), jitterPx: 0f, fluxSigma: 0.04f);

            var result = PhotometricRepeatability.Compare(AsSpan(a), AsSpan(b));

            result.ShouldNotBeNull();
            result.Overall.FluxDeltaP50.ShouldBeInRange(0.015f, 0.045f);
            result.Overall.FluxDeltaP95.ShouldBeInRange(0.05f, 0.12f);
            // Scatter, not a throughput difference: the SIGNED median stays near zero, which is how a
            // caller tells "noisy" from "one frame was dimmer".
            MathF.Abs(result.Overall.FluxBiasP50).ShouldBeLessThan(0.02f);
        }

        [Fact]
        public void Compare_SeparatesAThroughputDifferenceFromScatter()
        {
            // Frame B uniformly 10% brighter (thin cloud, or a normalisation difference). The absolute
            // delta and the signed bias must BOTH report ~10%, which is what distinguishes this from
            // the scatter case above where the bias vanished.
            var a = SyntheticField(400, jitterPx: 0f, fluxJitter: 0f);
            var b = Scale(Shift(a, 3f, -2f), 1.10f);

            var result = PhotometricRepeatability.Compare(AsSpan(a), AsSpan(b));

            result.ShouldNotBeNull();
            result.Overall.FluxBiasP50.ShouldBe(0.10f, tolerance: 0.005f);
            result.Overall.FluxDeltaP50.ShouldBe(0.10f, tolerance: 0.005f);
        }

        [Fact]
        public void Compare_BandsBySnr_SoTheFaintTailCannotSetTheThreshold()
        {
            // The whole reason for banding: give the faint stars 12% scatter and the bright stars 1%.
            // An unbanded p95 lands between the two and describes neither, and it would move with how
            // many faint stars a frame happened to yield.
            var a = SyntheticField(800, jitterPx: 0f, fluxJitter: 0f);
            var b = PerturbBySnr(Shift(a, 8f, 4f), faintSigma: 0.12f, brightSigma: 0.01f, snrSplit: 50f);

            var result = PhotometricRepeatability.Compare(AsSpan(a), AsSpan(b));

            result.ShouldNotBeNull();

            var faint = FindBand(result.Bands, 5f);
            var bright = FindBand(result.Bands, 100f);

            faint.Stars.ShouldBeGreaterThan(20);
            bright.Stars.ShouldBeGreaterThan(20);
            // An order of magnitude apart in the data must be visible as such in the report.
            bright.FluxDeltaP50.ShouldBeLessThan(faint.FluxDeltaP50 / 3f);
        }

        [Fact]
        public void Compare_UsesTheLimitingSnrOfThePair_NotTheBrighterFrames()
        {
            // The same star at SNR 200 in A and 8 in B is an 8-SNR measurement. Banding it as bright
            // would charge the model for the faint frame's noise.
            var a = new[] { Star(100f, 100f, flux: 1000f, snr: 200f) };
            var b = new[] { Star(100f, 100f, flux: 1000f, snr: 8f) };

            // One star is below the match floor, so drive the internals through a field that carries
            // this pair plus enough filler to clear MinMatchedStars.
            var fillerA = new List<ImagedStar>(a);
            var fillerB = new List<ImagedStar>(b);
            for (var i = 0; i < 40; i++)
            {
                var x = 500f + i * 20f;
                fillerA.Add(Star(x, 700f, flux: 500f, snr: 150f));
                fillerB.Add(Star(x, 700f, flux: 500f, snr: 150f));
            }

            var result = PhotometricRepeatability.Compare(AsSpan(fillerA.ToArray()), AsSpan(fillerB.ToArray()));

            result.ShouldNotBeNull();
            // The pair lands in the 5-10 band (limiting SNR 8), never in the 100+ band, which holds
            // only the 40 filler stars.
            FindBand(result.Bands, 5f).Stars.ShouldBe(1);
            FindBand(result.Bands, 100f).Stars.ShouldBe(40);
        }

        [Fact]
        public void Compare_ReturnsNull_WhenTheFramesDoNotOverlap()
        {
            // Two different patches of sky must not produce a confident scatter number out of chance
            // pairings, the same posture PsfProfileFit takes on an unfittable profile.
            var a = SyntheticField(300, jitterPx: 0f, fluxJitter: 0f);
            var b = Shift(SyntheticField(300, jitterPx: 0f, fluxJitter: 0f, seedOffset: 99), 4000f, 4000f);

            PhotometricRepeatability.Compare(AsSpan(a), AsSpan(b)).ShouldBeNull();
        }

        [Fact]
        public void Compare_ReturnsNull_OnAFieldTooSparseToMeasure()
        {
            var a = SyntheticField(5, jitterPx: 0f, fluxJitter: 0f);
            var b = Shift(a, 1f, 1f);

            PhotometricRepeatability.Compare(AsSpan(a), AsSpan(b)).ShouldBeNull();
        }

        private static PhotometricRepeatability.Band FindBand(ImmutableArray<PhotometricRepeatability.Band> bands, float low)
        {
            foreach (var band in bands)
            {
                if (band.SnrLow == low)
                {
                    return band;
                }
            }

            throw new InvalidOperationException($"No band starting at SNR {low}.");
        }

        private static ReadOnlySpan<ImagedStar> AsSpan(ImagedStar[] stars) => stars;

        private static ImagedStar Star(float x, float y, float flux, float snr)
            => new(HFD: 3f, StarFWHM: 3f, SNR: snr, Flux: flux, XCentroid: x, YCentroid: y, Ellipticity: 0.1f);

        /// <summary>
        /// A field on a loose grid with jittered positions, so stars are well separated and the mutual
        /// nearest-neighbour match is unambiguous. Flux spans three decades and SNR is tied to it, so
        /// the SNR bands are populated the way a real frame populates them.
        /// </summary>
        private static ImagedStar[] SyntheticField(int count, float jitterPx, float fluxJitter, int seedOffset = 0)
        {
            var rng = new Random(Seed + seedOffset);
            var stars = new ImagedStar[count];
            var perRow = (int)MathF.Ceiling(MathF.Sqrt(count));

            for (var i = 0; i < count; i++)
            {
                var gx = 200f + i % perRow * 60f;
                var gy = 200f + i / perRow * 60f;
                var x = gx + (float)(rng.NextDouble() - 0.5) * 10f + (float)(rng.NextDouble() - 0.5) * jitterPx;
                var y = gy + (float)(rng.NextDouble() - 0.5) * 10f + (float)(rng.NextDouble() - 0.5) * jitterPx;

                // Log-uniform flux over [50, 50000]; SNR ~ sqrt(flux) puts stars across every band.
                var flux = (float)(50.0 * Math.Pow(1000.0, rng.NextDouble()));
                flux *= 1f + (float)(rng.NextDouble() - 0.5) * 2f * fluxJitter;
                stars[i] = Star(x, y, flux, MathF.Sqrt(flux));
            }

            return stars;
        }

        private static ImagedStar[] Shift(ImagedStar[] stars, float dx, float dy)
        {
            var moved = new ImagedStar[stars.Length];
            for (var i = 0; i < stars.Length; i++)
            {
                moved[i] = stars[i] with
                {
                    XCentroid = stars[i].XCentroid + dx,
                    YCentroid = stars[i].YCentroid + dy,
                };
            }
            return moved;
        }

        private static ImagedStar[] Scale(ImagedStar[] stars, float factor)
        {
            var scaled = new ImagedStar[stars.Length];
            for (var i = 0; i < stars.Length; i++)
            {
                scaled[i] = stars[i] with { Flux = stars[i].Flux * factor };
            }
            return scaled;
        }

        private static ImagedStar[] Perturb(ImagedStar[] stars, float jitterPx, float fluxSigma)
        {
            var rng = new Random(Seed + 7);
            var out_ = new ImagedStar[stars.Length];
            for (var i = 0; i < stars.Length; i++)
            {
                out_[i] = stars[i] with
                {
                    Flux = stars[i].Flux * (1f + (float)Gaussian(rng) * fluxSigma),
                    XCentroid = stars[i].XCentroid + (float)Gaussian(rng) * jitterPx,
                    YCentroid = stars[i].YCentroid + (float)Gaussian(rng) * jitterPx,
                };
            }
            return out_;
        }

        private static ImagedStar[] PerturbBySnr(ImagedStar[] stars, float faintSigma, float brightSigma, float snrSplit)
        {
            var rng = new Random(Seed + 11);
            var out_ = new ImagedStar[stars.Length];
            for (var i = 0; i < stars.Length; i++)
            {
                var sigma = stars[i].SNR < snrSplit ? faintSigma : brightSigma;
                out_[i] = stars[i] with { Flux = stars[i].Flux * (1f + (float)Gaussian(rng) * sigma) };
            }
            return out_;
        }

        /// <summary>Box-Muller, so the injected scatter is normal and the percentile expectations in
        /// the tests above (0.674 sigma at p50 of |x|, 1.96 sigma at p95) actually apply.</summary>
        private static double Gaussian(Random rng)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
