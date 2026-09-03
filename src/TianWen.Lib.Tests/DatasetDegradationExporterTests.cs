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
    }
}
