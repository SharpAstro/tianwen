using Shouldly;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The cross-night exporter against two synthetic nights of ONE sky whose relation is known exactly:
    /// the same stars and nebula rendered on night B's grid through a chosen rotation and shift, at a
    /// chosen gain, under a different sky level and gradient, with independent noise. Every number the
    /// exporter records (rotation, gain, PSF, the residual noise correlation) therefore has a truth to
    /// be checked against, which no real pair offers.
    /// </summary>
    [Collection("Imaging")]
    public class DatasetCrossNightExporterTests(ITestOutputHelper output) : IDisposable
    {
        private const int W = 640;
        private const int H = 560;
        private const string NightA = "TestCam/None/Target/2026-01-05|TestCam|Target|None";
        private const string NightB = "TestCam/None/Target/2026-01-06|TestCam|Target|None";
        private const double RotationDeg = 0.6;
        private const float ShiftX = 7.3f;
        private const float ShiftY = -4.1f;
        private const float GainB = 1.25f;
        private const float NoiseSigma = 12f;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "tianwen-crossnight-tests", Guid.NewGuid().ToString("N"));

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

        private readonly record struct Sky(float X, float Y, float Amp);

        /// <summary>The sky both nights see: 140 stars and one broad nebula, in night A's pixel frame.</summary>
        private static (Sky[] Stars, Sky Nebula) SharedSky()
        {
            var rng = new Random(7);
            var stars = new Sky[140];
            for (var s = 0; s < stars.Length; s++)
            {
                stars[s] = new Sky(
                    (float)(20 + (rng.NextDouble() * (W - 40))),
                    (float)(20 + (rng.NextDouble() * (H - 40))),
                    (float)(600 + (rng.NextDouble() * 7000)));
            }
            return (stars, new Sky(0.62f * W, 0.41f * H, 350f));
        }

        /// <summary>Night A's pixel position on night B's grid: a rotation about the frame centre plus a shift.</summary>
        private static Vector2 ToNightB(Vector2 p, double rotationDeg, float shiftX, float shiftY)
        {
            var c = new Vector2(W / 2f, H / 2f);
            var m = Matrix3x2.CreateRotation((float)(rotationDeg * Math.PI / 180.0), c) * Matrix3x2.CreateTranslation(shiftX, shiftY);
            return Vector2.Transform(p, m);
        }

        /// <summary>
        /// One night's linear master. <paramref name="sharedNoise"/> hands back the noise field so a control
        /// pair can share it; <paramref name="noise"/> supplies one to share.
        /// </summary>
        private static Image RenderNight(
            (Sky[] Stars, Sky Nebula) sky, bool isNightB, float gain, float level, float gradX, float gradY,
            float fwhmPx, int seed, float[][]? noise, out float[][] sharedNoise)
        {
            var rng = new Random(seed);
            var sigma = fwhmPx / 2.354820045f;
            var planes = new float[3][,];
            sharedNoise = new float[3][];
            for (var c = 0; c < 3; c++)
            {
                var p = new float[H, W];
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        p[y, x] = level + (c * 80f) + (gradX * x / (W - 1)) + (gradY * y / (H - 1));
                    }
                }
                void Blob(Vector2 centre, float amp, float s, int radius)
                {
                    for (var y = Math.Max(0, (int)centre.Y - radius); y < Math.Min(H, (int)centre.Y + radius + 1); y++)
                    {
                        for (var x = Math.Max(0, (int)centre.X - radius); x < Math.Min(W, (int)centre.X + radius + 1); x++)
                        {
                            var dx = x - centre.X;
                            var dy = y - centre.Y;
                            p[y, x] += amp * MathF.Exp(-((dx * dx) + (dy * dy)) / (2f * s * s));
                        }
                    }
                }
                foreach (var star in sky.Stars)
                {
                    var pos = new Vector2(star.X, star.Y);
                    if (isNightB)
                    {
                        pos = ToNightB(pos, RotationDeg, ShiftX, ShiftY);
                    }
                    Blob(pos, star.Amp * gain * (1f + (0.15f * c)), sigma, (int)MathF.Ceiling(4 * sigma));
                }
                var neb = new Vector2(sky.Nebula.X, sky.Nebula.Y);
                if (isNightB)
                {
                    neb = ToNightB(neb, RotationDeg, ShiftX, ShiftY);
                }
                Blob(neb, sky.Nebula.Amp * gain, 55f, 200);

                var field = new float[W * H];
                for (var i = 0; i < field.Length; i++)
                {
                    var u1 = 1.0 - rng.NextDouble();
                    var u2 = rng.NextDouble();
                    field[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                }
                sharedNoise[c] = field;
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        var n = field[(y * W) + x];
                        if (noise is not null)
                        {
                            // Half shared, half own: the correlation an honest statistic must read as ~0.7.
                            n = noise[c][(y * W) + x] + n;
                        }
                        p[y, x] += NoiseSigma * gain * n;
                    }
                }
                planes[c] = p;
            }
            return new Image(planes, BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f,
                new ImageMeta { SensorType = SensorType.Color, Gain = 100, ExposureDuration = TimeSpan.FromSeconds(120) });
        }

        /// <summary>A bake holding the two nights' retained masters and a manifest row each (the exporter
        /// reads camera, gain and exposure from it).</summary>
        private string BuildBake(Image a, Image b, params (string Id, Image Master)[] extra)
        {
            var bake = Path.Combine(_root, "bake");
            Directory.CreateDirectory(bake);
            var rows = ImmutableArray.CreateBuilder<DatasetTileExporter.TileManifestRow>();
            foreach (var (id, master) in new[] { (NightA, a), (NightB, b) }.Concat(extra))
            {
                RetainedMasterStore.Write(bake, id, master, frameCount: 64);
                rows.Add(new DatasetTileExporter.TileManifestRow(
                    $"tiles/{DatasetTileExporter.Sanitize(id)}/x0_y0_master.f16", id, "TestCam", DatasetTileExporter.FrameMaster, "",
                    0, 0, 256, 3, 100, 120.0, 0.01));
            }
            File.WriteAllLines(Path.Combine(bake, DatasetTileExporter.ManifestFileName), rows.Select(static r => JsonSerializer.Serialize(r)));
            return bake;
        }

        private static float[] ReadTile(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var halfs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes);
            var result = new float[halfs.Length];
            for (var i = 0; i < halfs.Length; i++)
            {
                result[i] = (float)halfs[i];
            }
            return result;
        }

        [Fact]
        public async Task TwoNightsOfOneSkyBecomeOnePairWhoseRecordedGeometryGainAndNoiseMatchTheTruth()
        {
            var ct = TestContext.Current.CancellationToken;
            var sky = SharedSky();
            var a = RenderNight(sky, isNightB: false, gain: 1f, level: 900f, gradX: 140f, gradY: 60f, fwhmPx: 3f, seed: 11, noise: null, out _);
            var b = RenderNight(sky, isNightB: true, gain: GainB, level: 1300f, gradX: -50f, gradY: 90f, fwhmPx: 3f, seed: 12, noise: null, out _);
            var bake = BuildBake(a, b);
            var outDir = Path.Combine(_root, "pairs");

            var result = await DatasetCrossNightExporter.RunAsync(
                new DatasetCrossNightExporter.Options(bake, outDir, Pairs: [new DatasetCrossNightExporter.PairSpec(NightA, NightB)], CellsPerPair: 6),
                cancellationToken: ct);

            result.Skipped.ShouldBeEmpty(string.Join("; ", result.Skipped));
            result.Failed.ShouldBe(0);
            result.Pairs.Length.ShouldBe(1);
            var pair = result.Pairs[0];
            output.WriteLine(JsonSerializer.Serialize(pair));

            // Geometry: the fitted B -> A transform undoes the rotation the fixture applied.
            Math.Abs(Math.Abs(pair.RotationDeg) - RotationDeg).ShouldBeLessThan(0.05);
            Math.Abs(pair.Scale - 1.0).ShouldBeLessThan(0.005);
            pair.RegistrationRmsPx.ShouldBeLessThan(0.5);
            pair.OverlapFraction.ShouldBeGreaterThan(0.9);

            // Gain: rule 5, per channel, against the gain the fixture rendered night B at.
            foreach (var g in pair.Gain)
            {
                Math.Abs(g - GainB).ShouldBeLessThan(0.04, $"gain {g}");
            }

            // PSF: both nights were rendered at 3 px and neither was convolved.
            pair.BlurredSide.ShouldBe("");
            Math.Abs(pair.FwhmA - pair.FwhmB).ShouldBeLessThan(0.15);
            Math.Abs(pair.FwhmAfterA - pair.FwhmAfterB).ShouldBeLessThan(0.2);

            // Independent noise: the residual correlation is the number H8 rides on and it must read zero
            // here, on every channel.
            foreach (var r in pair.ResidualCorrelation)
            {
                double.IsFinite(r).ShouldBeTrue();
                Math.Abs(r).ShouldBeLessThan(0.05, $"residual correlation {r}");
            }

            // Output shape: three frames per cell, half slots carrying the two nights' ids, tiles present.
            var rows = File.ReadAllLines(result.TileManifestPath)
                .Select(l => JsonSerializer.Deserialize<DatasetTileExporter.TileManifestRow>(l)!)
                .ToArray();
            rows.Length.ShouldBe(pair.Cells * 3);
            rows.Count(r => r.Frame == DatasetTileExporter.FrameMaster).ShouldBe(pair.Cells);
            rows.Where(r => r.Frame == DatasetTileExporter.FrameHalfMasterA).ShouldAllBe(r => r.SourceFile == NightA);
            rows.Where(r => r.Frame == DatasetTileExporter.FrameHalfMasterB).ShouldAllBe(r => r.SourceFile == NightB);
            rows.ShouldAllBe(r => r.SessionId == pair.PairId);
            foreach (var row in rows)
            {
                File.Exists(Path.Combine(outDir, row.Tile.Replace('/', Path.DirectorySeparatorChar))).ShouldBeTrue(row.Tile);
            }

            // The two sides of one cell are the same sky: finite, non-zero, and strongly correlated pixel
            // for pixel, which is what registration onto one grid means.
            var cell = rows.First(r => r.Frame == DatasetTileExporter.FrameHalfMasterA);
            var tileA = ReadTile(Path.Combine(outDir, cell.Tile.Replace('/', Path.DirectorySeparatorChar)));
            var tileB = ReadTile(Path.Combine(outDir, cell.Tile.Replace("halfmaster_a", "halfmaster_b").Replace('/', Path.DirectorySeparatorChar)));
            tileA.ShouldAllBe(v => float.IsFinite(v));
            tileA.Count(v => v != 0f).ShouldBeGreaterThan(tileA.Length / 2);
            Pearson(tileA, tileB).ShouldBeGreaterThan(0.9);

            File.Exists(result.PairManifestPath).ShouldBeTrue();
        }

        [Fact]
        public async Task SharedNoiseReadsAsCorrelationSoTheStatisticCanTellAnHonestPairFromASameSessionOne()
        {
            var ct = TestContext.Current.CancellationToken;
            var sky = SharedSky();
            // Aligned nights (the shared field must land on the same pixels), half of B's noise being A's.
            var a = RenderNight(sky, isNightB: false, gain: 1f, level: 900f, gradX: 140f, gradY: 60f, fwhmPx: 3f, seed: 21, noise: null, out var noiseA);
            var b = RenderNight(sky, isNightB: false, gain: 1f, level: 1100f, gradX: 20f, gradY: -40f, fwhmPx: 3f, seed: 22, noise: noiseA, out _);
            var bake = BuildBake(a, b);

            var result = await DatasetCrossNightExporter.RunAsync(
                new DatasetCrossNightExporter.Options(bake, Path.Combine(_root, "pairs"), Pairs: [new DatasetCrossNightExporter.PairSpec(NightA, NightB)], CellsPerPair: 4),
                cancellationToken: ct);

            result.Pairs.Length.ShouldBe(1, string.Join("; ", result.Skipped));
            var pair = result.Pairs[0];
            output.WriteLine(JsonSerializer.Serialize(pair));
            // A's noise is sigma^2 of B's 2 sigma^2, so the expected correlation is 1/sqrt(2).
            foreach (var r in pair.ResidualCorrelation)
            {
                r.ShouldBeGreaterThan(0.55, $"residual correlation {r}");
                r.ShouldBeLessThan(0.85, $"residual correlation {r}");
            }
        }

        [Fact]
        public async Task AMismatchedPsfIsRefusedByDefaultAndConvolvedToTheWiderNightOnRequest()
        {
            var ct = TestContext.Current.CancellationToken;
            var sky = SharedSky();
            var a = RenderNight(sky, isNightB: false, gain: 1f, level: 900f, gradX: 140f, gradY: 60f, fwhmPx: 3f, seed: 31, noise: null, out _);
            var b = RenderNight(sky, isNightB: true, gain: 1f, level: 1000f, gradX: 0f, gradY: 0f, fwhmPx: 3.6f, seed: 32, noise: null, out _);
            var bake = BuildBake(a, b);
            var pairs = ImmutableArray.Create(new DatasetCrossNightExporter.PairSpec(NightA, NightB));

            var refused = await DatasetCrossNightExporter.RunAsync(
                new DatasetCrossNightExporter.Options(bake, Path.Combine(_root, "refused"), Pairs: pairs, CellsPerPair: 4), cancellationToken: ct);
            refused.Pairs.ShouldBeEmpty();
            refused.Skipped.Length.ShouldBe(1);
            refused.Skipped[0].ShouldContain("FWHM");

            var matched = await DatasetCrossNightExporter.RunAsync(
                new DatasetCrossNightExporter.Options(bake, Path.Combine(_root, "matched"), Pairs: pairs, CellsPerPair: 4, PsfMatch: true), cancellationToken: ct);
            matched.Pairs.Length.ShouldBe(1, string.Join("; ", matched.Skipped));
            var pair = matched.Pairs[0];
            output.WriteLine(JsonSerializer.Serialize(pair));
            pair.BlurredSide.ShouldBe("A");
            // Quadrature difference of 3.6 and 3.0 px, read off the detector's own widths.
            pair.BlurFwhmPx.ShouldBeInRange(1.5, 2.5);
            // After the convolution the two exported sides are within the matched tolerance of each other.
            (Math.Abs(pair.FwhmAfterA - pair.FwhmAfterB) / Math.Max(pair.FwhmAfterA, pair.FwhmAfterB)).ShouldBeLessThan(0.06);
        }

        [Fact]
        public async Task TheExportIsResumableAndRefusesToWriteIntoTheBake()
        {
            var ct = TestContext.Current.CancellationToken;
            var sky = SharedSky();
            var a = RenderNight(sky, isNightB: false, gain: 1f, level: 900f, gradX: 140f, gradY: 60f, fwhmPx: 3f, seed: 41, noise: null, out _);
            var b = RenderNight(sky, isNightB: true, gain: 1f, level: 950f, gradX: 10f, gradY: 10f, fwhmPx: 3f, seed: 42, noise: null, out _);
            var bake = BuildBake(a, b);
            var outDir = Path.Combine(_root, "pairs");
            var options = new DatasetCrossNightExporter.Options(bake, outDir, Pairs: [new DatasetCrossNightExporter.PairSpec(NightA, NightB)], CellsPerPair: 3);

            var first = await DatasetCrossNightExporter.RunAsync(options, cancellationToken: ct);
            first.Pairs.Length.ShouldBe(1, string.Join("; ", first.Skipped));
            var lines = File.ReadAllLines(first.TileManifestPath).Length;

            var second = await DatasetCrossNightExporter.RunAsync(options, cancellationToken: ct);
            second.Pairs.ShouldBeEmpty();
            second.Skipped.ShouldBeEmpty();
            File.ReadAllLines(second.TileManifestPath).Length.ShouldBe(lines);

            await Should.ThrowAsync<ArgumentException>(() => DatasetCrossNightExporter.RunAsync(options with { OutDir = bake }, cancellationToken: ct));
        }

        [Fact]
        public void DiscoveryPairsNightsOfOneObjectAndNeverTwoSessionsOfOneNight()
        {
            var tiny = new Image(new[] { new float[16, 16], new float[16, 16], new float[16, 16] }, BitDepth.Float32, 1f, 0f, 0f,
                new ImageMeta { SensorType = SensorType.Color });
            const string sameNightOtherObject = "TestCam/None/Other/2026-01-05|TestCam|Other|None";
            const string otherFilter = "TestCam/Ha/Target/2026-01-07|TestCam|Target|Ha";
            const string thirdNight = "TestCam/None/Target/2026-01-08|TestCam|Target|None";
            const string flagged = "TestCam/None/Target/2026-01-09-baddark|TestCam|Target|None";
            var bake = BuildBake(tiny, tiny, (sameNightOtherObject, tiny), (otherFilter, tiny), (thirdNight, tiny), (flagged, tiny));
            var ids = new[] { NightA, NightB, sameNightOtherObject, otherFilter, thirdNight, flagged, "TestCam/None/Ghost/2026-02-01|TestCam|Ghost|None" };

            var pairs = DatasetCrossNightExporter.DiscoverPairs(ids,
                new DatasetCrossNightExporter.Options(bake, Path.Combine(_root, "x"), Exclude: ["baddark"]));

            // Target/None on three nights: three pairs. Other and Ha stand alone; the flagged night and the
            // session with no retained master never enter.
            pairs.Length.ShouldBe(3);
            pairs.ShouldAllBe(p => p.SessionA.Contains("|Target|None") && p.SessionB.Contains("|Target|None"));
            pairs.ShouldAllBe(p => !p.SessionA.Contains("baddark") && !p.SessionB.Contains("baddark"));
            pairs.ShouldAllBe(p => DatasetCrossNightExporter.NightLabel(p.SessionA) != DatasetCrossNightExporter.NightLabel(p.SessionB));

            var filtered = DatasetCrossNightExporter.DiscoverPairs(ids,
                new DatasetCrossNightExporter.Options(bake, Path.Combine(_root, "x"), ObjectFilters: ["ghost"]));
            filtered.ShouldBeEmpty();
        }

        [Fact]
        public void APairIdKeepsTheSessionIdShapeWithBothNightsInTheFolder()
        {
            var id = DatasetCrossNightExporter.PairIdFor(new DatasetCrossNightExporter.PairSpec(NightA, NightB));
            id.ShouldBe("TestCam/None/Target/2026-01-05+2026-01-06|TestCam|Target|None");
            DatasetCrossNightExporter.NightLabel(NightA).ShouldBe("2026-01-05");
            var (folder, camera, obj, filter) = DatasetCrossNightExporter.SplitSessionId(id);
            camera.ShouldBe("TestCam");
            obj.ShouldBe("Target");
            filter.ShouldBe("None");
            folder.ShouldEndWith("2026-01-05+2026-01-06");
        }

        [Theory]
        [InlineData(0.6, 7.3f, -4.1f, 1.0)]
        [InlineData(-3.0, -120f, 35f, 1.002)]
        [InlineData(0.0, 0.5f, 0.5f, 1.0)]
        public void TheHalfTransformAppliedTwiceIsTheWholeOne(double rotationDeg, float tx, float ty, double scale)
        {
            var whole = Matrix3x2.CreateScale((float)scale) * Matrix3x2.CreateRotation((float)(rotationDeg * Math.PI / 180.0)) * Matrix3x2.CreateTranslation(tx, ty);
            var (half, s, rot) = DatasetCrossNightExporter.HalfTransform(whole);
            Math.Abs(s - scale).ShouldBeLessThan(1e-4);
            Math.Abs(rot - rotationDeg).ShouldBeLessThan(1e-3);
            var twice = half * half;
            foreach (var (got, want) in new[] { (twice.M11, whole.M11), (twice.M12, whole.M12), (twice.M21, whole.M21), (twice.M22, whole.M22), (twice.M31, whole.M31), (twice.M32, whole.M32) })
            {
                Math.Abs(got - want).ShouldBeLessThan(1e-3f);
            }
            // And the B side, m * half^-1, is the other half exactly: both nights resample by the same amount.
            Matrix3x2.Invert(half, out var halfInverse).ShouldBeTrue();
            var bSide = whole * halfInverse;
            Math.Abs(bSide.M11 - half.M11).ShouldBeLessThan(1e-3f);
            Math.Abs(bSide.M12 - half.M12).ShouldBeLessThan(1e-3f);
        }

        [Fact]
        public async Task ThePsfIsMatchedOnTheEXPORTEDSidesAndNotOnlyOnTheMasters()
        {
            var ct = TestContext.Current.CancellationToken;
            var sky = SharedSky();
            var a = RenderNight(sky, isNightB: false, gain: 1f, level: 900f, gradX: 140f, gradY: 60f, fwhmPx: 3f, seed: 61, noise: null, out _);
            var b = RenderNight(sky, isNightB: true, gain: 1f, level: 1000f, gradX: 0f, gradY: 0f, fwhmPx: 5f, seed: 62, noise: null, out _);
            var bake = BuildBake(a, b);
            var pairs = ImmutableArray.Create(new DatasetCrossNightExporter.PairSpec(NightA, NightB));
            // The master-side match is OFF here (the mismatch is merely tolerated), so what closes the gap
            // is the exported-side loop and nothing else. On real pairs both run: the master step takes
            // most of it and the loop takes the rest, which is where the 3 to 6 percent the first arm
            // shipped came from -- the warp widens the two sides by different amounts after the fact.
            var options = new DatasetCrossNightExporter.Options(
                bake, "", Pairs: pairs, CellsPerPair: 4, MaxFwhmMismatch: 0.5);
            var masterOnly = await DatasetCrossNightExporter.RunAsync(
                options with { OutDir = Path.Combine(_root, "master-only"), PsfIterations = 0 }, cancellationToken: ct);
            masterOnly.Pairs.Length.ShouldBe(1, string.Join("; ", masterOnly.Skipped));
            var before = masterOnly.Pairs[0];
            before.PsfPasses.ShouldBe(0);
            var mismatchBefore = Math.Abs(before.FwhmAfterA - before.FwhmAfterB) / Math.Max(before.FwhmAfterA, before.FwhmAfterB);

            var iterated = await DatasetCrossNightExporter.RunAsync(
                options with { OutDir = Path.Combine(_root, "iterated") }, cancellationToken: ct);
            iterated.Pairs.Length.ShouldBe(1, string.Join("; ", iterated.Skipped));
            var after = iterated.Pairs[0];
            output.WriteLine($"exported mismatch {mismatchBefore:P2} -> {Math.Abs(after.FwhmAfterA - after.FwhmAfterB) / Math.Max(after.FwhmAfterA, after.FwhmAfterB):P2} in {after.PsfPasses} pass(es)");
            after.PsfPasses.ShouldBeGreaterThan(0);
            var mismatchAfter = Math.Abs(after.FwhmAfterA - after.FwhmAfterB) / Math.Max(after.FwhmAfterA, after.FwhmAfterB);
            mismatchAfter.ShouldBeLessThan(mismatchBefore);
            mismatchAfter.ShouldBeLessThan(0.02);
            mismatchBefore.ShouldBeGreaterThan(0.2);
        }

        [Fact]
        public async Task InjectedDrawsAreNightAsOwnSceneWithMoreNoiseOnIt()
        {
            var ct = TestContext.Current.CancellationToken;
            var sky = SharedSky();
            var a = RenderNight(sky, isNightB: false, gain: 1f, level: 900f, gradX: 140f, gradY: 60f, fwhmPx: 3f, seed: 71, noise: null, out _);
            var b = RenderNight(sky, isNightB: true, gain: 1f, level: 980f, gradX: 20f, gradY: -30f, fwhmPx: 3f, seed: 72, noise: null, out _);
            var bake = BuildBake(a, b);
            var outDir = Path.Combine(_root, "injected");

            var result = await DatasetCrossNightExporter.RunAsync(
                new DatasetCrossNightExporter.Options(
                    bake, outDir, Pairs: [new DatasetCrossNightExporter.PairSpec(NightA, NightB)], CellsPerPair: 3,
                    InjectDraws: 2, MinInjectedNoise: 1.0, MaxInjectedNoise: 1.0),
                cancellationToken: ct);

            result.Pairs.Length.ShouldBe(1, string.Join("; ", result.Skipped));
            var pair = result.Pairs[0];
            pair.InjectedDraws.ShouldBe(pair.Cells * 2);

            var tiles = Directory.GetFiles(Path.Combine(outDir, "tiles"), "*.f16", SearchOption.AllDirectories);
            var draw = tiles.First(static t => Path.GetFileName(t).EndsWith("_deg000.f16", StringComparison.Ordinal));
            var nightA = draw.Replace("_deg000.f16", "_halfmaster_a.f16", StringComparison.Ordinal);
            File.Exists(nightA).ShouldBeTrue(nightA);
            var degraded = ReadTile(draw);
            var clean = ReadTile(nightA);

            // Same scene: the draw is that night with noise added, so it tracks it almost exactly.
            Pearson(degraded, clean).ShouldBeGreaterThan(0.95);
            // And noisier: at one times the night's own measured noise the input sits at sqrt(2) of it,
            // which a pixel-to-pixel difference sees and a correlation does not.
            var noisier = AdjacentSigma(degraded) / AdjacentSigma(clean);
            output.WriteLine($"adjacent-difference sigma ratio {noisier:F2}");
            noisier.ShouldBeGreaterThan(1.1);
        }

        [Fact]
        public async Task AThirdNightRidesOnThePairsOwnGridAsAMeasurementFrame()
        {
            var ct = TestContext.Current.CancellationToken;
            var sky = SharedSky();
            var a = RenderNight(sky, isNightB: false, gain: 1f, level: 900f, gradX: 140f, gradY: 60f, fwhmPx: 3f, seed: 81, noise: null, out _);
            var b = RenderNight(sky, isNightB: true, gain: 1f, level: 980f, gradX: 20f, gradY: -30f, fwhmPx: 3f, seed: 82, noise: null, out _);
            var c = RenderNight(sky, isNightB: true, gain: 0.9f, level: 1050f, gradX: -40f, gradY: 15f, fwhmPx: 3f, seed: 83, noise: null, out _);
            const string NightC = "TestCam/None/Target/2026-01-07|TestCam|Target|None";
            var bake = BuildBake(a, b, (NightC, c));
            var outDir = Path.Combine(_root, "triple");

            var result = await DatasetCrossNightExporter.RunAsync(
                new DatasetCrossNightExporter.Options(
                    bake, outDir, Pairs: [new DatasetCrossNightExporter.PairSpec(NightA, NightB, NightC)], CellsPerPair: 3),
                cancellationToken: ct);

            result.Pairs.Length.ShouldBe(1, string.Join("; ", result.Skipped));
            var pair = result.Pairs[0];
            pair.SessionC.ShouldBe(NightC);
            // The third night is on the same grid, so it reads the same width as the other two.
            pair.FwhmAfterC.ShouldBeInRange(pair.FwhmAfterA * 0.85, pair.FwhmAfterA * 1.15);

            // One measurement tile per cell, beside the three the trainer knows about.
            var tiles = Directory.GetFiles(Path.Combine(outDir, "tiles"), "*.f16", SearchOption.AllDirectories);
            tiles.Count(static t => Path.GetFileName(t).EndsWith("_nightc.f16", StringComparison.Ordinal)).ShouldBe(pair.Cells);
            var third = tiles.First(static t => Path.GetFileName(t).EndsWith("_nightc.f16", StringComparison.Ordinal));
            var nightA = third.Replace("_nightc.f16", "_halfmaster_a.f16", StringComparison.Ordinal);
            // Registered and level matched onto A like B is: the same sky, so it correlates with it.
            Pearson(ReadTile(third), ReadTile(nightA)).ShouldBeGreaterThan(0.8);
        }

        [Theory]
        [InlineData(0, 500, 500, new[] { 100, 200, 400 })]
        [InlineData(0, 120, 500, new[] { 100, 120 })]
        [InlineData(0, 60, 500, new[] { 60 })]
        [InlineData(250, 500, 500, new[] { 250 })]
        public void TheQuadBudgetEscalatesUntilTheStarListsRunOut(int requested, int starsA, int starsB, int[] expected)
        {
            // A partially overlapping pair is refused for want of stars nobody looked at, so the budget
            // climbs; it never asks for more than the shorter list holds, and never repeats a budget.
            DatasetCrossNightExporter.QuadBudgets(
                new DatasetCrossNightExporter.Options("bake", "out", QuadStars: requested), starsA, starsB)
                .ShouldBe(expected);
        }

        [Fact]
        public void ANanAwareBlurWidensAStarWithoutSpreadingTheUncoveredRing()
        {
            var plane = new float[32, 32];
            plane[16, 16] = 100f;
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    plane[y, x] = float.NaN;
                }
            }

            DatasetCrossNightExporter.BlurPlaneInPlace(plane, 1.5);

            // The point spread, and the ring did not: a plain convolution would have eaten four more
            // columns per pass, and the exported-side match runs several.
            plane[16, 16].ShouldBeLessThan(100f);
            plane[16, 18].ShouldBeGreaterThan(0f);
            for (var y = 0; y < 32; y++)
            {
                float.IsNaN(plane[y, 3]).ShouldBeTrue();
                float.IsNaN(plane[y, 4]).ShouldBeFalse();
            }
            // Flux is conserved by the renormalisation rather than leaked into the ring.
            var sum = 0.0;
            for (var y = 0; y < 32; y++)
            {
                for (var x = 4; x < 32; x++)
                {
                    sum += plane[y, x];
                }
            }
            sum.ShouldBeInRange(95.0, 105.0);
        }

        private static double AdjacentSigma(float[] tile)
        {
            var diffs = new double[tile.Length - 1];
            for (var i = 0; i < diffs.Length; i++)
            {
                diffs[i] = Math.Abs(tile[i + 1] - (double)tile[i]);
            }
            Array.Sort(diffs);
            return diffs[diffs.Length / 2] * 1.4826 / Math.Sqrt(2);
        }

        private static double Pearson(float[] a, float[] b)
        {
            double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
            for (var i = 0; i < a.Length; i++)
            {
                sa += a[i];
                sb += b[i];
                saa += a[i] * (double)a[i];
                sbb += b[i] * (double)b[i];
                sab += a[i] * (double)b[i];
            }
            var n = a.Length;
            var cov = (sab / n) - ((sa / n) * (sb / n));
            var va = (saa / n) - ((sa / n) * (sa / n));
            var vb = (sbb / n) - ((sb / n) * (sb / n));
            return cov / Math.Sqrt(va * vb);
        }
    }
}
