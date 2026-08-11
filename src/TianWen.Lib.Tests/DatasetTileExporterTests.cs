using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
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

        private async Task<SessionRegistrar.RegisteredSession> RegisterFixtureAsync(CancellationToken ct)
        {
            var lightsDir = Path.Combine(_dir, "LIGHT");
            var darksDir = Path.Combine(_dir, "DARK");
            Directory.CreateDirectory(lightsDir);
            Directory.CreateDirectory(darksDir);
            RgbBayerSyntheticFixture.WriteSyntheticLights(lightsDir);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);

            var calibrator = new Calibrator(Dark: await MasterFrameBuilder.BuildDarkMasterAsync(ReadFrames(darksDir, "dark_*.fits"), ct));
            var session = new ImagingSession(lightsDir, "synth/rggb", "SynthBayer", "SynthRgb", "", [.. ReadFrames(lightsDir, "light_*.fits")]);
            var registered = await SessionRegistrar.RegisterAsync(
                session, calibrator, Path.Combine(_dir, "scratch"), minSubs: 4, cancellationToken: ct);
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
    }
}
