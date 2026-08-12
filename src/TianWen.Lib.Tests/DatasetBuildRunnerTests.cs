using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// End-to-end coverage for <see cref="DatasetBuildRunner"/> (dataset builder P0/#43, the
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
        /// so the copies form a session that passes the scan and then explodes at register time; 
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

        /// <summary>Full-file copies with shifted DATE-OBS; a second VALID session (unlike
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

        /// <summary>
        /// Two builds over one output directory must not both proceed. The failure they produce
        /// otherwise is not a race that merely loses work, it is a wipe that reads like a pipeline
        /// bug: the per-session scratch is deleted in a <c>finally</c>, so the run that finishes
        /// first removes the warped subs the other is still tiling, and the loser dies on
        /// <c>FileNotFoundException: warped_0113.fits</c> while the winner's manifest and PSF record
        /// look perfectly complete. This cost a full 8-minute validation session and about an hour of
        /// misdirected diagnosis, so the guard is pinned rather than trusted.
        ///
        /// <para>Also asserts the two things that make the lock safe to leave in place: it does NOT
        /// survive the process (nothing to clean up by hand after a kill, since a stale lock file
        /// that blocked every later run would be worse than the collision), and the scratch root's
        /// own lock sits BESIDE the tree rather than inside it, or the held handle would block the
        /// per-session wipe it exists to protect.</para>
        /// </summary>
        [Fact]
        public async Task Run_RefusesToStartWhileAnotherBuildHoldsTheOutputDirectory()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            var m42 = Path.Combine(root, "M42", "LIGHT");
            Directory.CreateDirectory(m42);
            Directory.CreateDirectory(Path.Combine(root, "DARK"));
            RgbBayerSyntheticFixture.WriteSyntheticLights(m42);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(Path.Combine(root, "DARK"));

            var outDir = Path.Combine(_dir, "out");
            Directory.CreateDirectory(outDir);
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

            // Stand in for the other process by holding its lock exactly as it would.
            var lockPath = Path.Combine(outDir, DatasetBuildRunner.RunLockFileName);
            using (new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var refused = await Should.ThrowAsync<InvalidOperationException>(
                    () => DatasetBuildRunner.RunAsync(options, cancellationToken: ct));
                // Refused before any work: the message has to name the way out, and the archive scan
                // (seek-bound, minutes on a spindle) must not have run.
                refused.Message.ShouldContain(outDir);
                refused.Message.ShouldContain("--out");
                Directory.Exists(Path.Combine(outDir, "tiles")).ShouldBeFalse();
            }

            // The file is deliberately left behind here: a lock is a live handle, never a tombstone,
            // so a leftover path (a copied output directory, a share that kept the entry) must not
            // refuse a run that nothing is actually holding.
            File.Exists(lockPath).ShouldBeTrue();
            var built = await DatasetBuildRunner.RunAsync(options, cancellationToken: ct);
            built.Registered.ShouldBe(1);
            // Neither lock outlives the run, and the scratch tree is gone rather than blocked open.
            File.Exists(lockPath).ShouldBeFalse();
            var scratchRoot = Path.Combine(outDir, "_scratch");
            Directory.Exists(scratchRoot).ShouldBeFalse();
            File.Exists(scratchRoot + ".lock").ShouldBeFalse();
        }

        [Fact]
        public async Task Run_ReportOnly_ReRendersFromTheStore_WithNoArchiveAndNoWork()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            var m42 = Path.Combine(root, "M42", "LIGHT");
            Directory.CreateDirectory(m42);
            Directory.CreateDirectory(Path.Combine(root, "DARK"));
            RgbBayerSyntheticFixture.WriteSyntheticLights(m42);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(Path.Combine(root, "DARK"));

            var outDir = Path.Combine(_dir, "out");
            var built = await DatasetBuildRunner.RunAsync(
                new DatasetBuildOptions
                {
                    ArchiveRoots = [root],
                    OutputDir = outDir,
                    MinExposure = TimeSpan.FromSeconds(0.5),
                    MaxExposure = TimeSpan.FromMinutes(5),
                    MinSubsPerSession = 4,
                    TileSize = 64,
                    CellsPerSession = 20,
                    SubsPerCell = 3,
                },
                cancellationToken: ct);
            built.Registered.ShouldBe(1);
            File.Exists(built.ReportPath).ShouldBeTrue();
            var manifestBefore = File.ReadAllBytes(built.ManifestPath);

            // Delete the archive outright: a re-render must read the OUTPUT directory only, which is
            // what lets it run with the archive disk unmounted. If it still scanned, this would fail
            // rather than merely being slow, which is the point of deleting instead of mocking.
            Directory.Delete(root, recursive: true);
            File.Delete(built.ReportPath);

            var rendered = await DatasetBuildRunner.RunAsync(
                new DatasetBuildOptions { ArchiveRoots = [], OutputDir = outDir, ReportOnly = true },
                cancellationToken: ct);

            File.Exists(rendered.ReportPath).ShouldBeTrue();
            rendered.ReportPath.ShouldBe(built.ReportPath);
            // Session set comes from the manifest, and nothing was registered, exported or measured.
            rendered.Sessions.ShouldBe(1);
            rendered.Registered.ShouldBe(0);
            rendered.Failed.ShouldBe(0);
            rendered.PsfRemeasured.ShouldBe(0);
            rendered.PsfMissing.ShouldBe(0);
            rendered.TotalTiles.ShouldBe(built.TotalTiles);
            // The manifest is a checkpoint a re-render has no business touching.
            File.ReadAllBytes(built.ManifestPath).ShouldBe(manifestBefore);

            var md = await File.ReadAllTextAsync(rendered.ReportPath, ct);
            md.ShouldContain("Field-radius PSF profile");
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
            // LAST step of an export, so the interrupted N43 has none, plus the torn half-row the
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
            // uninterrupted run: M42's rows were neither dropped nor duplicated.
            var counts = await DatasetTileExporter.ReadManifestCheckpointsAsync(first.ManifestPath, ct);
            counts.Values.Sum(c => c.TileCount).ShouldBe(first.TotalTiles);
            counts.Single(kv => kv.Key.StartsWith("M42|", StringComparison.Ordinal)).Value.TileCount.ShouldBe(m42Rows.Length);

            // Resume again with everything complete: nothing re-runs, manifest byte-identical.
            var manifestBytes = File.ReadAllBytes(first.ManifestPath);
            var third = await DatasetBuildRunner.RunAsync(options with { Resume = true }, cancellationToken: ct);
            third.Resumed.ShouldBe(2);
            third.Registered.ShouldBe(0);
            third.TotalTiles.ShouldBe(first.TotalTiles);
            third.ParityChecked.ShouldBeFalse(); // nothing exported this run to gate
            File.ReadAllBytes(first.ManifestPath).ShouldBe(manifestBytes);
        }

        /// <summary>
        /// The integrated session master is retained, named with the SAME slug as its tile directory,
        /// and never rewritten once present.
        ///
        /// <para><b>Why this artifact matters more than it looks.</b> The master is the only perishable
        /// output of a build: scratch is wiped per session, so afterwards it exists nowhere, and the
        /// field-radius PSF profile (the input to the deconvolver's position-varying sweep) is measured
        /// ON it. Recovering anything measured there therefore meant registering the whole session
        /// again, which cost two full 7h16m archive re-runs in two days, once for a star-detection fix
        /// and once for an FWHM estimator fix. Neither needed the subs; both needed only a master the
        /// run had already built and discarded.</para>
        ///
        /// <para>The not-rewritten half is the load-bearing assertion. A resume must not spend 108 MB
        /// of I/O per already-retained session, and the write goes via a <c>.partial</c> temp name
        /// precisely so a kill mid-write cannot leave a truncated FITS that a later run would mistake
        /// for a complete one, which is the manifest-claims-missing-tiles bug in another costume.</para>
        /// </summary>
        [Fact]
        public async Task Run_RetainsTheSessionMaster_WithTheTileSlug_AndDoesNotRewriteItOnResume()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            var m42 = Path.Combine(root, "M42", "LIGHT");
            Directory.CreateDirectory(m42);
            Directory.CreateDirectory(Path.Combine(root, "DARK"));
            RgbBayerSyntheticFixture.WriteSyntheticLights(m42);
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
            first.Registered.ShouldBe(1);

            var mastersDir = Path.Combine(outDir, "session-masters");
            var masters = Directory.GetFiles(mastersDir, "*.fits");
            masters.Length.ShouldBe(1, "one registered session, one retained master");
            // No .partial survives a clean run: the temp name is moved into place, never left behind.
            Directory.GetFiles(mastersDir, "*.partial").ShouldBeEmpty();

            // The master's name is the tile directory's name, so one traces to the other directly.
            var tileDirs = Directory.GetDirectories(Path.Combine(outDir, "tiles"));
            tileDirs.Length.ShouldBe(1);
            Path.GetFileNameWithoutExtension(masters[0]).ShouldBe(Path.GetFileName(tileDirs[0]));

            // It is a readable FITS carrying the integration, not a stub.
            Image.TryReadFitsFile(masters[0], out var master, out _).ShouldBeTrue();
            master.ShouldNotBeNull();
            master.Width.ShouldBeGreaterThan(0);
            master.Height.ShouldBeGreaterThan(0);
            output.WriteLine($"retained master {Path.GetFileName(masters[0])}: {master.Width}x{master.Height}x{master.ChannelCount}");

            // Resume with everything already done: the master must not be rewritten.
            var stampBefore = File.GetLastWriteTimeUtc(masters[0]);
            var lengthBefore = new FileInfo(masters[0]).Length;
            var second = await DatasetBuildRunner.RunAsync(options with { Resume = true }, cancellationToken: ct);
            second.Resumed.ShouldBe(1);
            File.GetLastWriteTimeUtc(masters[0]).ShouldBe(stampBefore);
            new FileInfo(masters[0]).Length.ShouldBe(lengthBefore);

            // Opting out writes nothing, so a caller short of disk can genuinely decline it.
            var optOutDir = Path.Combine(_dir, "out-noretain");
            var third = await DatasetBuildRunner.RunAsync(
                options with { OutputDir = optOutDir, RetainSessionMasters = false }, cancellationToken: ct);
            third.Registered.ShouldBe(1);
            Directory.Exists(Path.Combine(optOutDir, "session-masters")).ShouldBeFalse();
        }

        /// <summary>
        /// A resumed run must not narrow the PSF/noise report. It used to: the report was in-memory
        /// derived state rewritten at the end of every run, so resuming an archive where only one
        /// session needed work replaced a whole-archive report with a one-session one, and the rest
        /// could not be recovered (the field-radius profile is measured on the session master, which
        /// lives in scratch wiped per session). This drives a real two-session build, resumes it, and
        /// asserts the rendered report still describes BOTH.
        /// </summary>
        [Fact]
        public async Task Run_Resume_KeepsThePsfReportCoveringEverySessionEverMeasured()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            var m42 = Path.Combine(root, "M42", "LIGHT");
            Directory.CreateDirectory(m42);
            Directory.CreateDirectory(Path.Combine(root, "DARK"));
            RgbBayerSyntheticFixture.WriteSyntheticLights(m42);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(Path.Combine(root, "DARK"));
            WriteShiftedCopies(m42, Path.Combine(root, "N43", "LIGHT"));

            var options = new DatasetBuildOptions
            {
                ArchiveRoots = [root],
                OutputDir = Path.Combine(_dir, "out"),
                MinExposure = TimeSpan.FromSeconds(0.5),
                MaxExposure = TimeSpan.FromMinutes(5),
                MinSubsPerSession = 4,
                TileSize = 64,
                CellsPerSession = 20,
                SubsPerCell = 3,
            };

            var first = await DatasetBuildRunner.RunAsync(options, cancellationToken: ct);
            first.Registered.ShouldBe(2);
            var fullReport = await File.ReadAllTextAsync(first.ReportPath, ct);
            fullReport.ShouldContain("- Sessions: 2");
            (await DatasetPsfStore.ReadAsync(first.PsfStorePath, cancellationToken: ct)).Count.ShouldBe(2);

            // Resume with everything already exported: nothing is re-registered, which is precisely
            // the case that used to destroy the report.
            var resumed = await DatasetBuildRunner.RunAsync(options with { Resume = true }, cancellationToken: ct);
            resumed.Registered.ShouldBe(0);
            resumed.Resumed.ShouldBe(2);
            resumed.PsfMissing.ShouldBe(0);

            var afterResume = await File.ReadAllTextAsync(first.ReportPath, ct);
            afterResume.ShouldContain("- Sessions: 2");
            // Byte-identical, not merely non-empty: rebuilt from the store in a deterministic order,
            // so a resume is a no-op on the report rather than a re-derivation that could drift.
            afterResume.ShouldBe(fullReport);
        }

        /// <summary>
        /// --regen-psf fills GAPS and --force-psf RE-MEASURES REGARDLESS, and the difference matters
        /// because the sessions whose records are wrong after an estimator change are exactly the ones
        /// that already have a record. The gap-fill cannot reach them by construction, so before the
        /// force flag existed the only way to re-measure was to delete the whole store by hand, which
        /// also discarded the records of sessions that were still fine.
        /// </summary>
        [Fact]
        public async Task Run_ForcePsf_ReMeasuresSessionsThatAlreadyHaveARecord_WhereRegenPsfWillNot()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            var m42 = Path.Combine(root, "M42", "LIGHT");
            Directory.CreateDirectory(m42);
            Directory.CreateDirectory(Path.Combine(root, "DARK"));
            RgbBayerSyntheticFixture.WriteSyntheticLights(m42);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(Path.Combine(root, "DARK"));

            var options = new DatasetBuildOptions
            {
                ArchiveRoots = [root],
                OutputDir = Path.Combine(_dir, "out"),
                MinExposure = TimeSpan.FromSeconds(0.5),
                MaxExposure = TimeSpan.FromMinutes(5),
                MinSubsPerSession = 4,
                TileSize = 64,
                CellsPerSession = 20,
                SubsPerCell = 3,
            };

            var first = await DatasetBuildRunner.RunAsync(options, cancellationToken: ct);
            first.Registered.ShouldBe(1);
            var tilesDir = Path.Combine(options.OutputDir, "tiles");
            var tilesAfterFirst = Directory.GetFiles(tilesDir, "*.f16", SearchOption.AllDirectories).Length;

            // Gap-fill has no gap to fill, so it resumes without measuring anything.
            var regen = await DatasetBuildRunner.RunAsync(
                options with { Resume = true, RegenPsfForExportedSessions = true }, cancellationToken: ct);
            regen.Resumed.ShouldBe(1);
            regen.PsfRemeasured.ShouldBe(0);

            // Force re-measures the very same session.
            var forced = await DatasetBuildRunner.RunAsync(
                options with { Resume = true, ForcePsfRemeasure = true }, cancellationToken: ct);
            forced.PsfRemeasured.ShouldBe(1);
            forced.Resumed.ShouldBe(0);
            // A PSF-only pass still counts as Registered, because it genuinely re-registers the
            // session to rebuild the master the measurement needs; only the TILES are spared.
            // PsfRemeasured is the field that distinguishes it from a full export.
            forced.Registered.ShouldBe(1);

            // Tiles are untouched by a re-measure, and the tile count is still banked.
            Directory.GetFiles(tilesDir, "*.f16", SearchOption.AllDirectories).Length.ShouldBe(tilesAfterFirst);
            forced.TotalTiles.ShouldBe(first.TotalTiles);

            // Last-wins by id, so the store gained a line but the report still covers one session.
            var store = await DatasetPsfStore.ReadAsync(first.PsfStorePath, cancellationToken: ct);
            store.Count.ShouldBe(1);
            (await File.ReadAllLinesAsync(first.PsfStorePath, ct))
                .Count(l => l.Trim().Length > 0).ShouldBe(2);
        }

        /// <summary>
        /// The manifest is a claim about the past, not proof. Deleting a session's tiles used to leave
        /// resume reporting "already exported", skipping it, and finishing with exit 0 while counting
        /// tiles that were gone; the manifest and the filesystem then disagreed with nothing to say
        /// so. A checkpoint is now honoured only if the tiles are actually there.
        /// </summary>
        [Fact]
        public async Task Run_Resume_ReRegistersASessionWhoseTilesWentMissing()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            var m42 = Path.Combine(root, "M42", "LIGHT");
            Directory.CreateDirectory(m42);
            Directory.CreateDirectory(Path.Combine(root, "DARK"));
            RgbBayerSyntheticFixture.WriteSyntheticLights(m42);
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
            first.Registered.ShouldBe(1);
            first.TotalTiles.ShouldBeGreaterThan(0);

            // Wipe the tiles but leave the manifest claiming them, exactly the state a manual cleanup
            // (or a half-deleted output dir) leaves behind.
            var tileDir = Directory.GetDirectories(Path.Combine(outDir, "tiles")).Single();
            Directory.Delete(tileDir, recursive: true);

            var resumed = await DatasetBuildRunner.RunAsync(options with { Resume = true }, cancellationToken: ct);

            resumed.Resumed.ShouldBe(0);        // NOT treated as already done
            resumed.Registered.ShouldBe(1);     // re-registered instead
            resumed.TotalTiles.ShouldBe(first.TotalTiles);
            Directory.EnumerateFiles(tileDir, "*.f16").Count().ShouldBe(first.TotalTiles);
        }

        /// <summary>A fresh (non-resume) run over an output dir that already has a manifest rotates it
        /// aside rather than deleting it: the tiles it describes are still on disk, and erasing the
        /// only record of them leaves them unaccounted for.</summary>
        [Fact]
        public async Task Run_WithoutResume_RotatesAnExistingManifestInsteadOfDeletingIt()
        {
            var ct = TestContext.Current.CancellationToken;
            var root = Path.Combine(_dir, "archive");
            var m42 = Path.Combine(root, "M42", "LIGHT");
            Directory.CreateDirectory(m42);
            Directory.CreateDirectory(Path.Combine(root, "DARK"));
            RgbBayerSyntheticFixture.WriteSyntheticLights(m42);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(Path.Combine(root, "DARK"));

            var options = new DatasetBuildOptions
            {
                ArchiveRoots = [root],
                OutputDir = Path.Combine(_dir, "out"),
                MinExposure = TimeSpan.FromSeconds(0.5),
                MaxExposure = TimeSpan.FromMinutes(5),
                MinSubsPerSession = 4,
                TileSize = 64,
                CellsPerSession = 20,
                SubsPerCell = 3,
            };

            var first = await DatasetBuildRunner.RunAsync(options, cancellationToken: ct);
            var original = await File.ReadAllBytesAsync(first.ManifestPath, ct);

            await DatasetBuildRunner.RunAsync(options, cancellationToken: ct);

            var rotated = first.ManifestPath + ".bak-1";
            File.Exists(rotated).ShouldBeTrue();
            (await File.ReadAllBytesAsync(rotated, ct)).ShouldBe(original);

            // A third fresh run rotates to the next free index rather than clobbering the first backup.
            await DatasetBuildRunner.RunAsync(options, cancellationToken: ct);
            File.Exists(first.ManifestPath + ".bak-2").ShouldBeTrue();
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
