using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Coverage for <see cref="DatasetTileExporter"/> (dataset builder P0/#40): drives a real
    /// <see cref="SessionRegistrar"/> pass over the synthetic RGGB fixture, then exports tiles and
    /// asserts the output contract: fp16 CHW blobs in [0, 1], a JSONL manifest with one row per
    /// tile, the master + N2N-sub structure per cell, and byte-for-byte determinism across runs.
    /// </summary>
    [Collection("Imaging")]
    public class DatasetTileExporterTests(ITestOutputHelper output) : IDisposable
    {
        private const int TileSize = 64;   // small so many cells fit the synthetic 384px canvas
        private const int SubsPerCell = 3;

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "tileexport-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static List<FrameInfo> ReadFrames(string dir, string pattern)
        {
            var frames = new List<FrameInfo>();
            foreach (var path in Directory.GetFiles(dir, pattern).OrderBy(p => p, StringComparer.Ordinal))
            {
                Image.TryReadFitsFile(path, out var img).ShouldBeTrue();
                frames.Add(new FrameInfo(path, img!.Width, img.Height, img.ChannelCount, img.BitDepth, img.ImageMeta));
                img.Release();
            }
            return frames;
        }

        private async Task<SessionRegistrar.RegisteredSession> RegisterFixtureAsync(
            CancellationToken ct, int minSubsForHalfMasters = int.MaxValue)
        {
            var lightsDir = Path.Combine(_dir, "LIGHT");
            var darksDir = Path.Combine(_dir, "DARK");
            Directory.CreateDirectory(lightsDir);
            Directory.CreateDirectory(darksDir);
            RgbBayerSyntheticFixture.WriteSyntheticLights(lightsDir);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);

            var calibrator = new Calibrator(Dark: await MasterFrameBuilder.BuildDarkMasterAsync(ReadFrames(darksDir, "dark_*.fits"), ct));
            var session = new ImagingSession(lightsDir, "synth/rggb", "SynthBayer", "SynthRgb", "", [.. ReadFrames(lightsDir, "light_*.fits")]);
            // int.MaxValue by default so the existing cases keep exporting master + subs only; the
            // half-master pair is opted into per test rather than added to every expected tile count.
            var registered = await SessionRegistrar.RegisterAsync(
                session, calibrator, Path.Combine(_dir, "scratch"), minSubs: 4,
                minSubsForHalfMasters: minSubsForHalfMasters, cancellationToken: ct);
            registered.ShouldNotBeNull();
            return registered;
        }

        [Fact]
        public async Task Export_ProducesFp16TilesAndManifest()
        {
            var ct = TestContext.Current.CancellationToken;
            var registered = await RegisterFixtureAsync(ct);
            var outDir = Path.Combine(_dir, "out");

            var result = await DatasetTileExporter.ExportAsync(
                registered, outDir, tileSize: TileSize, cellsPerSession: 20, subsPerCell: SubsPerCell,
                logger: new XunitLogger(output), cancellationToken: ct);

            result.Cells.ShouldBeGreaterThan(0);
            result.Cells.ShouldBeLessThanOrEqualTo(20);
            result.MasterTiles.ShouldBe(result.Cells);
            // Every cell exports min(subsPerCell, registered subs) sub tiles; the fixture registers
            // all 8, so that's exactly SubsPerCell per cell.
            var expectedSubsPerCell = Math.Min(SubsPerCell, registered.Subs.Length);
            result.SubTiles.ShouldBe(result.Cells * expectedSubsPerCell);
            result.Rows.Length.ShouldBe(result.MasterTiles + result.SubTiles);

            // Manifest: one JSONL line per row.
            File.Exists(result.ManifestPath).ShouldBeTrue();
            var lines = File.ReadAllLines(result.ManifestPath).Count(l => l.Trim().Length > 0);
            lines.ShouldBe(result.Rows.Length);

            var channels = registered.Master.ChannelCount;
            var expectedBytes = channels * TileSize * TileSize * 2; // fp16 CHW

            // Each cell has exactly one master tile + expectedSubsPerCell sub tiles sharing coords.
            foreach (var cell in result.Rows.GroupBy(r => (r.CellX, r.CellY)))
            {
                cell.Count(r => r.Frame == "master").ShouldBe(1);
                cell.Count(r => r.Frame == "sub").ShouldBe(expectedSubsPerCell);
            }

            // Every blob exists, is the right fp16 size, and decodes to finite [0,1] values (the
            // MTF pre-stretch output range: this is what the model trains and infers on).
            foreach (var row in result.Rows)
            {
                row.TileSize.ShouldBe(TileSize);
                row.Channels.ShouldBe(channels);
                var blob = Path.Combine(outDir, row.Tile.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(blob).ShouldBeTrue($"tile blob missing: {row.Tile}");
                var bytes = File.ReadAllBytes(blob);
                bytes.Length.ShouldBe(expectedBytes);
                var halfs = MemoryMarshal.Cast<byte, Half>(bytes);
                var anyNonZero = false;
                foreach (var h in halfs)
                {
                    var f = (float)h;
                    float.IsNaN(f).ShouldBeFalse();
                    f.ShouldBeInRange(-0.001f, 1.001f, $"{row.Frame} {row.Tile}: f={f}");
                    if (f > 0f) anyNonZero = true;
                }
                anyNonZero.ShouldBeTrue($"tile {row.Tile} is all-zero");
            }

            output.WriteLine($"cells={result.Cells} master={result.MasterTiles} sub={result.SubTiles}");
        }

        /// <summary>
        /// A session that carries a half-master pair exports two more tiles per cell, on the SAME
        /// cells as the master and the subs, and the manifest has to name which half each one is.
        /// Both halves carry an empty <c>SourceFile</c> (they are integrations, not one file), so the
        /// frame kind is the only thing distinguishing them: a consumer pairing halfA with halfB, and
        /// the parity check resolving a tile back to its source, both key on it.
        /// </summary>
        [Fact]
        public async Task Export_EmitsAHalfMasterTilePairPerCell_NamedByFrameKind()
        {
            var ct = TestContext.Current.CancellationToken;
            var registered = await RegisterFixtureAsync(ct, minSubsForHalfMasters: 4);
            registered.HalfMasterA.ShouldNotBeNull();
            var outDir = Path.Combine(_dir, "out");

            var result = await DatasetTileExporter.ExportAsync(
                registered, outDir, tileSize: TileSize, cellsPerSession: 20, subsPerCell: SubsPerCell,
                logger: new XunitLogger(output), cancellationToken: ct);

            result.HalfMasterTiles.ShouldBe(result.Cells * 2);
            result.Rows.Length.ShouldBe(result.MasterTiles + result.HalfMasterTiles + result.SubTiles);
            var expectedSubsPerCell = Math.Min(SubsPerCell, registered.Subs.Length);
            foreach (var cell in result.Rows.GroupBy(r => (r.CellX, r.CellY)))
            {
                cell.Count(r => r.Frame == DatasetTileExporter.FrameMaster).ShouldBe(1);
                cell.Count(r => r.Frame == DatasetTileExporter.FrameHalfMasterA).ShouldBe(1);
                cell.Count(r => r.Frame == DatasetTileExporter.FrameHalfMasterB).ShouldBe(1);
                cell.Count(r => r.Frame == DatasetTileExporter.FrameSub).ShouldBe(expectedSubsPerCell);
            }

            // Distinct blobs, all present, and the two halves genuinely differ from each other and
            // from the master: identical bytes would mean the same frame was tiled three times, which
            // is the one failure this whole pair exists to avoid and which every count above would
            // still pass.
            var byFrame = new Dictionary<string, byte[]>();
            foreach (var frame in new[]
                {
                    DatasetTileExporter.FrameMaster, DatasetTileExporter.FrameHalfMasterA,
                    DatasetTileExporter.FrameHalfMasterB,
                })
            {
                var row = result.Rows.First(r => r.Frame == frame);
                row.SourceFile.ShouldBe("");
                var blob = Path.Combine(outDir, row.Tile.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(blob).ShouldBeTrue($"tile blob missing: {row.Tile}");
                byFrame[frame] = File.ReadAllBytes(blob);
            }
            // Same cell for all three (Rows is canonically sorted, so First() lands on the first cell
            // for every kind), so a byte difference is a pixel difference and not a different tile.
            var cellOfMaster = result.Rows.First(r => r.Frame == DatasetTileExporter.FrameMaster);
            foreach (var frame in byFrame.Keys)
            {
                var row = result.Rows.First(r => r.Frame == frame);
                (row.CellX, row.CellY).ShouldBe((cellOfMaster.CellX, cellOfMaster.CellY));
            }
            byFrame[DatasetTileExporter.FrameHalfMasterA]
                .ShouldNotBe(byFrame[DatasetTileExporter.FrameHalfMasterB]);
            byFrame[DatasetTileExporter.FrameHalfMasterA]
                .ShouldNotBe(byFrame[DatasetTileExporter.FrameMaster]);

            // The parity check must handle the new kinds; before this it keyed its source cache off
            // SourceFile, which is "" for all three, so it compared a half-master's stored tile
            // against the master's pixels.
            var parity = await DatasetTileExporter.VerifyParityAsync(
                registered, outDir, result.Rows, sampleCount: 12, cancellationToken: ct);
            parity.Checked.ShouldBeGreaterThan(0);
            parity.MaxAbsDiff.ShouldBe(0.0);
        }

        /// <summary>
        /// Every tile carries its OWN frame's stretch and noise (E16), never the master's: the manifest's stretch is
        /// what the runner's input stretch measures on that frame, each tile's plane lies beside it, and the planes
        /// rank the frames by depth. And against a truth the estimate never saw: over the fixture's sky, a half's
        /// plane reads the noise the two halves' own difference measures.
        /// </summary>
        /// <remarks>
        /// A half's plane is its MEASURED standard error since E16c step 2, which is why the halves are compared robust
        /// against robust, the plane's median with the pair's MAD spread: the measurement also reports where the halves
        /// really disagree, around the stars, where four subs and no clip leave each half's demosaic its own errors, and
        /// a MAD spread is built not to see those. Measured 2026-10-05: the median reads 1.14 (1.11 to 1.25), the mean
        /// 1.78. <see cref="TheRegistrarHandsEachHalfItsOwnMeasuredError"/> pins that the tail is that disagreement.
        /// A sub has no measurement and keeps the model's plane.
        /// </remarks>
        [Fact]
        public async Task EveryTileCarriesItsOwnFramesStretchAndNoisePlane()
        {
            var ct = TestContext.Current.CancellationToken;
            var registered = await RegisterFixtureAsync(ct, minSubsForHalfMasters: 4);
            var halfA = registered.HalfMasterA.ShouldNotBeNull();
            var halfB = registered.HalfMasterB.ShouldNotBeNull();
            var outDir = Path.Combine(_dir, "out");

            var result = await DatasetTileExporter.ExportAsync(
                registered, outDir, tileSize: TileSize, cellsPerSession: 20, subsPerCell: SubsPerCell,
                logger: new XunitLogger(output), cancellationToken: ct);

            var balanceOf = new Dictionary<string, double[]>();
            foreach (var (frame, image) in new[]
                {
                    (DatasetTileExporter.FrameMaster, registered.Master),
                    (DatasetTileExporter.FrameHalfMasterA, halfA),
                    (DatasetTileExporter.FrameHalfMasterB, halfB),
                })
            {
                var (_, applied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(DatasetTileExporter.ToUnitRange(image));
                applied.ShouldBeTrue();
                var expectedMin = origMin.ShouldNotBeNull().Select(v => (double)v).ToArray();
                var expectedBalance = balances.ShouldNotBeNull();
                foreach (var row in result.Rows.Where(r => r.Frame == frame))
                {
                    row.StretchOrigMin.ShouldBe(expectedMin);
                    row.StretchBalance.ShouldBe(expectedBalance);
                    // One calibration per channel, each the channel's own.
                    row.NoiseSigma.ShouldNotBeNull().Length.ShouldBe(row.Channels);
                    row.NoiseSigma.ShouldAllBe(s => s > 0.0);
                    row.NoiseBackground.ShouldNotBeNull().Length.ShouldBe(row.Channels);
                }
                balanceOf[frame] = expectedBalance;
            }
            // Each half's stretch is its own: the column exists because the master's does not stand in for it.
            balanceOf[DatasetTileExporter.FrameHalfMasterA].ShouldNotBe(balanceOf[DatasetTileExporter.FrameMaster]);

            var planeBytes = TileSize * TileSize * 2;
            var meanPlane = new Dictionary<string, List<double>>();
            foreach (var row in result.Rows)
            {
                var sigma = row.SigmaTile.ShouldNotBeNull($"{row.Tile} has no plane");
                sigma.ShouldBe(DatasetDegradationExporter.SigmaPathFor(row.Tile));
                var bytes = File.ReadAllBytes(Path.Combine(outDir, sigma.Replace('/', Path.DirectorySeparatorChar)));
                bytes.Length.ShouldBe(planeBytes);
                var mean = 0.0;
                foreach (var h in MemoryMarshal.Cast<byte, Half>(bytes))
                {
                    var v = (double)(float)h;
                    double.IsFinite(v).ShouldBeTrue();
                    mean += v;
                }
                var key = row.Frame == DatasetTileExporter.FrameSub ? DatasetTileExporter.FrameSub
                    : row.Frame == DatasetTileExporter.FrameMaster ? DatasetTileExporter.FrameMaster : "half";
                (meanPlane.TryGetValue(key, out var list) ? list : meanPlane[key] = []).Add(mean / (TileSize * TileSize));
            }
            var master = meanPlane[DatasetTileExporter.FrameMaster].Average();
            var half = meanPlane["half"].Average();
            var sub = meanPlane[DatasetTileExporter.FrameSub].Average();
            output.WriteLine($"mean plane: master {master:F4}, half {half:F4} ({half / master:F2}x), sub {sub:F4} ({sub / master:F2}x), " +
                             $"{registered.Subs.Length} subs; balance master {balanceOf[DatasetTileExporter.FrameMaster][0]:F4}, " +
                             $"half A {balanceOf[DatasetTileExporter.FrameHalfMasterA][0]:F4}");

            // The external truth, which the estimate never saw: per cell, two frames of the same depth differ by
            // their noise alone, so their difference over sqrt 2 is one frame's noise, in the frames' own stretched
            // units. The two halves for a half, two subs for a sub. Luminance (the channel mean), as the plane is.
            var halfRatios = new List<double>();
            var halfMeanRatios = new List<double>();
            var subRatios = new List<double>();
            foreach (var cell in result.Rows.GroupBy(r => (r.CellX, r.CellY)))
            {
                var halfRow = cell.Single(r => r.Frame == DatasetTileExporter.FrameHalfMasterA);
                halfRow.PlaneMeasured.ShouldBe(true);
                var pair = PairNoise(outDir, halfRow, cell.Single(r => r.Frame == DatasetTileExporter.FrameHalfMasterB));
                halfRatios.Add(MedianPlane(outDir, halfRow) / pair);
                halfMeanRatios.Add(MeanPlane(outDir, halfRow) / pair);
                var subs = cell.Where(r => r.Frame == DatasetTileExporter.FrameSub).ToArray();
                subs[0].PlaneMeasured.ShouldNotBe(true);
                subRatios.Add(MeanPlane(outDir, subs[0]) / PairNoise(outDir, subs[0], subs[1]));
            }
            var halfMedian = Median(halfRatios);
            var subMedian = Median(subRatios);
            output.WriteLine($"plane over the pair's own noise: half {halfMedian:F3} ({halfRatios.Min():F3} to {halfRatios.Max():F3}; its mean " +
                             $"{Median(halfMeanRatios):F3}), sub {subMedian:F3} ({subRatios.Min():F3} to {subRatios.Max():F3}) over {halfRatios.Count} cells");
            halfMedian.ShouldBeInRange(0.8, 1.25);
            subMedian.ShouldBeInRange(0.8, 1.25);
        }

        /// <summary>
        /// E16c step 2's wiring: the registrar hands each half the standard error ITS integration measured, in its own
        /// units, and at the sky it reads the noise the two halves' difference measures, per channel (the master's too,
        /// at the root-2 smaller error of twice the subs). Where it reads ten times its sky, the halves really differ
        /// there: the tail is a measurement, not noise in the estimate. Four subs a half and no clip, so the demosaic's
        /// errors around stars stay in each half (measured 2026-10-05: up to hundreds of ADU over a sky noise of 0.7).
        /// </summary>
        [Fact]
        public async Task TheRegistrarHandsEachHalfItsOwnMeasuredError()
        {
            var ct = TestContext.Current.CancellationToken;
            var registered = await RegisterFixtureAsync(ct, minSubsForHalfMasters: 4);
            var halfA = registered.HalfMasterA.ShouldNotBeNull();
            var halfB = registered.HalfMasterB.ShouldNotBeNull();
            var errorA = registered.HalfMasterAStandardError.ShouldNotBeNull();
            var errorB = registered.HalfMasterBStandardError.ShouldNotBeNull();
            var errorMaster = registered.StandardError.ShouldNotBeNull();
            foreach (var plane in new[] { errorA, errorB, errorMaster })
            {
                (plane.Width, plane.Height, plane.ChannelCount).ShouldBe((halfA.Width, halfA.Height, halfA.ChannelCount));
            }

            for (var c = 0; c < halfA.ChannelCount; c++)
            {
                var master = registered.Master.GetChannelArray(c);
                var a = halfA.GetChannelArray(c);
                var b = halfB.GetChannelArray(c);
                var seA = errorA.GetChannelArray(c);
                var seMaster = errorMaster.GetChannelArray(c);
                // The sky: the master's darker 40 percent, every reading finite.
                var levels = master.Cast<float>().Where(float.IsFinite).OrderBy(v => v).ToArray();
                var skyCut = levels[(int)(levels.Length * 0.4)];
                var differences = new List<float>();
                var sky = new List<double>();
                var skyMaster = new List<double>();
                var all = new List<(float Error, float Difference)>();
                for (var y = 0; y < master.GetLength(0); y++)
                {
                    for (var x = 0; x < master.GetLength(1); x++)
                    {
                        if (!(master[y, x] <= skyCut) || !float.IsFinite(a[y, x]) || !float.IsFinite(b[y, x])
                            || !float.IsFinite(seA[y, x]) || !float.IsFinite(seMaster[y, x]))
                        {
                            continue;
                        }
                        var d = (float)((a[y, x] - b[y, x]) / Math.Sqrt(2.0));
                        differences.Add(d);
                        sky.Add(seA[y, x]);
                        skyMaster.Add(seMaster[y, x]);
                        all.Add((seA[y, x], d));
                    }
                }
                var (_, mad) = TianWen.Lib.Stat.StatisticsHelper.MedianAndMad(differences.ToArray().AsSpan());
                var pair = 1.4826 * mad;
                var halfError = Median(sky);
                var masterError = Median(skyMaster);
                var flagged = all.Where(p => p.Error > 10 * halfError).ToArray();
                var flaggedRms = flagged.Length > 0 ? Math.Sqrt(flagged.Average(p => (double)p.Difference * p.Difference)) : 0.0;
                output.WriteLine($"channel {c}: pair {pair:F4}, half error {halfError:F4} ({halfError / pair:F3}), master error {masterError:F4} " +
                                 $"({masterError / (pair / Math.Sqrt(2.0)):F3}), {flagged.Length} of {all.Count} sky pixels past ten times, " +
                                 $"their difference {flaggedRms / pair:F1} times the pair's spread");
                (halfError / pair).ShouldBeInRange(0.85, 1.15);
                (masterError / (pair / Math.Sqrt(2.0))).ShouldBeInRange(0.85, 1.15);
                flagged.Length.ShouldBeGreaterThan(0);
                (flaggedRms / pair).ShouldBeGreaterThan(10.0);
            }
        }

        /// <summary>The median of a tile's plane, in stretched sigma (the plane's units divided out).</summary>
        private static double MedianPlane(string outDir, DatasetTileExporter.TileManifestRow row)
        {
            var plane = MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(Path.Combine(outDir,
                row.SigmaTile.ShouldNotBeNull().Replace('/', Path.DirectorySeparatorChar))));
            var values = new float[plane.Length];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (float)plane[i];
            }
            Array.Sort(values);
            return values[values.Length / 2] / TianWen.Lib.Imaging.Degradation.StretchedNoise.PlaneScale;
        }

        /// <summary>The mean of a tile's plane, in stretched sigma (the plane's units divided out).</summary>
        private static double MeanPlane(string outDir, DatasetTileExporter.TileManifestRow row)
        {
            var plane = MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(Path.Combine(outDir,
                row.SigmaTile.ShouldNotBeNull().Replace('/', Path.DirectorySeparatorChar))));
            var sum = 0.0;
            foreach (var h in plane)
            {
                sum += (float)h;
            }
            return sum / (plane.Length * TianWen.Lib.Imaging.Degradation.StretchedNoise.PlaneScale);
        }

        /// <summary>One frame's noise from two frames of the same depth: the MAD sigma of their difference over sqrt 2.</summary>
        private static double PairNoise(string outDir, DatasetTileExporter.TileManifestRow a, DatasetTileExporter.TileManifestRow b)
        {
            var la = ReadLuminance(outDir, a);
            var lb = ReadLuminance(outDir, b);
            var d = new float[la.Length];
            for (var i = 0; i < d.Length; i++)
            {
                d[i] = (float)((la[i] - lb[i]) / Math.Sqrt(2.0));
            }
            var (_, mad) = TianWen.Lib.Stat.StatisticsHelper.MedianAndMad(d.AsSpan());
            return 1.4826 * mad;
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted[sorted.Length / 2];
        }

        private static float[] ReadLuminance(string outDir, DatasetTileExporter.TileManifestRow row)
        {
            var halfs = MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(Path.Combine(outDir, row.Tile.Replace('/', Path.DirectorySeparatorChar))));
            var n = row.TileSize * row.TileSize;
            var lum = new float[n];
            for (var c = 0; c < row.Channels; c++)
            {
                for (var i = 0; i < n; i++)
                {
                    lum[i] += (float)halfs[(c * n) + i] / row.Channels;
                }
            }
            return lum;
        }

        [Fact]
        public async Task Export_IsDeterministic()
        {
            var ct = TestContext.Current.CancellationToken;
            var registered = await RegisterFixtureAsync(ct);

            var r1 = await DatasetTileExporter.ExportAsync(
                registered, Path.Combine(_dir, "out1"), tileSize: TileSize, cellsPerSession: 20, subsPerCell: SubsPerCell, cancellationToken: ct);
            var r2 = await DatasetTileExporter.ExportAsync(
                registered, Path.Combine(_dir, "out2"), tileSize: TileSize, cellsPerSession: 20, subsPerCell: SubsPerCell, cancellationToken: ct);

            // Seeded from the (stable) session id + canonical sort => identical tile set and row
            // order, which the pinned train/test split depends on.
            r1.Rows.Length.ShouldBe(r2.Rows.Length);
            for (var i = 0; i < r1.Rows.Length; i++)
            {
                r2.Rows[i].Tile.ShouldBe(r1.Rows[i].Tile);
                r2.Rows[i].CellX.ShouldBe(r1.Rows[i].CellX);
                r2.Rows[i].CellY.ShouldBe(r1.Rows[i].CellY);
                r2.Rows[i].Frame.ShouldBe(r1.Rows[i].Frame);
                r2.Rows[i].NoiseMad.ShouldBe(r1.Rows[i].NoiseMad);
            }
        }

        [Fact]
        public async Task ReadManifestCheckpoints_ToleratesRowsFromBeforeTheFwhmColumnWasDropped()
        {
            // Resume must survive the SessionMedianFwhm removal. A manifest written by an older
            // build carries that property on every row; if the reader rejected unknown members, a
            // resume against it would see zero checkpoints and silently re-export all 50 sessions
            // (~7 hours, and the tiles are already on disk). Deserialization ignores it, so the
            // checkpoint still resolves; this pins that rather than leaving it to a default.
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "legacy-manifest.jsonl");
            var legacy = /*lang=json*/ """
                {"Tile":"tiles/a/x0_y0_master.f16","SessionId":"a|Cam","Camera":"Cam","Frame":"master","SourceFile":"","CellX":0,"CellY":0,"TileSize":64,"Channels":3,"Gain":100,"ExposureSeconds":1,"NoiseMad":0.1,"SessionMedianFwhm":3.5682}
                """;
            await File.WriteAllTextAsync(path, legacy + "\n", ct);

            var checkpoints = await DatasetTileExporter.ReadManifestCheckpointsAsync(path, ct);

            var checkpoint = checkpoints.ShouldHaveSingleItem().Value;
            checkpoint.SessionId.ShouldBe("a|Cam");
            checkpoint.TileCount.ShouldBe(1);
            checkpoint.TileDirRelative.ShouldBe("tiles/a");
        }

        private static DatasetTileExporter.TileManifestRow Row(string tile) => new(
            Tile: tile, SessionId: "b|Cam", Camera: "Cam", Frame: "master",
            SourceFile: "", CellX: 0, CellY: 0, TileSize: 64, Channels: 3, Gain: 100,
            ExposureSeconds: 1, NoiseMad: 0.1);

        [Fact]
        public async Task AppendManifest_HealsTornTailBeforeAppending()
        {
            // A crash mid-append leaves a torn (newline-less) last line; because the build runner
            // fault-isolates per session and keeps going, the NEXT session's append would bury it
            // mid-file where every JSONL consumer chokes. The append must truncate it back to the
            // last complete row first.
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "tiles-manifest.jsonl");
            var complete = /*lang=json*/ """{"Tile":"tiles/a/x0_y0_master.f16"}""";
            var torn = """{"Tile":"tiles/a/x0_y64_s00""";
            await File.WriteAllTextAsync(path, complete + "\n" + torn, ct);

            await DatasetTileExporter.AppendManifestAsync(path, [Row("tiles/b/x0_y0_master.f16")], ct);

            var lines = (await File.ReadAllLinesAsync(path, ct)).Where(l => l.Length > 0).ToArray();
            lines.Length.ShouldBe(2);
            lines[0].ShouldBe(complete);                    // intact rows preserved verbatim
            lines[1].ShouldContain("tiles/b/");             // the new row follows them
            foreach (var line in lines)
            {
                Should.NotThrow(() => System.Text.Json.JsonDocument.Parse(line).Dispose(),
                    $"line is not valid JSON: {line}");
            }
        }

        [Fact]
        public async Task AppendManifest_WholeFileTorn_TruncatesToJustTheNewRows()
        {
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "tiles-manifest.jsonl");
            await File.WriteAllTextAsync(path, """{"Tile":"tiles/a/never-finis""", ct); // no newline at all

            await DatasetTileExporter.AppendManifestAsync(path, [Row("tiles/b/x0_y0_master.f16")], ct);

            var lines = (await File.ReadAllLinesAsync(path, ct)).Where(l => l.Length > 0).ToArray();
            lines.ShouldHaveSingleItem().ShouldContain("tiles/b/");
        }

        /// <summary>Rebuilds a registered session's master with a caller-chosen declared range and
        /// pixel fill, so a poisoned master can be handed to the exporter without needing a poisoned
        /// integration to produce one.</summary>
        private static SessionRegistrar.RegisteredSession WithMaster(
            SessionRegistrar.RegisteredSession registered, float maxValue, float fill)
        {
            var source = registered.Master;
            var data = Image.CreateChannelData(source.ChannelCount, source.Height, source.Width);
            if (fill != 0f)
            {
                for (var c = 0; c < source.ChannelCount; c++)
                {
                    for (var y = 0; y < source.Height; y++)
                    {
                        for (var x = 0; x < source.Width; x++)
                        {
                            data[c][y, x] = fill;
                        }
                    }
                }
            }
            var master = new Image(data, BitDepth.Float32, maxValue: maxValue, minValue: 0f, pedestal: 0f,
                imageMeta: source.ImageMeta);
            return registered with { Master = master };
        }

        [Fact]
        public async Task Export_RefusesAMasterWhosePixelRangeIsNotFinite()
        {
            // The exact shape the WriteHalf overflow produced: a sub reached the 16-bit ceiling, staged
            // as +Inf, the integrator averaged it in, and the master's MaxValue went infinite. Dividing
            // by it zeroed every sample, so five sessions wrote 1,500 tiles of pure zeroes that looked
            // like ordinary files. Parity could never catch it, because zeroes equal zeroes.
            var ct = TestContext.Current.CancellationToken;
            var registered = await RegisterFixtureAsync(ct);
            var poisoned = WithMaster(registered, float.PositiveInfinity, fill: 0f);

            var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
                await DatasetTileExporter.ExportAsync(
                    poisoned, Path.Combine(_dir, "out-inf"), tileSize: TileSize, cellsPerSession: 4,
                    subsPerCell: SubsPerCell, logger: new XunitLogger(output), cancellationToken: ct));

            ex.Message.ShouldContain("not finite");
            // Refused before writing: no half-populated tile directory left behind.
            Directory.Exists(Path.Combine(_dir, "out-inf", "tiles")).ShouldBeFalse();
        }

        [Fact]
        public async Task Export_RefusesToWriteATileThatIsEntirelyZero()
        {
            // The backstop for whatever the master-range check does not anticipate. A finite declared
            // range over all-zero pixels survives that check, stretches to zeroes, and would otherwise
            // write a full set of empty tiles.
            var ct = TestContext.Current.CancellationToken;
            var registered = await RegisterFixtureAsync(ct);
            var blank = WithMaster(registered, maxValue: 1f, fill: 0f);

            var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
                await DatasetTileExporter.ExportAsync(
                    blank, Path.Combine(_dir, "out-zero"), tileSize: TileSize, cellsPerSession: 4,
                    subsPerCell: SubsPerCell, logger: new XunitLogger(output), cancellationToken: ct));

            ex.Message.ShouldContain("entirely zero");
        }

        /// <summary>
        /// Tiling a DRIZZLED session, which is the kind that has no warped scratch to read: its subs
        /// are warped again here, one per sub because the export loop is sub-major. The parity check
        /// re-warps them a second time and finds the stored tiles bit-identical, which is the
        /// end-to-end statement that a sub is the same frame however it was obtained -- the
        /// registrar test asserts the two routes agree, and this asserts the exporter is on the
        /// route it thinks it is.
        /// </summary>
        [Fact]
        public async Task ADrizzledSessionsSubsAreTiledByRewarpingThem()
        {
            var ct = TestContext.Current.CancellationToken;
            var lightsDir = Path.Combine(_dir, "LIGHT");
            var darksDir = Path.Combine(_dir, "DARK");
            Directory.CreateDirectory(lightsDir);
            Directory.CreateDirectory(darksDir);
            RgbBayerSyntheticFixture.WriteSyntheticLights(lightsDir, DrizzleStrategy.AutoSelectMinFrameCount);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);
            var calibrator = new Calibrator(Dark: await MasterFrameBuilder.BuildDarkMasterAsync(ReadFrames(darksDir, "dark_*.fits"), ct));
            var session = new ImagingSession(
                lightsDir, "synth/rggb-deep", "SynthBayer", "SynthRgb", "", [.. ReadFrames(lightsDir, "light_*.fits")]);
            var scratch = Path.Combine(_dir, "scratch");
            var registered = await SessionRegistrar.RegisterAsync(
                session, calibrator, scratch, minSubs: 4, minSubsForHalfMasters: int.MaxValue,
                logger: new XunitLogger(output), cancellationToken: ct);
            registered.ShouldNotBeNull();
            registered.MasterStrategy.ShouldBe(IntegrationStrategyKind.BayerDrizzle);
            Directory.GetFiles(scratch, "warped_*.fits", SearchOption.AllDirectories).ShouldBeEmpty();

            var outDir = Path.Combine(_dir, "out");
            var result = await DatasetTileExporter.ExportAsync(
                registered, outDir, tileSize: TileSize, cellsPerSession: 10, subsPerCell: SubsPerCell,
                logger: new XunitLogger(output), cancellationToken: ct);

            result.Cells.ShouldBeGreaterThan(0);
            result.Rows.Count(r => r.Frame == DatasetTileExporter.FrameSub).ShouldBe(result.Cells * SubsPerCell);
            var parity = await DatasetTileExporter.VerifyParityAsync(
                registered, outDir, result.Rows, sampleCount: 12, cancellationToken: ct);
            parity.Checked.ShouldBeGreaterThan(0);
            parity.MaxAbsDiff.ShouldBe(0.0);
        }
    }
}
