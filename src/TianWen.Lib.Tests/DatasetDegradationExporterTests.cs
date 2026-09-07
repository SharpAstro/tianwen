using Shouldly;
using System;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The shared degradation exporter against a hand-built bake: a retained linear master, a P0
    /// manifest naming its cells, and the P0 tiles themselves, so the properties under test are the
    /// ones a real run depends on. The load-bearing one is parity: the clean tile this exporter derives
    /// from the RETAINED master must be the bytes P0 wrote from the in-memory one, or a "degraded pair"
    /// is two different frames rather than one frame plus a degradation.
    /// </summary>
    [Collection("Imaging")]
    public class DatasetDegradationExporterTests(ITestOutputHelper output) : IDisposable
    {
        private const int W = 384;
        private const int H = 320;
        private const int TileSize = 256;
        private const string SessionId = "TestCam/None/Target/2026-01-01|TestCam|Target|None";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "tianwen-degrade-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
            GC.SuppressFinalize(this);
        }

        /// <summary>A linear master on an ADU-like scale: sky, a gradient, stars, and noise at the depth
        /// a 64-frame integration would have.</summary>
        private static Image SyntheticMaster(int stackedFrames = 64)
        {
            var rng = new Random(4);
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                var p = new float[H, W];
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        p[y, x] = 900f + (c * 120f) + (140f * x / (W - 1)) + (60f * y / (H - 1));
                    }
                }
                for (var s = 0; s < 40; s++)
                {
                    var cx = (float)(8 + (rng.NextDouble() * (W - 16)));
                    var cy = (float)(8 + (rng.NextDouble() * (H - 16)));
                    var amp = (float)(200 + (rng.NextDouble() * 6000));
                    for (var y = Math.Max(0, (int)cy - 6); y < Math.Min(H, (int)cy + 6); y++)
                    {
                        for (var x = Math.Max(0, (int)cx - 6); x < Math.Min(W, (int)cx + 6); x++)
                        {
                            var dx = x - cx;
                            var dy = y - cy;
                            p[y, x] += amp * MathF.Exp(-((dx * dx) + (dy * dy)) / (2f * 1.6f * 1.6f));
                        }
                    }
                }
                var flat = new float[W * H];
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        flat[(y * W) + x] = p[y, x];
                    }
                }
                LinearDegradation.AddNoiseInPlace(
                    flat,
                    NoiseField.White(W, H, rng),
                    new LinearDegradation.NoiseCalibration(0.0, 1000.0, 40.0, stackedFrames),
                    depthScale: 1.0 / Math.Sqrt(stackedFrames));
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        p[y, x] = flat[(y * W) + x];
                    }
                }
                planes[c] = p;
            }
            return new Image(planes, BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f,
                new ImageMeta { SensorType = SensorType.Color, Gain = 100, ExposureDuration = TimeSpan.FromSeconds(120) });
        }

        /// <summary>
        /// Builds the bake this exporter reads: the retained master, the P0 tiles for two cells, and the
        /// manifest rows describing them, all through the same helpers the real P0 path uses.
        /// </summary>
        private string BuildBake(int stackedFrames = 64)
        {
            var bake = Path.Combine(_root, "bake");
            Directory.CreateDirectory(bake);
            var master = SyntheticMaster(stackedFrames);
            RetainedMasterStore.Write(bake, SessionId, master, frameCount: stackedFrames);

            var slug = DatasetTileExporter.Sanitize(SessionId);
            var tilesDir = Path.Combine(bake, "tiles", slug);
            Directory.CreateDirectory(tilesDir);

            var unit = DatasetTileExporter.ToUnitRange(master);
            var (stretched, applied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(unit);
            applied.ShouldBeTrue("the synthetic master must read as linear or the fixture is not testing the real path");
            origMin.ShouldNotBeNull();
            balances.ShouldNotBeNull();

            var rows = ImmutableArray.CreateBuilder<DatasetTileExporter.TileManifestRow>();
            foreach (var cell in new[] { new Point(0, 0), new Point(W - TileSize, H - TileSize) })
            {
                var file = $"x{cell.X}_y{cell.Y}_master.f16";
                var mad = DatasetTileExporter.WriteTile(stretched, cell, TileSize, Path.Combine(tilesDir, file), SessionId);
                rows.Add(new DatasetTileExporter.TileManifestRow(
                    $"tiles/{slug}/{file}", SessionId, "TestCam", DatasetTileExporter.FrameMaster, "",
                    cell.X, cell.Y, TileSize, 3, 100, 120.0, mad));
                // Two sub rows per cell so the shape measurement has a real pair to compare against, and
                // so the exporter can anchor its injected level on a sub's measured noise. They are made
                // the way a real sub differs from its master -- noise added in LINEAR at one sub's depth,
                // then the SAME stretch -- because the anchor is only testable against a known truth if
                // the fixture's subs carry the noise the fixture claims.
                for (var s = 0; s < 2; s++)
                {
                    var subFile = $"x{cell.X}_y{cell.Y}_s{s:D3}.f16";
                    var noisy = OneSubCopy(unit, origMin, balances, s);
                    var subMad = DatasetTileExporter.WriteTile(noisy, cell, TileSize, Path.Combine(tilesDir, subFile), SessionId);
                    noisy.Release();
                    rows.Add(new DatasetTileExporter.TileManifestRow(
                        $"tiles/{slug}/{subFile}", SessionId, "TestCam", DatasetTileExporter.FrameSub, $"sub{s}.fits",
                        cell.X, cell.Y, TileSize, 3, 100, 120.0, subMad));
                }
            }

            File.WriteAllLines(
                Path.Combine(bake, DatasetTileExporter.ManifestFileName),
                rows.Select(static r => JsonSerializer.Serialize(r)));

            if (!ReferenceEquals(stretched, unit))
            {
                stretched.Release();
            }
            if (!ReferenceEquals(unit, master))
            {
                unit.Release();
            }
            master.Release();
            return bake;
        }

        /// <summary>One sub's worth of noise on the unit-scaled linear master, stretched with the
        /// master's own parameters: what a registered sub of this session would look like.</summary>
        private static Image OneSubCopy(Image unitMaster, float[] origMin, double[] balances, int seed)
        {
            var rng = new Random(100 + seed);
            var planes = new float[unitMaster.ChannelCount][,];
            for (var c = 0; c < unitMaster.ChannelCount; c++)
            {
                var span = unitMaster.GetChannelSpan(c);
                var flat = span.ToArray();
                LinearDegradation.AddNoiseInPlace(
                    flat,
                    NoiseField.White(unitMaster.Width, unitMaster.Height, rng),
                    new LinearDegradation.NoiseCalibration(0.0, OneSubBackgroundUnit, OneSubSigmaUnit, 1),
                    depthScale: 1.0);
                var p = new float[unitMaster.Height, unitMaster.Width];
                for (var y = 0; y < unitMaster.Height; y++)
                {
                    for (var x = 0; x < unitMaster.Width; x++)
                    {
                        p[y, x] = flat[(y * unitMaster.Width) + x];
                    }
                }
                planes[c] = p;
            }
            var linear = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, unitMaster.ImageMeta);
            var stretched = linear.MtfStretchWith(origMin, balances);
            linear.Release();
            return stretched;
        }

        /// <summary>The fixture's truth: one sub's noise at the background, on the unit-scaled linear
        /// scale the exporter works in (40 ADU of 65535, at a background of 1000 ADU).</summary>
        private const double OneSubSigmaUnit = 40.0 / 65535.0;

        private const double OneSubBackgroundUnit = 1000.0 / 65535.0;

        [Fact]
        public async Task TheCleanTileFromTheRetainedMasterIsTheByteThePipelineAlreadyWrote()
        {
            var bake = BuildBake();
            var outDir = Path.Combine(_root, "degraded");

            var result = await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(bake, outDir, Draws: 2, CellsPerSession: 0),
                logger: null,
                TestContext.Current.CancellationToken);

            result.Failed.ShouldBe(0);
            result.Sessions.Length.ShouldBe(1);
            output.WriteLine($"parity against the bake's own master tile: {result.WorstParity:E3}");
            result.WorstParity.ShouldBe(0.0, "a retained master must reproduce the tile P0 exported from the in-memory one");
        }

        [Fact]
        public async Task ADrawIsTheCleanTilePlusNoiseAndNothingElse()
        {
            var bake = BuildBake();
            var outDir = Path.Combine(_root, "degraded");

            var result = await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(bake, outDir, Draws: 3, CellsPerSession: 1, Seed: 5),
                logger: null,
                TestContext.Current.CancellationToken);

            var session = result.Sessions.Single();
            session.CleanTiles.ShouldBe(1);
            session.DegradedTiles.ShouldBe(3);

            var rows = ReadDegradationRows(outDir);
            rows.Length.ShouldBe(3);
            rows.Select(r => r.Seed).Distinct().Count().ShouldBe(3, "every draw needs its own noise field");
            rows.ShouldAllBe(r => r.DepthScale >= 0.1 && r.DepthScale <= 1.5);
            rows.ShouldAllBe(r => r.StackedFrames == 64);
            rows.ShouldAllBe(r => r.ExtraFwhmPx == 0.0);
            rows.ShouldAllBe(r => r.NoiseAnchor == "sub-noisemad");

            // The level anchor is the property most able to be quietly wrong: it crosses domains (a
            // stretched MAD from the manifest into a linear sigma) and nothing downstream would notice a
            // factor. The fixture's subs carry a known one-sub noise, so it can be checked against truth.
            //
            // It reads about 30 percent HIGH here and that is the expected direction: the manifest's
            // NoiseMad is a plain MAD of the whole tile, so the cell's gradient counts as noise. This
            // fixture's ramp is deliberately violent (140 ADU across 384 px against 40 ADU of sub noise,
            // where G1 measured a real master's whole-frame gradient at a median of 2.3 background
            // sigma), so the contamination here is an upper bound rather than a typical one. The
            // conversion ITSELF is pinned separately and to 5 percent, on a flat field, by
            // LinearDegradationTests.TheStretchedNoiseMeasurementConvertsBackToTheLinearSigmaItCameFrom.
            output.WriteLine($"one sub: truth {OneSubSigmaUnit:E3}, recovered {rows[0].OneSubSigma:E3} ({rows[0].OneSubSigma / OneSubSigmaUnit:F2}x)");
            rows[0].OneSubSigma.ShouldBe(OneSubSigmaUnit, OneSubSigmaUnit * 0.45);
            rows[0].OneSubSigma.ShouldBeGreaterThan(OneSubSigmaUnit * 0.9, "the anchor may be contaminated upward by structure, never short of the truth");

            // The draw must differ from the clean tile by noise ONLY: same scene, same level, and a
            // difference whose spread matches the level the row claims to have injected.
            var clean = ReadTile(outDir, CleanTileOf(rows[0]));
            var drawn = ReadTile(outDir, rows[0].Tile);
            var diff = new float[clean.Length];
            for (var i = 0; i < clean.Length; i++)
            {
                diff[i] = drawn[i] - clean[i];
            }
            var mean = diff.Average();
            var sd = Math.Sqrt(diff.Sum(v => (v - mean) * (v - mean)) / diff.Length);
            output.WriteLine($"draw 0: depth {rows[0].DepthScale:F3} x one-sub {rows[0].OneSubSigma:E3}; stretched diff mean {mean:E2}, sd {sd:E3}");

            ((double)Math.Abs(mean)).ShouldBeLessThan(sd, "an injection must not shift the level");
            sd.ShouldBeGreaterThan(0.0);
            // The stretch is monotone, so more injected noise must mean a wider difference; check the
            // ordering across the three draws rather than an absolute value the MTF curve would distort.
            var spreads = rows.Select(r =>
            {
                var d = ReadTile(outDir, r.Tile);
                var s = 0.0;
                for (var i = 0; i < d.Length; i++)
                {
                    var v = d[i] - clean[i];
                    s += v * v;
                }
                return (r.DepthScale, Rms: Math.Sqrt(s / d.Length));
            }).OrderBy(t => t.DepthScale).ToArray();
            foreach (var (depth, rms) in spreads)
            {
                output.WriteLine($"  depth {depth:F3} -> rms {rms:E3}");
            }
            spreads.Zip(spreads.Skip(1)).ShouldAllBe(p => p.Second.Rms > p.First.Rms);
        }

        [Fact]
        public async Task BlurModeWidensStarsAndLabelsTheWidthItAdded()
        {
            var bake = BuildBake();
            var outDir = Path.Combine(_root, "degraded-blur");

            await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(
                    bake, outDir, Mode: DatasetDegradationExporter.DegradationMode.Blur,
                    Draws: 2, CellsPerSession: 1, Seed: 9, MinExtraFwhmPx: 2.5, MaxExtraFwhmPx: 4.0),
                logger: null,
                TestContext.Current.CancellationToken);

            var rows = ReadDegradationRows(outDir);
            rows.ShouldAllBe(r => r.ExtraFwhmPx >= 2.5 && r.ExtraFwhmPx <= 4.0);
            rows.ShouldAllBe(r => r.MoffatBeta >= 1.5 && r.MoffatBeta <= 8.0);
            rows.ShouldAllBe(r => r.Mode == "Blur");

            // A blurred frame's brightest pixel is lower and its faint structure smoother: the peak of
            // the difference must be negative where the stars are.
            var clean = ReadTile(outDir, CleanTileOf(rows[0]));
            var blurred = ReadTile(outDir, rows[0].Tile);
            var cleanPeak = clean.Max();
            var blurredPeak = blurred.Max();
            output.WriteLine($"added {rows[0].ExtraFwhmPx:F2} px (beta {rows[0].MoffatBeta:F2}): peak {cleanPeak:F4} -> {blurredPeak:F4}");
            ((double)blurredPeak).ShouldBeLessThan(cleanPeak, "adding blur must lower the brightest pixel");
        }

        /// <summary>
        /// H2's label, and the invariant that makes it trustworthy: <b>a psf01 never appears without
        /// stars behind it.</b> `HfdPsfEstimator` falls back to a constant default radius when it finds
        /// none, and storing that as a training label would put a number nothing measured into the
        /// column a model conditions on. The exporter writes null instead, so a consumer drops the row.
        /// </summary>
        [Fact]
        public async Task APsf01LabelNeverAppearsWithoutStarsBehindIt()
        {
            var bake = BuildBake();
            var outDir = Path.Combine(_root, "degraded-psf01");

            await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(
                    bake, outDir, Mode: DatasetDegradationExporter.DegradationMode.Blur,
                    Draws: 3, CellsPerSession: 2, Seed: 21, MinExtraFwhmPx: 0.5, MaxExtraFwhmPx: 4.0),
                logger: null,
                TestContext.Current.CancellationToken);

            var rows = ReadDegradationRows(outDir);
            rows.Length.ShouldBeGreaterThan(0);

            foreach (var r in rows)
            {
                output.WriteLine($"added {r.ExtraFwhmPx:F2} px: clean FWHM {r.CleanFwhmPx?.ToString("F2") ?? "none"}, "
                    + $"psf01 estimated {r.Psf01Estimated?.ToString("F3") ?? "null"} from {r.Psf01Stars} stars, "
                    + $"psf01 from kernel {r.Psf01FromKernel?.ToString("F3") ?? "null"}");

                if (r.Psf01Stars == 0)
                {
                    r.Psf01Estimated.ShouldBeNull("the estimator's fallback radius is not a measurement");
                }
                else
                {
                    r.Psf01Estimated.ShouldNotBeNull();
                    r.Psf01Estimated!.Value.ShouldBeInRange(0.0, 1.0);
                }

                // The kernel-side label is available only when the CLEAN cell yielded a width to compose
                // the drawn one into; it is training-only by construction, which is H2's whole point.
                if (r.CleanFwhmPx is null)
                {
                    r.Psf01FromKernel.ShouldBeNull();
                }
                else
                {
                    r.Psf01FromKernel.ShouldNotBeNull();
                    r.Psf01FromKernel!.Value.ShouldBeInRange(0.0, 1.0);
                }
            }
        }

        /// <summary>
        /// The sweep's top is a RATIO to the frame's own width, not a pixel count. E1's oracle leaves a
        /// star about 1.6x too wide past 2x blur, so a draw beyond that teaches a problem nothing can
        /// solve; a fixed pixel cap cannot express that, being roughly 2x on one master and far past it
        /// on a sharper one. Asserted against the cell's OWN measured width, which is the quantity the
        /// bound is relative to.
        /// </summary>
        [Fact]
        public async Task TheBlurSweepIsCappedAgainstTheFramesOwnWidthNotAPixelCount()
        {
            var bake = BuildBake();
            var outDir = Path.Combine(_root, "degraded-ratio-cap");

            await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(
                    bake, outDir, Mode: DatasetDegradationExporter.DegradationMode.Blur,
                    Draws: 8, CellsPerSession: 2, Seed: 5,
                    // A pixel cap far beyond anything the ratio permits, so only the ratio can be what
                    // bounds the draws: without it this asserts the pixel cap and passes for free.
                    MinExtraFwhmPx: 0.5, MaxExtraFwhmPx: 40.0, MaxBlurRatio: 1.5),
                logger: null,
                TestContext.Current.CancellationToken);

            var rows = ReadDegradationRows(outDir).Where(r => r.CleanFwhmPx is > 0).ToArray();
            Assert.SkipWhen(rows.Length == 0, "no row carried a clean width to bound against");

            foreach (var r in rows)
            {
                var own = r.CleanFwhmPx!.Value;
                // The realised width by composition where the row carries it (the ratio draw), else the
                // quadrature the pixel draw was capped by.
                var total = r.ComposedFwhmPx ?? Math.Sqrt((own * own) + (r.ExtraFwhmPx * r.ExtraFwhmPx));
                var ratio = total / own;
                output.WriteLine($"own {own:F2} px + nominal {r.ExtraFwhmPx:F2} = {total:F2} ({ratio:F2}x)");
                ratio.ShouldBeLessThanOrEqualTo(1.5 + 0.02, "the draw must respect the per-frame ratio cap");
            }

            // And the cap must BIND rather than sit unreached, or the assertion above is vacuous.
            rows.Max(r => r.ExtraFwhmPx).ShouldBeGreaterThan(rows.Min(r => r.CleanFwhmPx!.Value) * 0.5);
        }

        /// <summary>
        /// The draw is the blur RATIO and the kernel is solved to realise it as sampled (E1d found the
        /// pixel draw's nominal width under-delivered below 1.5 px: a nominal 1 px kernel blurs like 0.6
        /// to 0.8 px). Pinned two ways: the drawn ratio sits inside the requested range, and the realised
        /// ratio, the clean width composed with the kernel actually built, is within two percent of the
        /// drawn one wherever the pixel clamps did not bind. Read against a floor low enough that the
        /// clamp cannot be what makes it pass.
        /// </summary>
        [Fact]
        public async Task TheDrawIsABlurRatioAndTheSampledKernelRealisesIt()
        {
            var bake = BuildBake();
            var outDir = Path.Combine(_root, "degraded-ratio-draw");

            await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(
                    bake, outDir, Mode: DatasetDegradationExporter.DegradationMode.Blur,
                    Draws: 8, CellsPerSession: 2, Seed: 11,
                    MinExtraFwhmPx: 0.1, MaxExtraFwhmPx: 40.0, MinBlurRatio: 1.05, MaxBlurRatio: 1.6),
                logger: null,
                TestContext.Current.CancellationToken);

            var rows = ReadDegradationRows(outDir).Where(r => r.CleanFwhmPx is > 0).ToArray();
            Assert.SkipWhen(rows.Length == 0, "no row carried a clean width to draw a ratio against");

            foreach (var r in rows)
            {
                r.BlurRatioDrawn.ShouldNotBeNull();
                r.ComposedFwhmPx.ShouldNotBeNull();
                var drawn = r.BlurRatioDrawn.Value;
                var realised = r.ComposedFwhmPx.Value / r.CleanFwhmPx!.Value;
                output.WriteLine($"clean {r.CleanFwhmPx:F2} px, drawn {drawn:F3}x, nominal {r.ExtraFwhmPx:F2} px (beta {r.MoffatBeta:F2}), realised {realised:F3}x");
                drawn.ShouldBeInRange(1.05, 1.6);
                if (r.ExtraFwhmPx > 0.1 + 1e-9)
                {
                    realised.ShouldBe(drawn, drawn * 0.02, "the solved kernel must realise the drawn ratio as sampled");
                }
            }

            // For the record, not asserted: what a quadrature draw would have asked for at the light end.
            // The two corrections pull opposite ways (a heavy-winged Moffat widens the half maximum by
            // MORE than quadrature, the pixel sampling delivers LESS than nominal), so the solved width
            // can sit either side of it; the realised ratio above is the contract.
            foreach (var r in rows.Where(r => r.BlurRatioDrawn is < 1.2))
            {
                var own = r.CleanFwhmPx!.Value;
                var quadrature = own * Math.Sqrt((r.BlurRatioDrawn!.Value * r.BlurRatioDrawn.Value) - 1.0);
                output.WriteLine($"  light end: quadrature would ask {quadrature:F2} px, solved {r.ExtraFwhmPx:F2} px (beta {r.MoffatBeta:F2})");
            }
        }

        /// <summary>
        /// The estimator step's kernel on the row (E3.0 ground-work, pre-registered in
        /// deconvolver-training.md): where both profile fits return, the estimated width is read against the
        /// drawn kernel's EFFECTIVE width on the same core; the pre-registration predicts within 5 percent
        /// from a realised ratio of 1.3x up and kills at 20 percent, and the refusal fraction on 512 px cells
        /// between 20 and 50 percent. Asserted at the kill bound, printed at the prediction, because the
        /// fixture's synthetic plate is not the archive.
        /// </summary>
        [Fact]
        public async Task TheEstimatorsKernelIsWrittenOnTheRowAndReadsTheEffectiveWidth()
        {
            var bake = BuildBake();
            var outDir = Path.Combine(_root, "degraded-kernels");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(
                    bake, outDir, Mode: DatasetDegradationExporter.DegradationMode.Blur,
                    Draws: 8, CellsPerSession: 2, Seed: 17,
                    MinBlurRatio: 1.1, MaxBlurRatio: 2.0, EstimateKernels: true),
                logger: null,
                TestContext.Current.CancellationToken);
            clock.Stop();

            var rows = ReadDegradationRows(outDir).Where(r => r.CleanFwhmPx is > 0).ToArray();
            Assert.SkipWhen(rows.Length == 0, "no row carried a clean width");
            output.WriteLine($"{rows.Length} rows in {clock.Elapsed.TotalSeconds:F1} s ({clock.Elapsed.TotalMilliseconds / Math.Max(1, rows.Length):F0} ms a draw, export included)");

            rows.ShouldAllBe(r => r.KernelSource == "estimated" || r.KernelSource == "drawn");
            rows.ShouldAllBe(r => r.EffectiveKernelFwhmPx.HasValue && r.EffectiveKernelFwhmPx.Value > 0);
            var estimated = rows.Where(r => r.KernelSource == "estimated").ToArray();
            var refused = rows.Length - estimated.Length;
            output.WriteLine($"estimated on {estimated.Length}, refused on {refused} ({100.0 * refused / rows.Length:F0} percent): "
                + string.Join("; ", rows.Where(r => r.KernelSource == "drawn").Select(r => r.KernelEstimateRefusal).Distinct()));
            foreach (var r in rows.OrderBy(r => r.ComposedFwhmPx / r.CleanFwhmPx))
            {
                var ratio = r.ComposedFwhmPx!.Value / r.CleanFwhmPx!.Value;
                output.WriteLine($"  realised {ratio:F3}x: clean fit {r.CleanFitFwhmPx?.ToString("F2") ?? "-"} ({r.CleanFitBeta?.ToString("F1") ?? "-"}), "
                    + $"observed fit {r.ObservedFitFwhmPx?.ToString("F2") ?? "-"} ({r.ObservedFitBeta?.ToString("F1") ?? "-"}), "
                    + $"kernel est {r.EstimatedKernelFwhmPx:F2} ({r.EstimatedKernelBeta:F1}) vs effective {r.EffectiveKernelFwhmPx:F2} (drawn {r.ExtraFwhmPx:F2}, beta {r.MoffatBeta:F1}) [{r.KernelSource}{(r.KernelEstimateRefusal is null ? "" : ": " + r.KernelEstimateRefusal)}]");
            }

            var readable = estimated.Where(r => r.ComposedFwhmPx / r.CleanFwhmPx >= 1.3).ToArray();
            Assert.SkipWhen(readable.Length == 0, "no estimated row at 1.3x or more to read the width against");
            foreach (var r in readable)
            {
                r.EstimatedKernelFwhmPx!.Value.ShouldBe(r.EffectiveKernelFwhmPx!.Value, r.EffectiveKernelFwhmPx.Value * 0.20,
                    "the estimated kernel width must read the effective width within the pre-registered kill bound at 1.3x and up");
            }
        }

        /// <summary>
        /// The solver's contract on its own: a ratio the bracket cannot reach returns the bound, and a
        /// reachable one is realised to a thousandth on a continuous-width core.
        /// </summary>
        [Theory]
        [InlineData(2.15, 1.10, 3.0)]
        [InlineData(2.15, 1.50, 2.5)]
        [InlineData(1.53, 1.30, 4.0)]
        [InlineData(2.81, 1.05, 6.0)]
        public void TheNominalWidthSolvedForARatioRealisesIt(double cleanFwhm, double ratio, double beta)
        {
            // The production bracket (the exporter's pixel bounds), timed, because the solve runs once
            // per draw and a full export is two hundred thousand of them.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var nominal = DatasetDegradationExporter.SolveNominalFwhmForRatio(cleanFwhm, ratio, beta, 1.0, 0.0, 0.5, 4.0);
            clock.Stop();
            var realised = TianWen.Lib.Imaging.Degradation.MoffatComposition.ComposedFwhm(
                cleanFwhm, TianWen.Lib.Imaging.Degradation.MoffatComposition.DefaultCoreBeta,
                TianWen.Lib.Imaging.Degradation.PsfKernel.Moffat(nominal, beta)) / cleanFwhm;
            var quadrature = cleanFwhm * Math.Sqrt((ratio * ratio) - 1.0);
            output.WriteLine($"core {cleanFwhm} px beta {beta}: ratio {ratio} needs nominal {nominal:F3} px (quadrature {quadrature:F3}), realised {realised:F4}x, solved in {clock.Elapsed.TotalMilliseconds:F1} ms");
            realised.ShouldBe(ratio, 0.005);

            // The bounds are honoured: a target below what the floor kernel gives returns the floor.
            DatasetDegradationExporter.SolveNominalFwhmForRatio(cleanFwhm, 1.0001, beta, 1.0, 0.0, 2.0, 4.0).ShouldBe(2.0);
        }

        /// <summary>
        /// The label has to MOVE with the blur, or it carries no information for the model to condition
        /// on. Skipped rather than asserted when the fixture yields no measurement, because a synthetic
        /// plate is not guaranteed to present stars the detector accepts, and a silently vacuous
        /// assertion is worse than an honest skip.
        /// </summary>
        [Fact]
        public async Task ThePsf01LabelRisesWithTheInjectedBlur()
        {
            var bake = BuildBake();
            var outDir = Path.Combine(_root, "degraded-psf01-monotone");

            await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(
                    bake, outDir, Mode: DatasetDegradationExporter.DegradationMode.Blur,
                    Draws: 8, CellsPerSession: 2, Seed: 33, MinExtraFwhmPx: 0.5, MaxExtraFwhmPx: 4.0),
                logger: null,
                TestContext.Current.CancellationToken);

            var measured = ReadDegradationRows(outDir)
                .Where(r => r.Psf01Estimated is not null)
                .OrderBy(r => r.ExtraFwhmPx)
                .ToArray();

            Assert.SkipWhen(measured.Length < 6, $"only {measured.Length} rows carried a measurement");

            var half = measured.Length / 2;
            var gentle = measured.Take(half).Average(r => r.Psf01Estimated!.Value);
            var heavy = measured.Skip(measured.Length - half).Average(r => r.Psf01Estimated!.Value);
            output.WriteLine($"psf01 over the gentlest {half} draws {gentle:F3}, over the heaviest {half} {heavy:F3}");
            heavy.ShouldBeGreaterThan(gentle, "a conditioning label that does not move with the blur conditions on nothing");
        }

        /// <summary>
        /// Subsetting cells must SAMPLE, not take a prefix: the P0 cells arrive sorted row-major, so a
        /// prefix is the top of the canvas and a training set drawn from it sees one edge of every
        /// frame. Seeded, so the same cells come back on a re-run.
        /// </summary>
        [Fact]
        public async Task SubsettingCellsSamplesInsteadOfTakingTheTopOfTheFrame()
        {
            var bake = BuildBake();
            var outA = Path.Combine(_root, "subset-a");
            var outB = Path.Combine(_root, "subset-b");
            var options = new DatasetDegradationExporter.Options(bake, outA, Draws: 1, CellsPerSession: 1, Seed: 3);

            await DatasetDegradationExporter.RunAsync(options, logger: null, TestContext.Current.CancellationToken);
            await DatasetDegradationExporter.RunAsync(options with { OutDir = outB }, logger: null, TestContext.Current.CancellationToken);

            var a = ReadDegradationRows(outA).Single();
            var b = ReadDegradationRows(outB).Single();
            (a.CellX, a.CellY).ShouldBe((b.CellX, b.CellY), "the same seed must pick the same cell");

            // The fixture's two cells are (0,0) and the bottom-right one; a prefix would always pick
            // (0,0), so a seed that lands on the other one is what proves this samples.
            var seeds = Enumerable.Range(1, 12).Select(async s =>
            {
                var dir = Path.Combine(_root, $"subset-s{s}");
                await DatasetDegradationExporter.RunAsync(options with { OutDir = dir, Seed = s }, logger: null, TestContext.Current.CancellationToken);
                var row = ReadDegradationRows(dir).Single();
                return (row.CellX, row.CellY);
            });
            var picked = (await Task.WhenAll(seeds)).Distinct().ToArray();
            output.WriteLine($"cells picked across 12 seeds: {string.Join(", ", picked.Select(p => $"({p.CellX},{p.CellY})"))}");
            picked.Length.ShouldBeGreaterThan(1, "a prefix would pick the same cell for every seed");
        }

        /// <summary>
        /// The level the model is DEPLOYED at must be interior to the range it is trained across, or the
        /// conditioning plane reads an out-of-distribution level at inference: the H0 domain skew again,
        /// one layer up. Deployment depth is the master's own 1/sqrt(StackedFrames) and it varies by a
        /// factor of four across one bake, so the bottom of the range is derived per session rather than
        /// fixed. Measured on the real pool before this was pinned: the fixed 0.1 floor sat ABOVE the
        /// master depth of 34 of 51 sessions.
        /// </summary>
        [Theory]
        [InlineData(15)]    // a shallow session: master depth 0.258, above the 0.1 clamp
        [InlineData(64)]    // 0.125
        [InlineData(257)]   // the deepest in the organized pool: 0.062
        public async Task TheDeploymentDepthIsInteriorToTheInjectedRange(int stackedFrames)
        {
            var bake = BuildBake(stackedFrames);
            var outDir = Path.Combine(_root, $"depth-{stackedFrames}");

            await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(bake, outDir, Draws: 24, CellsPerSession: 1, Seed: 7),
                logger: null,
                TestContext.Current.CancellationToken);

            var rows = ReadDegradationRows(outDir);
            var masterDepth = 1.0 / Math.Sqrt(stackedFrames);
            rows.ShouldAllBe(r => r.MasterDepth == masterDepth);
            var lowest = rows.Min(r => r.DepthScale);
            output.WriteLine($"N={stackedFrames}: master depth {masterDepth:F3}, drawn depths {lowest:F3} to {rows.Max(r => r.DepthScale):F3}");
            lowest.ShouldBeLessThan(masterDepth, "the deployed level must sit inside the range, not at or past its edge");
        }

        [Fact]
        public async Task TheExportIsResumableAndSkipsWhatItAlreadyDid()
        {
            var bake = BuildBake();
            var outDir = Path.Combine(_root, "degraded-resume");
            var options = new DatasetDegradationExporter.Options(bake, outDir, Draws: 1, CellsPerSession: 1);

            var first = await DatasetDegradationExporter.RunAsync(options, logger: null, TestContext.Current.CancellationToken);
            var second = await DatasetDegradationExporter.RunAsync(options, logger: null, TestContext.Current.CancellationToken);

            first.Sessions.Length.ShouldBe(1);
            second.Sessions.Length.ShouldBe(0);
            second.Skipped.ShouldBe(1);
            ReadDegradationRows(outDir).Length.ShouldBe(1, "a skipped session must not append its rows twice");
        }

        [Fact]
        public async Task ASessionFilterNamesThePoolWithoutExportingTheBake()
        {
            var bake = BuildBake();
            var outDir = Path.Combine(_root, "degraded-filtered");

            var none = await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(bake, outDir, Draws: 1, CellsPerSession: 1, SessionFilters: ["Another-Object"]),
                logger: null,
                TestContext.Current.CancellationToken);
            var matched = await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(bake, outDir, Draws: 1, CellsPerSession: 1, SessionFilters: ["Another-Object", "testcam/none/target"]),
                logger: null,
                TestContext.Current.CancellationToken);

            none.Sessions.Length.ShouldBe(0, "a filter nothing matches exports nothing, and is not an error");
            none.Skipped.ShouldBe(0);
            matched.Sessions.Length.ShouldBe(1, "the match is a case-insensitive substring of the session id");
            matched.Sessions[0].SessionId.ShouldBe(SessionId);
        }

        [Fact]
        public async Task WritingTheCacheIntoTheBakeIsRefused()
        {
            var bake = BuildBake();

            await Should.ThrowAsync<ArgumentException>(async () => await DatasetDegradationExporter.RunAsync(
                new DatasetDegradationExporter.Options(bake, bake),
                logger: null,
                TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task TheShapeMeasurementSeparatesTheTwoInjectionArms()
        {
            var bake = BuildBake();
            var white = Path.Combine(_root, "shape-white");
            var warped = Path.Combine(_root, "shape-warped");
            var baseOptions = new DatasetDegradationExporter.Options(bake, white, Draws: 2, CellsPerSession: 0, Seed: 21);

            await DatasetDegradationExporter.RunAsync(baseOptions, logger: null, TestContext.Current.CancellationToken);
            await DatasetDegradationExporter.RunAsync(
                baseOptions with { OutDir = warped, Shape = DatasetDegradationExporter.NoiseShape.Warped },
                logger: null,
                TestContext.Current.CancellationToken);

            var whiteShape = await DatasetDegradationExporter.MeasureShapeAsync(white, bake, cancellationToken: TestContext.Current.CancellationToken);
            var warpedShape = await DatasetDegradationExporter.MeasureShapeAsync(warped, bake, cancellationToken: TestContext.Current.CancellationToken);

            foreach (var m in whiteShape.Concat(warpedShape))
            {
                output.WriteLine($"{m.Label,-24} pairs {m.Pairs,3}  band0 {m.Band0:E2}  band1 {m.Band1:E2}  band1/band0 {m.Ratio:F3}");
            }

            var w = whiteShape.Single(m => m.Label == "injected draws");
            var p = warpedShape.Single(m => m.Label == "injected draws");
            w.Pairs.ShouldBeGreaterThan(0);
            p.Ratio.ShouldBeGreaterThan(w.Ratio, "the warped arm has to be measurably smoother than the white one, or the arms are one experiment");
            whiteShape.ShouldContain(m => m.Label == "real sub pairs" && m.Pairs > 0);
        }

        private static ImmutableArray<DatasetDegradationExporter.DegradationRow> ReadDegradationRows(string outDir)
        {
            var path = Path.Combine(outDir, DatasetDegradationExporter.DegradationManifestFileName);
            return [.. File.ReadAllLines(path)
                .Where(static l => l.Length > 0)
                .Select(static l => JsonSerializer.Deserialize<DatasetDegradationExporter.DegradationRow>(l)!)];
        }

        /// <summary>The clean tile that pairs with a degraded row. Derived from the ROW's cell, never
        /// hardcoded: which cell a session contributes is a seeded sample, so a fixed name is a test
        /// that passes on the luck of the seed.</summary>
        private static string CleanTileOf(DatasetDegradationExporter.DegradationRow row)
            => $"tiles/{DatasetTileExporter.Sanitize(row.SessionId)}/x{row.CellX}_y{row.CellY}_{DatasetDegradationExporter.FrameClean}.f16";

        private static float[] ReadTile(string root, string relative)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var values = new float[TileSize * TileSize];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (float)BitConverter.ToHalf(bytes, i * 2);
            }
            return values;
        }

        /// <summary>
        /// H3's arm, and the reason it exists. A master already carries a blue/green width ratio near
        /// 1.32; adding ONE kernel to all three channels composes the same width into both and drives
        /// that ratio toward 1 as the blur grows, which is channel structure the archive does not show.
        /// Scaling the added width per channel holds it. This asserts the DIFFERENCE between the two
        /// arms rather than either one's absolute number, because that difference is the hypothesis.
        /// </summary>
        [Theory]
        [InlineData(1.0)]
        [InlineData(2.0)]
        [InlineData(4.0)]
        public void APerChannelDrawHoldsTheChannelWidthRatioWhereASharedKernelCollapsesIt(double extraFwhm)
        {
            // The archive's median per-channel master widths (blue, green), which the exporter composes
            // the drawn blur into in quadrature.
            const double OwnBlue = 2.47;
            const double OwnGreen = 1.80;
            var atRest = OwnBlue / OwnGreen;

            static double Compose(double own, double added) => Math.Sqrt((own * own) + (added * added));

            var shared = Compose(OwnBlue, extraFwhm) / Compose(OwnGreen, extraFwhm);

            var rng = new Random(7);
            var blue = DatasetDegradationExporter.PerChannelKernel(rng, 0, extraFwhm, 1.0, 0.0);
            var green = DatasetDegradationExporter.PerChannelKernel(rng, 1, extraFwhm, 1.0, 0.0);
            var perChannel = Compose(OwnBlue, blue.Fwhm) / Compose(OwnGreen, green.Fwhm);

            output.WriteLine($"added {extraFwhm:F1} px: at rest {atRest:F3}, shared kernel {shared:F3}, per-channel {perChannel:F3}");

            // The shared arm always loses ground, and the more blur the more it loses.
            shared.ShouldBeLessThan(atRest);
            // The per-channel arm stays close to the archive's own ratio, and beats the shared arm at
            // every blur in the sweep.
            perChannel.ShouldBeGreaterThan(shared);
            (perChannel / atRest).ShouldBe(1.0, 0.06);
        }

        /// <summary>
        /// Beta must land in the family the archive shows and the plan permits: never a Gaussian
        /// (the clamp's top), never lighter-winged than any measured master. The draw is log-normal
        /// about a fitted line, so an unclamped tail would reach both.
        /// </summary>
        [Fact]
        public void ThePerChannelBetaStaysInsideTheMeasuredFamily()
        {
            var rng = new Random(11);
            for (var i = 0; i < 400; i++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var k = DatasetDegradationExporter.PerChannelKernel(rng, c, 0.5 + (rng.NextDouble() * 3.5), 1.0, 0.0);
                    k.Beta.ShouldBeGreaterThanOrEqualTo(1.5);
                    k.Beta.ShouldBeLessThanOrEqualTo(20.0);
                }
            }
        }
    }
}
