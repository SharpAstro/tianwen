using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        /// <summary>Copies the fixture lights as header-valid but data-truncated FITS with shifted
        /// DATE-OBS (so they don't dedup against the source lights). Discovery only reads headers,
        /// so the copies form a session that passes the scan and then explodes at register time —
        /// the fault-isolation case (a bad frame surfacing hours into a bake must skip its session,
        /// never abort the run).</summary>
        private static void WriteTruncatedCopies(string srcDir, string dstDir)
        {
            Directory.CreateDirectory(dstDir);
            foreach (var src in Directory.GetFiles(srcDir, "light_*.fits"))
            {
                var bytes = File.ReadAllBytes(src);
                // Shift the minutes of DATE-OBS (fixture stamps T00:00:0i) for dedup-distinct copies.
                PatchAscii(bytes, "T00:00:0", "T00:59:0");
                File.WriteAllBytes(Path.Combine(dstDir, Path.GetFileName(src)), bytes[..HeaderEnd(bytes)]);
            }
        }

        /// <summary>Patches EVERY occurrence (DATE-OBS plus any sibling date card carrying the same
        /// timestamp) so the exposure start is guaranteed shifted wherever the parser reads it.</summary>
        private static void PatchAscii(byte[] bytes, string find, string replace)
        {
            var findBytes = System.Text.Encoding.ASCII.GetBytes(find);
            var replaceBytes = System.Text.Encoding.ASCII.GetBytes(replace);
            replaceBytes.Length.ShouldBe(findBytes.Length);
            var patched = 0;
            for (var idx = bytes.AsSpan().IndexOf(findBytes); idx >= 0;)
            {
                replaceBytes.CopyTo(bytes, idx);
                patched++;
                var next = bytes.AsSpan(idx + findBytes.Length).IndexOf(findBytes);
                idx = next < 0 ? -1 : idx + findBytes.Length + next;
            }
            patched.ShouldBeGreaterThan(0, $"'{find}' not found -- fixture DATE-OBS format changed?");
        }

        /// <summary>Byte offset of the end of the primary header (the 2880-block containing END).</summary>
        private static int HeaderEnd(byte[] bytes)
        {
            for (var i = 0; i + 4 <= bytes.Length; i += 80)
            {
                if (bytes[i] == 'E' && bytes[i + 1] == 'N' && bytes[i + 2] == 'D' && bytes[i + 3] == ' ')
                {
                    return (i / 2880 + 1) * 2880;
                }
            }
            throw new InvalidOperationException("No FITS END card found");
        }

        [Fact]
        public async Task Run_SyntheticArchive_ProducesTilesManifestSplitReport_ParityGreen_SkipsBrokenSession()
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
            // A second, BROKEN session: header-valid but data-truncated lights. Sorts before M42
            // ("BROKEN" < "M42" ordinal), so it also proves a leading failure doesn't derail the
            // parity gate on the session that does export.
            WriteTruncatedCopies(lightsDir, Path.Combine(root, "BROKEN", "LIGHT"));

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

            // Both sessions discovered; the good one registered, the broken one fault-isolated
            // (counted, logged, skipped) instead of aborting the run.
            result.Sessions.ShouldBe(2);
            result.Registered.ShouldBe(1);
            result.Failed.ShouldBe(1);
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
        public async Task Run_RequireDark_SkipsSessionWithNoDarkInsteadOfRegisteringUncalibrated()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            // Lights only -- no dark library anywhere, so no master dark can resolve (models a camera
            // whose darks were never shot, e.g. the QHY294/Newtonian rig in the real 2026 archive).
            var lightsDir = Path.Combine(root, "M42", "LIGHT");
            Directory.CreateDirectory(lightsDir);
            RgbBayerSyntheticFixture.WriteSyntheticLights(lightsDir);

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
                RequireDarkCalibration = true,
            };

            var result = await DatasetBuildRunner.RunAsync(options, logger: null, progress: null, cancellationToken: ct);

            // The one session is discovered but skipped for lack of a dark -- not registered, not a
            // failure, and nothing exported (an uncalibrated N2N pair is not a valid training sample).
            result.Sessions.ShouldBe(1);
            result.Registered.ShouldBe(0);
            result.Failed.ShouldBe(0);
            result.SkippedNoDark.ShouldBe(1);
            result.TotalTiles.ShouldBe(0);
            File.Exists(result.ManifestPath).ShouldBeFalse();
        }

        /// <summary>Full-file copies with shifted DATE-OBS — a second VALID session (unlike
        /// <see cref="WriteTruncatedCopies"/>, whose copies explode at register time), so the
        /// resume test has two completed sessions to checkpoint.</summary>
        private static void WriteShiftedCopies(string srcDir, string dstDir)
        {
            Directory.CreateDirectory(dstDir);
            foreach (var src in Directory.GetFiles(srcDir, "light_*.fits"))
            {
                var bytes = File.ReadAllBytes(src);
                PatchAscii(bytes, "T00:00:0", "T00:59:0");
                File.WriteAllBytes(Path.Combine(dstDir, Path.GetFileName(src)), bytes);
            }
        }

        [Fact]
        public async Task Run_Resume_SkipsCheckpointedSessions_AndCompletesTheInterruptedOne()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            var m42 = Path.Combine(root, "M42", "LIGHT");
            Directory.CreateDirectory(m42);
            Directory.CreateDirectory(Path.Combine(root, "DARK"));
            RgbBayerSyntheticFixture.WriteSyntheticLights(m42);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(Path.Combine(root, "DARK"));
            WriteShiftedCopies(m42, Path.Combine(root, "N43", "LIGHT"));

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
            first.Registered.ShouldBe(2);

            // Simulate a stop that landed AFTER M42's export and DURING N43's: rows append as the
            // LAST step of an export, so the interrupted N43 has none — plus the torn half-row the
            // kill left behind.
            var rows = File.ReadAllLines(first.ManifestPath).Where(l => l.Trim().Length > 0).ToArray();
            var m42Rows = rows.Where(l =>
                JsonSerializer.Deserialize(l, DatasetManifestJsonContext.Default.TileManifestRow)!
                    .SessionId.StartsWith("M42|", StringComparison.Ordinal)).ToArray();
            m42Rows.Length.ShouldBeInRange(1, rows.Length - 1); // both sessions really are in the manifest
            File.WriteAllText(first.ManifestPath, string.Join('\n', m42Rows) + "\n{\"tile\":\"torn-mid-wr");

            var resumedRun = await DatasetBuildRunner.RunAsync(options with { Resume = true }, cancellationToken: ct);

            // M42 checkpointed, N43 re-exported; totals line up with the uninterrupted run.
            resumedRun.Resumed.ShouldBe(1);
            resumedRun.Registered.ShouldBe(1);
            resumedRun.TotalTiles.ShouldBe(first.TotalTiles);
            resumedRun.ParityChecked.ShouldBeTrue(); // the re-exported session fed the parity gate

            // Manifest healed + complete: every row parseable, per-session counts match the
            // uninterrupted run — M42's rows were neither dropped nor duplicated.
            var counts = await DatasetTileExporter.ReadManifestSessionTileCountsAsync(first.ManifestPath, ct);
            counts.Values.Sum().ShouldBe(first.TotalTiles);
            counts.Single(kv => kv.Key.StartsWith("M42|", StringComparison.Ordinal)).Value.ShouldBe(m42Rows.Length);

            // Resume again with everything complete: nothing re-runs, manifest byte-identical.
            var manifestBytes = File.ReadAllBytes(first.ManifestPath);
            var third = await DatasetBuildRunner.RunAsync(options with { Resume = true }, cancellationToken: ct);
            third.Resumed.ShouldBe(2);
            third.Registered.ShouldBe(0);
            third.TotalTiles.ShouldBe(first.TotalTiles);
            third.ParityChecked.ShouldBeFalse(); // nothing exported this run to gate
            File.ReadAllBytes(first.ManifestPath).ShouldBe(manifestBytes);
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
