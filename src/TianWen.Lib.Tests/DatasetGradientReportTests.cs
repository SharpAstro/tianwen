using Shouldly;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Coverage for <see cref="DatasetGradientReport"/> (gradient-remover-training.md, G1): the polynomial
    /// basis the fit now reports, the sky-geometry helpers the covariates ride on, the per-master
    /// measurement on a synthetic master with a canvas ring, the store, and the Markdown.
    /// </summary>
    [Collection("Imaging")]
    public class DatasetGradientReportTests(ITestOutputHelper output) : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "gradreport-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void ThePolynomialBasisIsEnumeratedByTotalDegreeThenDescendingX()
        {
            BackgroundPolynomial.Exponents(2).ShouldBe([(0, 0), (1, 0), (0, 1), (2, 0), (1, 1), (0, 2)]);
            BackgroundPolynomial.TermCount(2).ShouldBe(6);
            BackgroundPolynomial.DegreeOf(6).ShouldBe(2);
            BackgroundPolynomial.DegreeOf(3).ShouldBe(1);
            BackgroundPolynomial.DegreeOf(4).ShouldBe(-1);
            BackgroundPolynomial.Normalise(0, 101).ShouldBe(-1.0);
            BackgroundPolynomial.Normalise(100, 101).ShouldBe(1.0);
            BackgroundPolynomial.Normalise(0, 1).ShouldBe(0.0);
            BackgroundPolynomial.Evaluate([1.0, 2.0, 3.0, 4.0, 5.0, 6.0], 0.5, -0.5).ShouldBe(1 + 1.0 - 1.5 + 1.0 - 1.25 + 1.5, 1e-12);
        }

        [Fact]
        public async Task TheFitReportsCoefficientsThatRecoverAnExactRamp()
        {
            const int W = 64, H = 48;
            const float A = 0.010f, B = 0.002f, C = -0.001f;
            var plane = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    plane[y, x] = (float)(A + B * BackgroundPolynomial.Normalise(x, W) + C * BackgroundPolynomial.Normalise(y, H));
                }
            }
            var image = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome });

            // Downsample 1 makes the working grid the plane itself, so the coordinates match exactly.
            var result = await new ClassicalBackgroundExtractor().ExtractAsync(
                image, BackgroundExtractionOptions.Default with { Downsample = 1 }, TestContext.Current.CancellationToken);

            var c = result.Planes[0].Coefficients;
            c.Length.ShouldBe(6);
            c[0].ShouldBe(A, 1e-6);
            c[1].ShouldBe(B, 1e-6);
            c[2].ShouldBe(C, 1e-6);
            c[3].ShouldBe(0.0, 1e-6);
            c[4].ShouldBe(0.0, 1e-6);
            c[5].ShouldBe(0.0, 1e-6);
            result.Cleaned.Release();
            result.Background.Release();
        }

        [Fact]
        public void DescribeShape_ReadsRampDomeBowlAndSaddleOffTheCoefficients()
        {
            var ramp = DatasetGradientReport.DescribeShape([1.0, 0.5, 0.0, 0.0, 0.0, 0.0], 101, 101, 0.01f);
            ramp.Shape.ShouldBe("Ramp");
            ramp.LinearShare.ShouldBe(1f);
            ramp.GradientAngleDeg.ShouldBe(0f, 1e-3f);
            ramp.LinearPeakToPeak.ShouldBe(1f, 1e-6f);

            // +y brightening reads 90 (down the rows); a rectangular plane does not change the direction of an axis-aligned ramp.
            DatasetGradientReport.DescribeShape([1.0, 0.0, 0.5, 0.0, 0.0, 0.0], 300, 200, 0.01f).GradientAngleDeg.ShouldBe(90f, 1e-3f);
            // Equal normalised slopes on a 2:1 plane: per pixel the x slope is half the y slope, so the direction leans toward +y.
            DatasetGradientReport.DescribeShape([1.0, 0.5, 0.5, 0.0, 0.0, 0.0], 201, 101, 0.01f).GradientAngleDeg.ShouldBe(63.43f, 0.05f);

            var dome = DatasetGradientReport.DescribeShape([1.0, 0.0, 0.0, -0.5, 0.0, -0.5], 101, 101, 0.01f);
            dome.Shape.ShouldBe("Dome");
            dome.CurvatureMajor.ShouldBeLessThan(0f);
            dome.CurvatureMinor.ShouldBeLessThan(0f);
            dome.LinearShare.ShouldBe(0f);

            DatasetGradientReport.DescribeShape([1.0, 0.0, 0.0, 0.5, 0.0, 0.5], 101, 101, 0.01f).Shape.ShouldBe("Bowl");
            DatasetGradientReport.DescribeShape([1.0, 0.0, 0.0, 0.5, 0.0, -0.5], 101, 101, 0.01f).Shape.ShouldBe("Saddle");

            // A small quadratic term on a strong ramp is still a ramp (under a quarter of the linear range).
            DatasetGradientReport.DescribeShape([1.0, 0.5, 0.0, 0.05, 0.0, 0.0], 101, 101, 0.01f).Shape.ShouldBe("Ramp");
            // Everything under half a sigma is flat, whatever its signs.
            DatasetGradientReport.DescribeShape([1.0, 0.001, 0.0, -0.001, 0.0, 0.0], 101, 101, 0.1f).Shape.ShouldBe("Flat");
            DatasetGradientReport.DescribeShape([], 101, 101, 0.1f).Shape.ShouldBe("Unfitted");
        }

        [Fact]
        public void PositionAngle_IsNorthZeroEastNinety()
        {
            CoordinateUtils.PositionAngleDeg(0.0, 0.0, 0.0, 10.0).ShouldBe(0.0, 1e-9);
            CoordinateUtils.PositionAngleDeg(0.0, 0.0, 1.0, 0.0).ShouldBe(90.0, 1e-6);
            CoordinateUtils.PositionAngleDeg(0.0, 0.0, 0.0, -10.0).ShouldBe(180.0, 1e-9);
            CoordinateUtils.PositionAngleDeg(1.0, 0.0, 0.0, 0.0).ShouldBe(270.0, 1e-6);
        }

        [Theory]
        [InlineData(-37.9, -60.0, 0.0)]   // southern site, target south of the zenith at transit: zenith due north
        [InlineData(-37.9, -20.0, 180.0)] // target north of the zenith: zenith due south
        [InlineData(50.9, 20.0, 0.0)]     // northern site, target south of the zenith
        [InlineData(50.9, 70.0, 180.0)]
        public void ParallacticAngle_AtTransitPointsAtTheZenith(double latitude, double dec, double expected)
        {
            CoordinateUtils.ParallacticAngleDeg(0.0, dec, latitude).ShouldBe(expected, 1e-9);
        }

        [Fact]
        public void ParallacticAngle_TurnsPositiveAfterTransitForATargetSouthOfTheZenith()
        {
            var q = CoordinateUtils.ParallacticAngleDeg(1.0, -60.0, -37.9);
            q.ShouldBeGreaterThan(0.0);
            q.ShouldBeLessThan(90.0);
            CoordinateUtils.ParallacticAngleDeg(-1.0, -60.0, -37.9).ShouldBe(-q, 1e-9);
        }

        [Fact]
        public void Azimuth_IsSouthAtTransitForATargetSouthOfTheZenith()
        {
            SiteContext.AzimuthDegrees(-37.9, 0.0, -60.0).ShouldBe(180.0, 1e-9);
            SiteContext.AzimuthDegrees(-37.9, 0.0, -20.0).ShouldBe(0.0, 1e-9);
            var west = SiteContext.AzimuthDegrees(-37.9, 2.0, -60.0);
            west.ShouldBeGreaterThan(180.0);
            west.ShouldBeLessThan(360.0);
            SiteContext.AzimuthDegrees(double.NaN, 0.0, 0.0).ShouldBe(double.NaN);
            // The altitude and azimuth of the same point agree with the direction cosines.
            var alt = SiteContext.AltitudeDegrees(-37.9, 2.0, -60.0);
            alt.ShouldBeGreaterThan(0.0);
        }

        [Fact]
        public void WcsPixelAngle_FollowsTheCdMatrixIncludingItsHandedness()
        {
            const double s = 0.001;
            // Conventional north-up, east-left frame: north is +y (row index grows downward), east is -x.
            var northUp = new WCS(6.5, 3.4) { CRPix1 = 100, CRPix2 = 100, CD1_1 = -s, CD1_2 = 0, CD2_1 = 0, CD2_2 = s };
            northUp.SkyPositionAngleToPixelAngleDeg(0).ShouldBe(90.0, 1e-9);
            northUp.SkyPositionAngleToPixelAngleDeg(90).ShouldBe(180.0, 1e-9);
            northUp.SkyPositionAngleToPixelAngleDeg(180).ShouldBe(270.0, 1e-9);
            // 0 and 360 are the same direction; a rounding hair below zero conditions to 360, so compare signed.
            CoordinateUtils.ConditionDegreesSigned(northUp.SkyPositionAngleToPixelAngleDeg(270)).ShouldBe(0.0, 1e-9);
            // Mirrored frame: east is +x now, north unchanged.
            var mirrored = northUp with { CD1_1 = s };
            CoordinateUtils.ConditionDegreesSigned(mirrored.SkyPositionAngleToPixelAngleDeg(90)).ShouldBe(0.0, 1e-9);
            mirrored.SkyPositionAngleToPixelAngleDeg(0).ShouldBe(90.0, 1e-9);
            // Rotated 90 degrees: north along +x.
            var rotated = new WCS(6.5, 3.4) { CRPix1 = 100, CRPix2 = 100, CD1_1 = 0, CD1_2 = s, CD2_1 = s, CD2_2 = 0 };
            CoordinateUtils.ConditionDegreesSigned(rotated.SkyPositionAngleToPixelAngleDeg(0)).ShouldBe(0.0, 1e-9);
            new WCS(6.5, 3.4).SkyPositionAngleToPixelAngleDeg(0).ShouldBe(double.NaN);
        }

        [Fact]
        public void CircularMean_AveragesAcrossTheWrap()
        {
            CoordinateUtils.ConditionDegreesSigned(DatasetGradientReport.CircularMeanDeg([350.0, 10.0])).ShouldBe(0.0, 1e-9);
            DatasetGradientReport.CircularMeanDeg([90.0, double.NaN, 90.0]).ShouldBe(90.0, 1e-9);
            DatasetGradientReport.CircularMeanDeg([double.NaN]).ShouldBe(double.NaN);
        }

        [Fact]
        public async Task MeasureMaster_MasksTheCanvasRingAndRecoversTheRampDirectionAndAmplitude()
        {
            var ct = TestContext.Current.CancellationToken;
            const int W = 256, H = 192, Ring = 8;
            const float Sky = 0.010f, Ramp = 0.004f, Noise = 2e-4f;
            var master = SyntheticMaster(W, H, Ring, Sky, Ramp, Noise, seed: 7);

            var record = await DatasetGradientReport.MeasureMasterAsync(master, "synthetic_master.fits", "Test", 12, solver: null, sweep: true, cancellationToken: ct);
            master.Release();

            record.Master.ShouldBe("synthetic_master.fits");
            record.Channels.ShouldBe(3);
            record.Planes.Length.ShouldBe(3);
            record.Strategy.ShouldBe("Test");
            record.StackedFrames.ShouldBe(12);
            record.Solved.ShouldBeFalse();
            double.IsNaN(record.HorizonAngleInFrameDeg).ShouldBeTrue();
            double.IsNaN(record.BrighteningMinusHorizonDeg).ShouldBeTrue();

            var ringFraction = 1.0 - (double)(W - 2 * Ring) * (H - 2 * Ring) / ((double)W * H);
            record.AbsentFraction.ShouldBe((float)ringFraction, 1e-4f);

            foreach (var plane in record.Planes)
            {
                output.WriteLine($"plane {plane.Plane}: sigma={plane.BackgroundSigma:E2} pp={plane.PeakToPeak:E3} ({plane.PeakToPeakSigma:F1} sigma) angle={plane.GradientAngleDeg:F1} share={plane.LinearShare:F3} shape={plane.Shape} kept={plane.KeptFraction:F3}");
                plane.Shape.ShouldBe("Ramp");
                plane.LinearShare.ShouldBeGreaterThan(0.9f);
                // Brightening along +x; the angle wraps at 360 so read it as a signed offset.
                CoordinateUtils.ConditionDegreesSigned(plane.GradientAngleDeg).ShouldBe(0.0, 4.0);
                // The ramp spans 2 x Ramp across the full width; the ring removes 8 of 256 columns each side.
                var expectedPeakToPeak = 2 * Ramp * (W - 1 - 2 * Ring) / (W - 1);
                plane.PeakToPeak.ShouldBe(expectedPeakToPeak, expectedPeakToPeak * 0.1f);
                plane.BackgroundSigma.ShouldBe(Noise, Noise * 0.25f);
                plane.PeakToPeakSigma.ShouldBe(expectedPeakToPeak / Noise, expectedPeakToPeak / Noise * 0.3f);
                plane.Level.ShouldBe(Sky, 5e-4f);
                plane.Coefficients.Length.ShouldBe(6);
            }
            record.DominantShape.ShouldBe("Ramp");
            CoordinateUtils.ConditionDegreesSigned(record.BrighteningAngleDeg).ShouldBe(0.0, 4.0);

            // The sweep covers both reasoned thresholds plus the mask-off control, and the default's own
            // surface row (10) is among them; on a pure ramp no setting moves the model by more than a sigma.
            record.Sweep.Length.ShouldBe(DatasetGradientReport.StructureThresholdSweep.Length + 1 + DatasetGradientReport.SurfaceThresholdSweep.Length);
            record.Sweep.Count(p => p.Parameter == nameof(BackgroundExtractionOptions.StructureThresholdSigma)).ShouldBe(3);
            record.Sweep.Count(p => p.Parameter == nameof(BackgroundExtractionOptions.ProtectStructure) && p.Value == 0f).ShouldBe(1);
            record.Sweep.Count(p => p.Parameter == nameof(BackgroundExtractionOptions.SurfaceStructureThresholdSigma) && p.Value == 10f).ShouldBe(1);
            foreach (var point in record.Sweep)
            {
                output.WriteLine($"sweep {point.Parameter}={point.Value}: kept={point.KeptFraction:F3} pp={point.PeakToPeakSigma:F1} dRms={point.DeltaRmsSigma:F3} dPp={point.DeltaPeakToPeakSigma:F3}");
                point.DeltaRmsSigma.ShouldBeLessThan(1f);
                point.KeptFraction.ShouldBeGreaterThan(0.5f);
            }
        }

        [Fact]
        public async Task TheStoreKeepsTheLastRecordPerMasterAndTheReportRendersEverySection()
        {
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(_dir);
            var master = SyntheticMaster(128, 96, 4, 0.01f, 0.003f, 2e-4f, seed: 3);
            var first = await DatasetGradientReport.MeasureMasterAsync(master, "m1.fits", "Float16Staged", 20, solver: null, sweep: false, cancellationToken: ct);
            var second = await DatasetGradientReport.MeasureMasterAsync(master, "m2.fits", "BayerDrizzle", 30, solver: null, sweep: true, cancellationToken: ct);
            master.Release();

            var storePath = Path.Combine(_dir, DatasetGradientStore.FileName);
            await DatasetGradientStore.AppendAsync(storePath, first, ct);
            await DatasetGradientStore.AppendAsync(storePath, second, ct);
            await DatasetGradientStore.AppendAsync(storePath, first with { StackedFrames = 21 }, ct);

            var store = await DatasetGradientStore.ReadAsync(storePath, cancellationToken: ct);
            store.Count.ShouldBe(2);
            store["m1.fits"].StackedFrames.ShouldBe(21);
            store["m2.fits"].Sweep.Length.ShouldBe(second.Sweep.Length);
            // NaN survives the round trip (an unsolved master has no frame directions).
            double.IsNaN(store["m1.fits"].HorizonAngleInFrameDeg).ShouldBeTrue();
            store["m1.fits"].Planes[0].Coefficients.Length.ShouldBe(6);

            var reportPath = Path.Combine(_dir, DatasetGradientReport.ReportFileName);
            await DatasetGradientReport.WriteMarkdownAsync(store.Values, reportPath, ct);
            var md = await File.ReadAllTextAsync(reportPath, ct);
            output.WriteLine(md);
            md.ShouldContain("# Dataset Gradient Distribution Report");
            md.ShouldContain("- Masters: 2 (0 plate-solved");
            md.ShouldContain("## Amplitude (per plane, all masters)");
            md.ShouldContain("## Direction (plate-solved masters)");
            md.ShouldContain("## By filter");
            md.ShouldContain("## By camera");
            md.ShouldContain("## Threshold sensitivity");
            md.ShouldContain("Masters swept: 1 of 2.");
            md.ShouldContain("| 3 (default) |");
            md.ShouldContain("| 10 (default) |");
            md.ShouldContain("## Per master");
            md.ShouldContain("| m1 |");
            md.ShouldContain("| m2 |");
            md.ShouldContain("Shape census (planes): Ramp 6.");
        }

        /// <summary>
        /// A three-channel master on a canvas: sky plus a ramp along +x, Gaussian noise, a few stars, and an
        /// exact-zero ring <paramref name="ring"/> pixels wide, as the integrator writes where no frame landed.
        /// </summary>
        private static Image SyntheticMaster(int width, int height, int ring, float sky, float ramp, float noise, int seed)
        {
            var rng = new Random(seed);
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                var plane = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        if (x < ring || y < ring || x >= width - ring || y >= height - ring)
                        {
                            plane[y, x] = 0f;
                            continue;
                        }
                        var u1 = 1.0 - rng.NextDouble();
                        var u2 = rng.NextDouble();
                        var gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                        plane[y, x] = (float)(sky + ramp * BackgroundPolynomial.Normalise(x, width) + noise * gauss);
                    }
                }
                for (var s = 0; s < 40; s++)
                {
                    var cx = ring + 4 + rng.NextDouble() * (width - 2 * ring - 8);
                    var cy = ring + 4 + rng.NextDouble() * (height - 2 * ring - 8);
                    var amp = 0.02f + 0.3f * (float)rng.NextDouble();
                    for (var dy = -4; dy <= 4; dy++)
                    {
                        for (var dx = -4; dx <= 4; dx++)
                        {
                            var px = (int)Math.Round(cx) + dx;
                            var py = (int)Math.Round(cy) + dy;
                            var r2 = (px - cx) * (px - cx) + (py - cy) * (py - cy);
                            plane[py, px] += amp * (float)Math.Exp(-r2 / (2 * 1.5 * 1.5));
                        }
                    }
                }
                planes[c] = plane;
            }
            var meta = new ImageMeta
            {
                Instrument = "SynthCam",
                Telescope = "SynthScope",
                Filter = "SynthFilter",
                ObjectName = "Synth Field",
                SensorType = SensorType.Color,
                ExposureStartTime = new DateTimeOffset(2026, 1, 20, 12, 36, 52, TimeSpan.Zero),
                Latitude = -37.876f,
                Longitude = 145.178f,
                TargetRA = 6.522,
                TargetDec = 3.434,
            };
            return new Image(planes, BitDepth.Float32, 1f, 0f, 0f, meta);
        }
    }
}
