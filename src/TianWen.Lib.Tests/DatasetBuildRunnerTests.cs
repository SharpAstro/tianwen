using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// End-to-end coverage for <see cref="DatasetBuildRunner"/> (dataset builder P0/#43 — the
    /// one-command exit gate). Lays out a synthetic archive (lights under a session directory, a
    /// SHARED dark library beside it), runs the full build, and asserts the complete output contract:
    /// archive-wide calibration resolved + cached, fp16 tiles + JSONL manifest written, pinned split
    /// + PSF/noise report produced, and the in-run zero-skew parity gate green.
    /// </summary>
    [Collection("Imaging")]
    public class DatasetBuildRunnerTests(ITestOutputHelper output) : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsrun-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task Run_SyntheticArchive_ProducesTilesManifestSplitReport_ParityGreen()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            // Lights under <root>/M42/LIGHT (a session); darks under <root>/DARK (a shared cal library,
            // resolved by header match -- NOT by folder).
            var lightsDir = Path.Combine(root, "M42", "LIGHT");
            var darksDir = Path.Combine(root, "DARK");
            Directory.CreateDirectory(lightsDir);
            Directory.CreateDirectory(darksDir);
            RgbBayerSyntheticFixture.WriteSyntheticLights(lightsDir);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);

            var outDir = Path.Combine(_dir, "out");
            var options = new DatasetBuildOptions
            {
                ArchiveRoots = [root],
                OutputDir = outDir,
                MinExposure = TimeSpan.FromSeconds(0.5),   // fixture lights are 1s
                MaxExposure = TimeSpan.FromMinutes(5),
                MinSubsPerSession = 4,                     // fixture has 8 lights
                TileSize = 64,
                CellsPerSession = 20,
                SubsPerCell = 3,
                TestFraction = 0.15,
            };

            var progress = new Progress<string>(output.WriteLine);
            var result = await DatasetBuildRunner.RunAsync(options, logger: null, progress: progress, cancellationToken: ct);

            // One session discovered + registered; tiles produced.
            result.Sessions.ShouldBe(1);
            result.Registered.ShouldBe(1);
            result.TotalTiles.ShouldBeGreaterThan(0);

            // The in-run zero-skew gate ran and is byte-exact.
            result.ParityChecked.ShouldBeTrue();
            result.ParityMaxDiff.ShouldBe(0.0);

            // Manifest: one JSONL row per tile.
            File.Exists(result.ManifestPath).ShouldBeTrue();
            var manifestLines = File.ReadAllLines(result.ManifestPath).Count(l => l.Trim().Length > 0);
            manifestLines.ShouldBe(result.TotalTiles);

            // Tiles on disk.
            var tileFiles = Directory.GetFiles(Path.Combine(outDir, "tiles"), "*" + DatasetTileExporter.TileExtension, SearchOption.AllDirectories);
            tileFiles.Length.ShouldBe(result.TotalTiles);

            // Calibration was resolved archive-wide and the dark master cached (build-once).
            var mastersDir = Path.Combine(outDir, "masters");
            Directory.Exists(mastersDir).ShouldBeTrue();
            Directory.GetFiles(mastersDir, "master_dark_*.fits").Length.ShouldBe(1);

            // Pinned split + PSF/noise report written.
            File.Exists(result.SplitPath).ShouldBeTrue();
            File.Exists(result.ReportPath).ShouldBeTrue();
            (await File.ReadAllTextAsync(result.ReportPath, ct)).ShouldContain("Field-radius PSF profile");

            // Scratch cleaned up (peak disk bounded to one session).
            Directory.Exists(Path.Combine(outDir, "_scratch")).ShouldBeFalse();

            output.WriteLine($"sessions={result.Sessions} registered={result.Registered} tiles={result.TotalTiles} test={result.TestSessions}");
        }

        [Fact]
        public async Task Run_IsIdempotent_ReusesMasterCacheAndReproducesTileCount()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            Directory.CreateDirectory(Path.Combine(root, "M42", "LIGHT"));
            Directory.CreateDirectory(Path.Combine(root, "DARK"));
            RgbBayerSyntheticFixture.WriteSyntheticLights(Path.Combine(root, "M42", "LIGHT"));
            RgbBayerSyntheticFixture.WriteSyntheticDarks(Path.Combine(root, "DARK"));

            var outDir = Path.Combine(_dir, "out");
            var options = new DatasetBuildOptions
            {
                ArchiveRoots = [root],
                OutputDir = outDir,
                MinExposure = TimeSpan.FromSeconds(0.5),
                MaxExposure = TimeSpan.FromMinutes(5),
                MinSubsPerSession = 4,
                TileSize = 64,
                CellsPerSession = 20,
                SubsPerCell = 3,
            };

            var first = await DatasetBuildRunner.RunAsync(options, cancellationToken: ct);
            var darkMtime = File.GetLastWriteTimeUtc(Directory.GetFiles(Path.Combine(outDir, "masters"), "master_dark_*.fits").Single());

            var second = await DatasetBuildRunner.RunAsync(options, cancellationToken: ct);

            // Same tile count; the dark master was a cache hit (not rebuilt).
            second.TotalTiles.ShouldBe(first.TotalTiles);
            File.GetLastWriteTimeUtc(Directory.GetFiles(Path.Combine(outDir, "masters"), "master_dark_*.fits").Single())
                .ShouldBe(darkMtime);
            // Manifest is regenerated fresh (not doubled by the re-run's appends).
            File.ReadAllLines(second.ManifestPath).Count(l => l.Trim().Length > 0).ShouldBe(second.TotalTiles);
        }
    }
}
