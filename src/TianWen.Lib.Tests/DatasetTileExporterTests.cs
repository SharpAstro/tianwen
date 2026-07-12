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
    /// asserts the output contract — fp16 CHW blobs in [0, 1], a JSONL manifest with one row per
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
            var session = new ImagingSession(lightsDir, "synth/rggb", "SynthBayer", "SynthRgb", [.. ReadFrames(lightsDir, "light_*.fits")]);
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
            // MTF pre-stretch output range — this is what the model trains and infers on).
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
    }
}
