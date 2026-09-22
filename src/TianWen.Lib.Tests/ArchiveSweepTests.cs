using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// These two sweeps replace and delete names for irreplaceable frames, so every test asserts on
    /// what is still READABLE afterwards, never only on the verdict returned. A sweep that reports
    /// success and leaves a path naming the wrong file is the failure worth catching.
    /// </summary>
    [Collection("Imaging")]
    public class ArchiveSweepTests
    {
        private const int Block = FitsFixture.Block;

        private static string TempDir([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
            => FitsFixture.CreateTempDir("TianWen.ArchiveSweepTests", name ?? "unnamed");

        /// <summary>The primary header as text, so a test can ask whether a card is THERE rather
        /// than trusting a parser to agree with the writer.</summary>
        private static string HeaderText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, Block * 4));
            var end = text.IndexOf("END     ", StringComparison.Ordinal);
            return end >= 0 ? text[..end] : text;
        }

        // ---- ArchiveLinkSweep -------------------------------------------------------------

        [Fact]
        public async Task GivenARawFrameAndItsCuratedTwin_WhenLinking_ThenOneFileCarriesBothNamesAndTheCuratedHeaderWins()
        {
            // The whole point of the sweep. The curated frame carries a FILTER identity established
            // by measurement; the raw one never had it. After linking there is ONE file, and the
            // name in the raw tree resolves to the curated header.
            var dir = TempDir();
            var (raw, payload) = FitsFixture.WriteFits(dir, "raw.fits", ["IMAGETYP= 'LIGHT'"]);
            var (curated, _) = FitsFixture.WriteFits(
                dir, "curated.fits", ["IMAGETYP= 'LIGHT'", "FILTER  = 'Optolong L-Ultimate 3nm'"]);
            var curatedBefore = FitsFixture.ShaOfFile(curated);

            var result = await ArchiveLinkSweep.LinkAsync(
                raw, curated, apply: true, cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(ArchiveLinkSweep.LinkOutcome.Linked);
            result.BytesReleased.ShouldBeGreaterThan(0);
            FitsFixture.IdentityOf(raw).IsSameFileAs(FitsFixture.IdentityOf(curated)).ShouldBeTrue();
            HeaderText(raw).ShouldContain("Optolong L-Ultimate 3nm");
            FitsFixture.ShaOfFile(curated).ShouldBe(curatedBefore);
            // The pixels are the same pixels either way; that is why the link was allowed at all.
            File.ReadAllBytes(raw).TakeLast(payload.Length).ShouldBe(payload);
        }

        [Fact]
        public async Task GivenTheRawStatesACardTheCuratedDoesNot_WhenLinking_ThenItIsRefusedAndNamesTheCard()
        {
            // The veto. Linking discards the raw header, so a card only the raw has is about to
            // stop existing. Measured over 1,500 real pairs this never happens, which is exactly
            // why it must be checked rather than assumed.
            var dir = TempDir();
            var (raw, _) = FitsFixture.WriteFits(dir, "raw.fits", ["IMAGETYP= 'LIGHT'", "FOCTEMP =    18.4"]);
            var (curated, _) = FitsFixture.WriteFits(dir, "curated.fits", ["IMAGETYP= 'LIGHT'"]);
            var rawBefore = FitsFixture.ShaOfFile(raw);

            var result = await ArchiveLinkSweep.LinkAsync(
                raw, curated, apply: true, cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(ArchiveLinkSweep.LinkOutcome.HeaderWouldLose);
            result.Detail.ShouldContain("FOCTEMP");
            FitsFixture.ShaOfFile(raw).ShouldBe(rawBefore);
            FitsFixture.IdentityOf(raw).IsSameFileAs(FitsFixture.IdentityOf(curated)).ShouldBeFalse();
        }

        [Fact]
        public async Task GivenDifferentPixels_WhenLinking_ThenItIsRefusedEvenThoughTheHeadersAgree()
        {
            // A ledger can be stale, and this archive's was 6 percent stale when the sweep was
            // written, so the digests are recomputed at the moment of writing and not looked up.
            var dir = TempDir();
            var (raw, _) = FitsFixture.WriteFits(dir, "raw.fits", ["IMAGETYP= 'LIGHT'"], seed: 1);
            var (curated, _) = FitsFixture.WriteFits(dir, "curated.fits", ["IMAGETYP= 'LIGHT'"], seed: 2);

            var result = await ArchiveLinkSweep.LinkAsync(
                raw, curated, apply: true, cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(ArchiveLinkSweep.LinkOutcome.PayloadDiffers);
            FitsFixture.IdentityOf(raw).IsSameFileAs(FitsFixture.IdentityOf(curated)).ShouldBeFalse();
        }

        [Fact]
        public async Task GivenADryRun_WhenLinking_ThenTheVerdictMatchesTheRealRunAndNothingIsWritten()
        {
            var dir = TempDir();
            var (raw, _) = FitsFixture.WriteFits(dir, "raw.fits", ["IMAGETYP= 'LIGHT'"]);
            var (curated, _) = FitsFixture.WriteFits(dir, "curated.fits", ["IMAGETYP= 'LIGHT'", "FILTER  = 'Ha'"]);
            var rawBefore = FitsFixture.ShaOfFile(raw);

            var dry = await ArchiveLinkSweep.LinkAsync(
                raw, curated, apply: false, cancellationToken: TestContext.Current.CancellationToken);

            // The dry run's own effect is none, and that is proven against the FILE: same bytes,
            // and the raw path still names its own file rather than the curated one. Asserting
            // only on the returned verdict would pass against a sweep that wrote anyway.
            dry.Outcome.ShouldBe(ArchiveLinkSweep.LinkOutcome.Linked);
            FitsFixture.ShaOfFile(raw).ShouldBe(rawBefore);
            FitsFixture.IdentityOf(raw).IsSameFileAs(FitsFixture.IdentityOf(curated)).ShouldBeFalse();

            var applied = await ArchiveLinkSweep.LinkAsync(
                raw, curated, apply: true, cancellationToken: TestContext.Current.CancellationToken);

            // ...and what it predicted is what the real run does, which is the property that makes
            // a dry run worth reading before committing to 472 GB of re-pointing.
            applied.Outcome.ShouldBe(dry.Outcome);
            applied.BytesReleased.ShouldBe(dry.BytesReleased);
            FitsFixture.IdentityOf(raw).IsSameFileAs(FitsFixture.IdentityOf(curated)).ShouldBeTrue();
        }

        [Fact]
        public async Task GivenTwoNamesThatAlreadyShareOneFile_WhenLinking_ThenItSaysSoAndDoesNothing()
        {
            var dir = TempDir();
            var (curated, _) = FitsFixture.WriteFits(dir, "curated.fits", ["IMAGETYP= 'LIGHT'"]);
            var raw = Path.Combine(dir, "raw.fits");
            FitsFixture.LinkOrSkip(raw, curated);

            var result = await ArchiveLinkSweep.LinkAsync(
                raw, curated, apply: true, cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(ArchiveLinkSweep.LinkOutcome.AlreadyOneFile);
            result.BytesReleased.ShouldBe(0);
        }

        [Fact]
        public async Task GivenRequireIdentical_WhenTheCuratedHeaderAddsACard_ThenItIsRefused()
        {
            // The strict policy is for an archive whose curation is not trusted yet: it links only
            // frames that are already byte for byte the same.
            var dir = TempDir();
            var (raw, _) = FitsFixture.WriteFits(dir, "raw.fits", ["IMAGETYP= 'LIGHT'"]);
            var (curated, _) = FitsFixture.WriteFits(dir, "curated.fits", ["IMAGETYP= 'LIGHT'", "FILTER  = 'Ha'"]);

            var strict = await ArchiveLinkSweep.LinkAsync(
                raw, curated, ArchiveLinkSweep.HeaderPolicy.RequireIdentical, apply: true,
                cancellationToken: TestContext.Current.CancellationToken);
            var lenient = await ArchiveLinkSweep.LinkAsync(
                raw, curated, ArchiveLinkSweep.HeaderPolicy.CuratedWins, apply: false,
                cancellationToken: TestContext.Current.CancellationToken);

            strict.Outcome.ShouldBe(ArchiveLinkSweep.LinkOutcome.HeaderWouldLose);
            lenient.Outcome.ShouldBe(ArchiveLinkSweep.LinkOutcome.Linked);
        }

        [Fact]
        public void GivenSeveralCuratedFrames_WhenIndexingByPayload_ThenARawFrameFindsItsTwinDespiteADifferentHeader()
        {
            var dir = TempDir();
            var (a, _) = FitsFixture.WriteFits(dir, "a.fits", ["IMAGETYP= 'LIGHT'", "FILTER  = 'Ha'"], seed: 1);
            var (b, _) = FitsFixture.WriteFits(dir, "b.fits", ["IMAGETYP= 'LIGHT'", "FILTER  = 'OIII'"], seed: 2);
            var (raw, _) = FitsFixture.WriteFits(dir, "raw.fits", ["IMAGETYP= 'LIGHT'"], seed: 2);

            var index = ArchiveLinkSweep.IndexByPayload([a, b], TestContext.Current.CancellationToken);
            var digest = TianWen.Lib.Imaging.Stacking.StackManifest.DigestData(raw);

            index.Count.ShouldBe(2);
            index[digest].ShouldBe(b);
        }

        [Fact]
        public async Task GivenARawFrameThatAlreadyHasASecondNameOfItsOwn_WhenLinking_ThenNoBytesAreReportedReleased()
        {
            // Releasing bytes means the extent lost its LAST name. A raw frame that is itself
            // multiply linked keeps its bytes when one name moves away, and reporting the file
            // size there would overstate the reclaim once per name.
            var dir = TempDir();
            var (raw, _) = FitsFixture.WriteFits(dir, "raw.fits", ["IMAGETYP= 'LIGHT'"]);
            var alsoRaw = Path.Combine(dir, "raw-second-name.fits");
            FitsFixture.LinkOrSkip(alsoRaw, raw);
            var (curated, _) = FitsFixture.WriteFits(dir, "curated.fits", ["IMAGETYP= 'LIGHT'", "FILTER  = 'Ha'"]);

            var result = await ArchiveLinkSweep.LinkAsync(
                raw, curated, apply: true, cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(ArchiveLinkSweep.LinkOutcome.Linked);
            result.BytesReleased.ShouldBe(0);
            // The other raw name still holds the original frame, untouched and untagged.
            HeaderText(alsoRaw).ShouldNotContain("FILTER");
        }

        [Fact]
        public void GivenAFrameAlreadyLinkedIntoTheCuratedTree_WhenAskedBeforeReading_ThenItSaysSo()
        {
            // What makes an interrupted sweep restartable. A full pass reads every frame to digest
            // it, so a re-run that re-read the part already finished would cost the same hours
            // twice. This answers from the directory entries and reads nothing.
            var root = TempDir();
            var curatedRoot = Path.Combine(root, "curated");
            var rawRoot = Path.Combine(root, "raw");
            Directory.CreateDirectory(curatedRoot);
            Directory.CreateDirectory(rawRoot);
            var (curated, _) = FitsFixture.WriteFits(curatedRoot, "frame.fits", ["IMAGETYP= 'LIGHT'"]);
            var done = Path.Combine(rawRoot, "done.fits");
            FitsFixture.LinkOrSkip(done, curated);
            var (notDone, _) = FitsFixture.WriteFits(rawRoot, "not-done.fits", ["IMAGETYP= 'LIGHT'"]);

            ArchiveLinkSweep.AlreadyLinkedInto(done, curatedRoot).ShouldBeTrue();
            ArchiveLinkSweep.AlreadyLinkedInto(notDone, curatedRoot).ShouldBeFalse();
            // A second name that is NOT under the curated tree is not the sweep's work either, so
            // this must not read as done just because the link count is up.
            var elsewhere = Path.Combine(rawRoot, "second-raw-name.fits");
            FitsFixture.LinkOrSkip(elsewhere, notDone);
            ArchiveLinkSweep.AlreadyLinkedInto(notDone, curatedRoot).ShouldBeFalse();
        }

        // ---- ArchivePruneSweep ------------------------------------------------------------

        [Fact]
        public void GivenAFolderWhoseFilesAllHaveNamesElsewhere_WhenPruning_ThenItGoesAndTheDataSurvives()
        {
            var root = TempDir();
            var keep = Path.Combine(root, "curated");
            var drop = Path.Combine(root, "raw");
            Directory.CreateDirectory(keep);
            Directory.CreateDirectory(drop);
            var (curated, payload) = FitsFixture.WriteFits(keep, "frame.fits", ["IMAGETYP= 'LIGHT'"]);
            FitsFixture.LinkOrSkip(Path.Combine(drop, "frame.fits"), curated);

            var verdict = ArchivePruneSweep.Consider(drop, apply: true, TestContext.Current.CancellationToken);

            verdict.Outcome.ShouldBe(ArchivePruneSweep.PruneOutcome.Pruned);
            verdict.Files.ShouldBe(1);
            Directory.Exists(drop).ShouldBeFalse();
            File.ReadAllBytes(curated).TakeLast(payload.Length).ShouldBe(payload);
        }

        [Fact]
        public void GivenTwoNamesForOneFileBothInsideTheFolder_WhenPruning_ThenItIsRefused()
        {
            // The subtlety a link COUNT cannot see. Both files report two names, so every
            // count-based test passes, and deleting the folder would still destroy the only copy.
            var root = TempDir();
            var drop = Path.Combine(root, "raw");
            Directory.CreateDirectory(drop);
            var (first, _) = FitsFixture.WriteFits(drop, "frame.fits", ["IMAGETYP= 'LIGHT'"]);
            FitsFixture.LinkOrSkip(Path.Combine(drop, "frame-again.fits"), first);

            FitsFixture.IdentityOf(first).LinkCount.ShouldBe(2);
            var verdict = ArchivePruneSweep.Consider(drop, apply: true, TestContext.Current.CancellationToken);

            verdict.Outcome.ShouldBe(ArchivePruneSweep.PruneOutcome.WouldOrphan);
            verdict.Orphans.ShouldBe(2);
            Directory.Exists(drop).ShouldBeTrue();
        }

        [Fact]
        public void GivenACaptureSidecarThatWasNeverCurated_WhenPruning_ThenItIsRefusedAndNamesTheSidecar()
        {
            // SharpCap's CameraSettings.txt is the only record of the white balance, the cooler
            // power and the target temperature, and nothing curated it, so it is never linked.
            // Holding EVERY file to the rule is what stops the prune taking it.
            var root = TempDir();
            var keep = Path.Combine(root, "curated");
            var drop = Path.Combine(root, "raw");
            Directory.CreateDirectory(keep);
            Directory.CreateDirectory(drop);
            var (curated, _) = FitsFixture.WriteFits(keep, "frame.fits", ["IMAGETYP= 'LIGHT'"]);
            FitsFixture.LinkOrSkip(Path.Combine(drop, "frame.fits"), curated);
            File.WriteAllText(Path.Combine(drop, "run.CameraSettings.txt"), "White Bal (R)=65\n");

            var verdict = ArchivePruneSweep.Consider(drop, apply: true, TestContext.Current.CancellationToken);

            verdict.Outcome.ShouldBe(ArchivePruneSweep.PruneOutcome.WouldOrphan);
            verdict.Orphans.ShouldBe(1);
            verdict.Detail.ShouldContain("CameraSettings");
            File.Exists(Path.Combine(drop, "run.CameraSettings.txt")).ShouldBeTrue();
        }

        [Fact]
        public void GivenADryRun_WhenPruning_ThenTheVerdictIsPrunedAndTheFolderSurvives()
        {
            var root = TempDir();
            var keep = Path.Combine(root, "curated");
            var drop = Path.Combine(root, "raw");
            Directory.CreateDirectory(keep);
            Directory.CreateDirectory(drop);
            var (curated, _) = FitsFixture.WriteFits(keep, "frame.fits", ["IMAGETYP= 'LIGHT'"]);
            FitsFixture.LinkOrSkip(Path.Combine(drop, "frame.fits"), curated);

            var verdict = ArchivePruneSweep.Consider(drop, apply: false, TestContext.Current.CancellationToken);

            verdict.Outcome.ShouldBe(ArchivePruneSweep.PruneOutcome.Pruned);
            Directory.Exists(drop).ShouldBeTrue();
        }

        [Fact]
        public void GivenAnEmptyFolder_WhenPruning_ThenItIsLeftAlone()
        {
            // An empty folder is somebody else's business: this pass retires trees that became pure
            // indirection, and deleting an empty one would be a different and unasked-for decision.
            var root = TempDir();
            var drop = Path.Combine(root, "raw");
            Directory.CreateDirectory(drop);

            var verdict = ArchivePruneSweep.Consider(drop, apply: true, TestContext.Current.CancellationToken);

            verdict.Outcome.ShouldBe(ArchivePruneSweep.PruneOutcome.Empty);
            Directory.Exists(drop).ShouldBeTrue();
        }

        [Fact]
        public void GivenASubfolderOfLinks_WhenPruning_ThenTheWholeTreeIsConsidered()
        {
            var root = TempDir();
            var keep = Path.Combine(root, "curated");
            var drop = Path.Combine(root, "raw");
            var nested = Path.Combine(drop, "Light", "session");
            Directory.CreateDirectory(keep);
            Directory.CreateDirectory(nested);
            var (curated, _) = FitsFixture.WriteFits(keep, "frame.fits", ["IMAGETYP= 'LIGHT'"]);
            FitsFixture.LinkOrSkip(Path.Combine(nested, "frame.fits"), curated);

            var verdict = ArchivePruneSweep.Consider(drop, apply: true, TestContext.Current.CancellationToken);

            verdict.Outcome.ShouldBe(ArchivePruneSweep.PruneOutcome.Pruned);
            verdict.Files.ShouldBe(1);
            Directory.Exists(drop).ShouldBeFalse();
            File.Exists(curated).ShouldBeTrue();
        }
    }
}
