using Shouldly;
using System;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The degradation math the shared exporter rests on (docs/plans/model-training-roadmap.md
    /// section 1 item 3): a kernel that is the width it says it is, a noise field whose SHAPE is the
    /// thing the two injection arms differ in, a level that follows the signal, and a stretch that can
    /// be handed one frame's parameters and applied to another.
    /// </summary>
    [Collection("Imaging")]
    public class LinearDegradationTests(ITestOutputHelper output)
    {
        private const int W = 96;
        private const int H = 96;

        private static float[] PointSource(int width, int height)
        {
            var p = new float[width * height];
            p[((height / 2) * width) + (width / 2)] = 1f;
            return p;
        }

        /// <summary>FWHM of a centred, radially symmetric spot, by walking +X to the half-maximum.</summary>
        private static double SpotFwhm(ReadOnlySpan<float> img, int width, int height)
        {
            var cx = width / 2;
            var cy = height / 2;
            var peak = img[(cy * width) + cx];
            var half = peak / 2f;
            for (var r = 1; cx + r < width; r++)
            {
                var here = img[(cy * width) + cx + r];
                if (here <= half)
                {
                    var prev = img[(cy * width) + cx + r - 1];
                    var t = (prev - half) / (prev - here);
                    return 2.0 * (r - 1 + t);
                }
            }
            return double.NaN;
        }

        [Theory]
        [InlineData(2.0)]
        [InlineData(3.5)]
        [InlineData(6.0)]
        public void AGaussianKernelIsTheWidthItWasAskedFor(double fwhm)
        {
            var k = PsfKernel.Gaussian(fwhm);

            k.MeasureFwhm().ShouldBe(fwhm, 0.15);
            var sum = 0.0;
            foreach (var w in k.Weights)
            {
                sum += w;
            }
            sum.ShouldBe(1.0, 1e-5);
        }

        [Theory]
        [InlineData(3.0, 2.0)]
        [InlineData(3.0, 8.0)]
        [InlineData(5.0, 1.5)]
        public void AMoffatKernelIsTheWidthItWasAskedForAtEveryBeta(double fwhm, double beta)
        {
            var k = PsfKernel.Moffat(fwhm, beta);

            k.MeasureFwhm().ShouldBe(fwhm, 0.15);
            var sum = 0.0;
            foreach (var w in k.Weights)
            {
                sum += w;
            }
            sum.ShouldBe(1.0, 1e-5);
        }

        [Fact]
        public void ConvolvingAPointSourceProducesAStarOfTheKernelsWidth()
        {
            var k = PsfKernel.Gaussian(4.0);

            var blurred = k.Convolve(PointSource(W, H), W, H);

            SpotFwhm(blurred, W, H).ShouldBe(4.0, 0.2);
        }

        /// <summary>
        /// The property the deconvolver's labels rest on: the kernel is the EXTRA blur, and two
        /// Gaussians compose in quadrature. A pair labelled "3.0 px added" is only honest if the frame
        /// really moved from 2.5 to sqrt(2.5^2 + 3.0^2) = 3.9.
        /// </summary>
        [Fact]
        public void BlurComposesInQuadratureSoTheLabelIsTheExtraWidth()
        {
            var start = PsfKernel.Gaussian(2.5).Convolve(PointSource(W, H), W, H);
            var startFwhm = SpotFwhm(start, W, H);

            var blurred = PsfKernel.Gaussian(3.0).Convolve(start, W, H);

            var expected = Math.Sqrt((startFwhm * startFwhm) + (3.0 * 3.0));
            output.WriteLine($"{startFwhm:F2} px blurred by 3.00 px -> {SpotFwhm(blurred, W, H):F2} px (quadrature {expected:F2})");
            SpotFwhm(blurred, W, H).ShouldBe(expected, 0.25);
        }

        [Fact]
        public void TheSeparableFastPathAgreesWithTheFullConvolution()
        {
            var k = PsfKernel.Gaussian(3.0);
            k.IsSeparable.ShouldBeTrue();
            var elongated = PsfKernel.Gaussian(3.0, elongation: 1.0001);
            elongated.IsSeparable.ShouldBeFalse("only an exactly circular Gaussian may take the fast path");

            var rng = new Random(7);
            var src = new float[W * H];
            for (var i = 0; i < src.Length; i++)
            {
                src[i] = (float)rng.NextDouble();
            }

            var fast = k.Convolve(src, W, H);
            var full = elongated.Convolve(src, W, H);

            var maxDiff = 0f;
            for (var i = 0; i < fast.Length; i++)
            {
                maxDiff = MathF.Max(maxDiff, MathF.Abs(fast[i] - full[i]));
            }
            output.WriteLine($"separable vs full: max |diff| {maxDiff:E2}");
            maxDiff.ShouldBeLessThan(1e-4f);
        }

        [Fact]
        public void BlurConservesFluxSoABackgroundLevelSurvivesIt()
        {
            var flat = new float[W * H];
            Array.Fill(flat, 0.017f);

            var blurred = PsfKernel.Moffat(4.0, 2.0).Convolve(flat, W, H);

            blurred.Min().ShouldBe(0.017f, 1e-6f);
            blurred.Max().ShouldBe(0.017f, 1e-6f);
        }

        [Fact]
        public void AWhiteFieldHasUnitVarianceAndAWarpedOneIsSmoother()
        {
            var white = NoiseField.White(128, 128, new Random(1));
            var warped = NoiseField.Warped(128, 128, realisations: 8, new Random(1));

            static double Variance(ReadOnlySpan<float> f)
            {
                var mean = 0.0;
                foreach (var v in f) mean += v;
                mean /= f.Length;
                var s = 0.0;
                foreach (var v in f) s += (v - mean) * (v - mean);
                return s / f.Length;
            }
            Variance(white).ShouldBe(1.0, 0.02);
            Variance(warped).ShouldBe(1.0, 0.02);

            // The whole point of the second arm: it must MOVE the shape, or S-white and S-warped are
            // the same experiment run twice. A bilinear resample redistributes power out of the
            // finest band, so band1/band0 rises.
            var whiteRatio = NoiseField.BandRatio(white, 128, 128);
            var warpedRatio = NoiseField.BandRatio(warped, 128, 128);
            output.WriteLine($"band1/band0: white {whiteRatio:F3}, warped {warpedRatio:F3}");
            warpedRatio.ShouldBeGreaterThan(whiteRatio * 1.05, "the warped arm has to differ in shape from the white one");

            // And the calibration knob has to MOVE the ratio monotonically, because that is the only
            // reason it exists: measured against this archive, bilinear alone lands at 0.33 while a real
            // half-master reads 0.46 and a real sub 0.79, and a resample sigma of 0.5 px is what closes
            // the first gap (0.460 against 0.463). A knob that saturated or reversed would leave the
            // arm uncalibratable and H2 untestable.
            var ratios = new[] { 0.0, 0.3, 0.5, 0.8 }
                .Select(s => (Sigma: s, Ratio: NoiseField.BandRatio(NoiseField.Warped(128, 128, 8, new Random(2), s), 128, 128)))
                .ToArray();
            foreach (var (sigma, ratio) in ratios)
            {
                output.WriteLine($"  resample sigma {sigma:F1} -> band1/band0 {ratio:F3}");
            }
            ratios.Zip(ratios.Skip(1)).ShouldAllBe(p => p.Second.Ratio > p.First.Ratio);
        }

        [Fact]
        public void TheInjectedLevelFollowsTheDepthScaleAndTheSignal()
        {
            var rng = new Random(3);
            const double background = 0.020;
            const double oneSub = 0.004;
            var cal = new LinearDegradation.NoiseCalibration(PedestalAdu: 0.0, BackgroundAdu: background, OneSubSigmaAdu: oneSub, StackedFrames: 100);

            static double MeasuredSigma(double signal, double depth, in LinearDegradation.NoiseCalibration cal, Random rng)
            {
                var plane = new float[64 * 64];
                Array.Fill(plane, (float)signal);
                LinearDegradation.AddNoiseInPlace(plane, NoiseField.White(64, 64, rng), cal, depth);
                var mean = plane.Average();
                var s = plane.Sum(v => (v - mean) * (v - mean)) / plane.Length;
                return Math.Sqrt(s);
            }

            // Level: half the depth is half the noise.
            MeasuredSigma(background, 1.0, cal, rng).ShouldBe(oneSub, oneSub * 0.05);
            MeasuredSigma(background, 0.5, cal, rng).ShouldBe(oneSub * 0.5, oneSub * 0.05);

            // Signal: four times the background is twice the noise (variance linear in signal).
            MeasuredSigma(background * 4, 1.0, cal, rng).ShouldBe(oneSub * 2.0, oneSub * 0.1);

            // And a pixel far below the background does not go silent; the read-noise floor holds.
            MeasuredSigma(background * 0.01, 1.0, cal, rng)
                .ShouldBe(oneSub * Math.Sqrt(LinearDegradation.NoiseCalibration.MinVarianceFraction), oneSub * 0.05);
        }

        [Fact]
        public void TheCalibrationReadsOneSubsNoiseBackOffTheMaster()
        {
            // A synthetic master: flat background plus noise at 1/sqrt(N) of a sub's.
            const int n = 64;
            const double oneSub = 0.008;
            var rng = new Random(11);
            var master = new float[W * H];
            Array.Fill(master, 0.030f);
            LinearDegradation.AddNoiseInPlace(
                master,
                NoiseField.White(W, H, rng),
                new LinearDegradation.NoiseCalibration(0.0, 0.030, oneSub, n),
                depthScale: 1.0 / Math.Sqrt(n));

            var measured = LinearDegradation.NoiseCalibration.Measure(master, pedestal: 0.0, stackedFrames: n);

            output.WriteLine($"one sub: truth {oneSub:E3}, measured {measured.OneSubSigmaAdu:E3}");
            measured.BackgroundAdu.ShouldBe(0.030, 1e-3);
            measured.OneSubSigmaAdu.ShouldBe(oneSub, oneSub * 0.1);
        }

        /// <summary>
        /// The level anchor crosses domains: the training manifest measures a sub's noise on the
        /// STRETCHED tile, and the injector adds noise in LINEAR units. On a flat field, where nothing
        /// but noise is in the measurement, the conversion through the MTF slope must come back to the
        /// sigma that went in. Anything worse than a few percent here is the conversion, not the scene.
        /// </summary>
        [Fact]
        public void TheStretchedNoiseMeasurementConvertsBackToTheLinearSigmaItCameFrom()
        {
            const double background = 0.0153; // ~1000 ADU of 65535, a typical light-polluted sky
            const double oneSub = 6.1e-4;     // ~40 ADU
            var rng = new Random(23);

            // The parameters a real frame of this background would produce: min at zero, median at the
            // background, mapped to the NAFNet input median. Taken analytically rather than by stretching
            // a flat field, because a field with no spread at all has median == min and MtfStretch then
            // falls back to the identity, which would test nothing.
            var origMin = new[] { 0f };
            var balances = new[] { Image.MidtonesBalanceFor(background, 0.25) };

            var noisy = new float[W * H];
            Array.Fill(noisy, (float)background);
            LinearDegradation.AddNoiseInPlace(
                noisy,
                NoiseField.White(W, H, rng),
                new LinearDegradation.NoiseCalibration(0.0, background, oneSub, 1),
                depthScale: 1.0);
            var noisyPlane = new float[W, H];
            for (var y = 0; y < W; y++)
            {
                for (var x = 0; x < H; x++)
                {
                    noisyPlane[y, x] = noisy[(y * H) + x];
                }
            }
            var noisyStretched = new Image([noisyPlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome })
                .MtfStretchWith(origMin, balances);

            // What the manifest would record: the raw MAD of the stored (stretched) tile.
            var stored = noisyStretched.GetChannelSpan(0).ToArray();
            var median = stored.Order().ElementAt(stored.Length / 2);
            for (var i = 0; i < stored.Length; i++)
            {
                stored[i] = MathF.Abs(stored[i] - median);
            }
            var storedMad = stored.Order().ElementAt(stored.Length / 2);

            var recovered = LinearDegradation.NoiseCalibration.FromStretchedSubNoise(
                new float[W * H].Select(_ => (float)background).ToArray(),
                pedestal: 0.0,
                subNoiseMadStretched: storedMad,
                midtonesBalance: balances[0],
                origMin: origMin[0],
                stackedFrames: 1);

            output.WriteLine($"one sub: truth {oneSub:E3}, recovered {recovered.OneSubSigmaAdu:E3} ({recovered.OneSubSigmaAdu / oneSub:F3}x); stretched MAD {storedMad:E3}, slope {Image.MidtonesTransferFunctionSlope(balances[0], background - origMin[0]):F1}");
            recovered.OneSubSigmaAdu.ShouldBe(oneSub, oneSub * 0.05);

            noisyStretched.Release();
        }

        [Fact]
        public void NoiseInjectionPreservesNaN()
        {
            var plane = new float[16];
            Array.Fill(plane, 0.5f);
            plane[3] = float.NaN;
            var cal = new LinearDegradation.NoiseCalibration(0.0, 0.5, 0.01, 10);

            LinearDegradation.AddNoiseInPlace(plane, NoiseField.White(4, 4, new Random(5)), cal, 1.0);

            float.IsNaN(plane[3]).ShouldBeTrue();
            plane.Where((_, i) => i != 3).ShouldAllBe(v => !float.IsNaN(v));
        }

        [Fact]
        public void MtfStretchWithReproducesTheMeasuringStretchAndRoundTrips()
        {
            var rng = new Random(13);
            var plane = new float[W, H];
            for (var y = 0; y < W; y++)
            {
                for (var x = 0; x < H; x++)
                {
                    plane[y, x] = 0.01f + (0.002f * (float)rng.NextDouble());
                }
            }
            var img = new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, new ImageMeta { SensorType = SensorType.Monochrome });

            var measured = img.MtfStretch(0.25, out var origMin, out var balances);
            var applied = img.MtfStretchWith(origMin, balances);

            var a = measured.GetChannelSpan(0);
            var b = applied.GetChannelSpan(0);
            for (var i = 0; i < a.Length; i++)
            {
                b[i].ShouldBe(a[i]);
            }

            // And the pair (stretch-with, unstretch) is an exact inverse, which is what lets a runner
            // hand the graph one domain and give the caller back another.
            var back = applied.MtfUnstretch(origMin, balances);
            var src = img.GetChannelSpan(0);
            var rt = back.GetChannelSpan(0);
            var maxDiff = 0f;
            for (var i = 0; i < src.Length; i++)
            {
                maxDiff = MathF.Max(maxDiff, MathF.Abs(src[i] - rt[i]));
            }
            output.WriteLine($"stretch-with round trip: max |diff| {maxDiff:E2}");
            maxDiff.ShouldBeLessThan(1e-6f);

            measured.Release();
            applied.Release();
            back.Release();
            img.Release();
        }

        /// <summary>
        /// The reason the exporter may stretch one CELL instead of the whole canvas per draw: with the
        /// parameters fixed by the target, the transform is pointwise, so cutting then stretching and
        /// stretching then cutting are the same bytes.
        /// </summary>
        [Fact]
        public void WithFixedParametersCuttingBeforeOrAfterTheStretchIsTheSameBytes()
        {
            var rng = new Random(17);
            var full = new float[64, 64];
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 64; x++)
                {
                    full[y, x] = 0.01f + (0.05f * (float)rng.NextDouble());
                }
            }
            var img = new Image([full], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome });
            var origMin = new[] { 0.008f };
            var balances = new[] { 0.31 };

            var stretchedFull = img.MtfStretchWith(origMin, balances);

            var cut = new float[16, 16];
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    cut[y, x] = full[y + 20, x + 20];
                }
            }
            var stretchedCut = new Image([cut], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome })
                .MtfStretchWith(origMin, balances);

            var whole = stretchedFull.GetChannelSpan(0);
            var part = stretchedCut.GetChannelSpan(0);
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    part[(y * 16) + x].ShouldBe(whole[((y + 20) * 64) + x + 20]);
                }
            }

            stretchedFull.Release();
            stretchedCut.Release();
            img.Release();
        }
    }
}
