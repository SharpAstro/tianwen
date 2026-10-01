using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Coverage for the dataset builder's P0 exit gates (#42): the pinned by-session train/test split
    /// (<see cref="DatasetSplitWriter"/>) and the zero-skew parity check
    /// (<see cref="DatasetTileExporter.VerifyParityAsync"/>).
    /// </summary>
    [Collection("Imaging")]
    public class DatasetSplitAndParityTests(ITestOutputHelper output) : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "splitparity-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task Split_IsDeterministic_ByLineage_AndGrowthStable()
        {
            var ids = Enumerable.Range(0, 400).Select(i => $"2026-0{i % 9 + 1}/night{i}|ASI533").ToList();

            var a = DatasetSplitWriter.SelectTestSessions(ids, 0.2);
            var b = DatasetSplitWriter.SelectTestSessions(Enumerable.Reverse(ids), 0.2);

            // Order-independent + roughly the requested fraction.
            a.ShouldBe(b);
            var frac = (double)a.Length / ids.Count;
            frac.ShouldBeInRange(0.12, 0.28);

            // Growth-stable: adding sessions never moves an existing one across the split.
            var grown = new List<string>(ids);
            grown.AddRange(Enumerable.Range(400, 200).Select(i => $"2027-05/night{i}|ASI2600"));
            var grownTest = DatasetSplitWriter.SelectTestSessions(grown, 0.2).ToHashSet();
            foreach (var id in ids)
            {
                grownTest.Contains(id).ShouldBe(a.Contains(id), $"session {id} changed split membership after growth");
            }

            // A test session stays test; a train session stays train (the pinning contract).
            foreach (var id in a)
            {
                DatasetSplitWriter.IsTestSession(id, 0.2).ShouldBeTrue();
            }

            // File round-trips the sorted test ids (skipping comment lines).
            var path = Path.Combine(_dir, DatasetSplitWriter.TestSessionsFileName);
            var written = await DatasetSplitWriter.WriteAsync(ids, 0.2, path, TestContext.Current.CancellationToken);
            written.ShouldBe(a);
            var fileIds = File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
            fileIds.ShouldBe(a.ToArray());
            output.WriteLine($"test sessions: {a.Length}/{ids.Count}");
        }

        [Fact]
        public async Task Parity_StoredTilesEqualReStretchedSource()
        {
            var ct = TestContext.Current.CancellationToken;
            var registered = await DatasetSyntheticFixtures.RegisterAsync(_dir, ct);
            var outDir = Path.Combine(_dir, "out");

            var export = await DatasetTileExporter.ExportAsync(
                registered, outDir, tileSize: 64, cellsPerSession: 20, subsPerCell: 3, cancellationToken: ct);
            export.Rows.Length.ShouldBeGreaterThan(0);

            // Re-derive a sample of tiles from source through the same stretch and diff against the
            // stored bytes. Zero-skew => byte-identical => max diff exactly 0.
            var parity = await DatasetTileExporter.VerifyParityAsync(
                registered, outDir, export.Rows, sampleCount: 12, cancellationToken: ct);

            parity.Checked.ShouldBeGreaterThan(0);
            parity.MaxAbsDiff.ShouldBe(0.0, "stored tiles must equal the C# stretch of the source (zero train/inference skew)");
            output.WriteLine($"parity checked={parity.Checked} maxDiff={parity.MaxAbsDiff}");
        }

        private const double Fraction = 0.15;

        /// <summary>The first <c>night|Cam|Target</c> id whose own hash says <paramref name="wasTest"/> and whose
        /// renamed id (with <c>|Filter</c>) says the opposite: the case a rename would move.</summary>
        private static (string Old, string Renamed) FlippingId(bool wasTest, string filter = "Optolong L-eNhance")
        {
            for (var i = 0; ; i++)
            {
                var old = $"Cam/Slug/Target/2024-01-{i:D4}|Cam|Target";
                var renamed = old + "|" + filter;
                if (DatasetSplitWriter.IsTestSession(old, Fraction) == wasTest && DatasetSplitWriter.IsTestSession(renamed, Fraction) != wasTest)
                {
                    return (old, renamed);
                }
            }
        }

        [Fact]
        public void ASessionThatGainsAFilterKeepsTheSetItHadBothWays()
        {
            var (testOld, testRenamed) = FlippingId(wasTest: true);
            var (trainOld, trainRenamed) = FlippingId(wasTest: false);
            var prior = new DatasetSplitWriter.PriorSplit([testOld, trainOld], [testOld]);

            // Held out before the FILTER card was written, held out after: no held-out sky reaches training.
            DatasetSplitWriter.IsTestSession(testRenamed, Fraction, alwaysHeldOut: null, prior).ShouldBeTrue();
            // And a training session does not drift into the held-out set either.
            DatasetSplitWriter.IsTestSession(trainRenamed, Fraction, alwaysHeldOut: null, prior).ShouldBeFalse();
            prior.MatchedByLegacyId(testRenamed).ShouldBeTrue();
            prior.MatchedByLegacyId(testOld).ShouldBeFalse();
            // Without the prior both would have moved, which is what the prior is for.
            DatasetSplitWriter.IsTestSession(testRenamed, Fraction).ShouldBeFalse();
            DatasetSplitWriter.IsTestSession(trainRenamed, Fraction).ShouldBeTrue();
        }

        [Fact]
        public void ASessionThePriorNeverSawStillTakesItsHash()
        {
            var (testOld, _) = FlippingId(wasTest: true);
            var prior = new DatasetSplitWriter.PriorSplit([testOld], [testOld]);
            foreach (var id in Enumerable.Range(0, 50).Select(i => $"New/Session/{i}|Cam|Field|Ha"))
            {
                DatasetSplitWriter.IsTestSession(id, Fraction, alwaysHeldOut: null, prior).ShouldBe(DatasetSplitWriter.IsTestSession(id, Fraction));
            }
        }

        [Fact]
        public void AFlipSideAndAForcedIdFollowTheirNightThroughTheRename()
        {
            var (testOld, testRenamed) = FlippingId(wasTest: true);
            var prior = new DatasetSplitWriter.PriorSplit([testOld], [testOld]);
            DatasetSplitWriter.IsTestSession(testRenamed + "|flip=a", Fraction, alwaysHeldOut: null, prior).ShouldBeTrue();

            var (trainOld, trainRenamed) = FlippingId(wasTest: false);
            var forced = new HashSet<string>(StringComparer.Ordinal) { trainOld };
            DatasetSplitWriter.IsForced(trainRenamed, forced).ShouldBeTrue();
            DatasetSplitWriter.IsForced(trainRenamed + "|flip=b", forced).ShouldBeTrue();
            DatasetSplitWriter.IsTestSession(trainRenamed, Fraction, forced, prior: null).ShouldBeTrue();
        }

        [Fact]
        public void TheFilterComponentIsTheFourthPartOfAGroupId()
        {
            DatasetSplitWriter.WithoutFilter("d/n|Cam|Target|Ha").ShouldBe("d/n|Cam|Target");
            DatasetSplitWriter.WithoutFilter("d/n|Cam||Ha").ShouldBe("d/n|Cam");
            DatasetSplitWriter.WithoutFilter("d/n|Cam|Target").ShouldBe("d/n|Cam|Target");
            DatasetSplitWriter.WithoutFilter("d/n|Cam").ShouldBe("d/n|Cam");
        }

        [Fact]
        public async Task ThePriorIsTheStoresLedgerAndPinnedFileAndTheWrittenSplitKeepsIt()
        {
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(Path.Combine(_dir, "stats"));
            var (testOld, testRenamed) = FlippingId(wasTest: true);
            var (trainOld, trainRenamed) = FlippingId(wasTest: false);
            var ledgerPath = Path.Combine(_dir, "stats", DatasetSessionLedger.FileName);
            foreach (var id in new[] { testOld, trainOld })
            {
                await DatasetSessionLedger.AppendAsync(ledgerPath, new DatasetSessionLedger.SessionLedgerEntry(id, "x", 3, DateTimeOffset.UnixEpoch, 0, "tiles/x"), ct);
            }
            var splitPath = Path.Combine(_dir, DatasetSplitWriter.TestSessionsFileName);
            await DatasetSplitWriter.WriteAsync([testOld, trainOld], Fraction, splitPath, ct);

            var prior = await DatasetSplitWriter.PriorSplit.ReadAsync(_dir, cancellationToken: ct);
            prior.KnownCount.ShouldBe(2);
            var written = await DatasetSplitWriter.WriteAsync([testRenamed, trainRenamed], Fraction, splitPath, alwaysHeldOut: null, prior, ct);

            written.ShouldBe([testRenamed]);
            DatasetSplitWriter.ReadPinned(splitPath).ShouldBe([testRenamed]);
        }
    }
}
