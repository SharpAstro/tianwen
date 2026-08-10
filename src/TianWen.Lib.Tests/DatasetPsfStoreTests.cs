using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The PSF store is what makes the archive PSF/noise report survive a partial or resumed run.
    /// Before it, the report was in-memory derived state overwritten at the end of every run, so a
    /// resume rewrote a 50-session report from the one session it happened to register and the rest
    /// was gone; unrecoverably, because the field-radius profile is measured on the session master,
    /// which lives in scratch wiped per session. These pin the properties that guarantee is built on.
    /// </summary>
    [Collection("Imaging")]
    public sealed class DatasetPsfStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "psf-store-" + Guid.NewGuid().ToString("N"));

        public DatasetPsfStoreTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }

        private static DatasetPsfNoiseReport.SessionPsf Record(
            string id, string train, float fwhm, double noise, int starsPerBin = 3, int bins = 5)
        {
            var samples = new DatasetPsfNoiseReport.RadiusSamples[bins];
            for (var b = 0; b < bins; b++)
            {
                var f = new float[starsPerBin];
                var e = new float[starsPerBin];
                for (var s = 0; s < starsPerBin; s++)
                {
                    // Widen with field radius, the profile's whole reason for existing.
                    f[s] = fwhm + b * 0.5f + s * 0.01f;
                    e[s] = 0.3f + b * 0.05f;
                }
                samples[b] = new DatasetPsfNoiseReport.RadiusSamples(f, e);
            }
            return new DatasetPsfNoiseReport.SessionPsf(
                SessionId: id, OpticalTrain: train,
                SubFwhm: [fwhm, fwhm + 0.1f, fwhm + 0.2f],
                SubHfd: [fwhm - 0.2f, fwhm - 0.1f, fwhm],
                SubEllipticity: [0.5f, 0.52f, 0.54f],
                MasterNoiseRelative: noise,
                Bins: samples);
        }

        [Fact]
        public async Task RoundTrip_PreservesEverySampleTheReportNeeds()
        {
            var ct = TestContext.Current.CancellationToken;
            var path = Path.Combine(_dir, DatasetPsfStore.FileName);
            var written = Record("2026-01-01|ASI533|M42", "ZWO ASI533MC Pro / Samyang @ 135mm", 2.5f, 0.004);

            await DatasetPsfStore.AppendAsync(path, written, ct);
            var read = await DatasetPsfStore.ReadAsync(path, cancellationToken: ct);

            read.Count.ShouldBe(1);
            var back = read[written.SessionId];
            back.OpticalTrain.ShouldBe(written.OpticalTrain);
            back.MasterNoiseRelative.ShouldBe(written.MasterNoiseRelative);
            back.SubFwhm.ShouldBe(written.SubFwhm);
            back.SubHfd.ShouldBe(written.SubHfd);
            back.SubEllipticity.ShouldBe(written.SubEllipticity);
            back.Bins.Length.ShouldBe(written.Bins.Length);
            for (var b = 0; b < written.Bins.Length; b++)
            {
                back.Bins[b].Fwhm.ShouldBe(written.Bins[b].Fwhm);
                back.Bins[b].Ellipticity.ShouldBe(written.Bins[b].Ellipticity);
            }
        }

        [Fact]
        public async Task ReMeasuring_AppendsAndLastWins_WithoutErasingTheEarlierLine()
        {
            var ct = TestContext.Current.CancellationToken;
            var path = Path.Combine(_dir, DatasetPsfStore.FileName);

            await DatasetPsfStore.AppendAsync(path, Record("s1", "train A", 3.0f, 0.009), ct);
            await DatasetPsfStore.AppendAsync(path, Record("s1", "train A", 2.0f, 0.001), ct);

            // Both lines are still on disk (nothing is erased, so a worse re-run stays comparable
            // against what it replaced) but the reader takes the last.
            File.ReadAllLines(path).Where(l => l.Trim().Length > 0).Count().ShouldBe(2);
            var read = await DatasetPsfStore.ReadAsync(path, cancellationToken: ct);
            read.Count.ShouldBe(1);
            read["s1"].SubFwhm[0].ShouldBe(2.0f);
        }

        [Fact]
        public async Task TornTail_FromAKilledRun_IsSkippedOnReadAndHealedOnTheNextAppend()
        {
            var ct = TestContext.Current.CancellationToken;
            var path = Path.Combine(_dir, DatasetPsfStore.FileName);

            await DatasetPsfStore.AppendAsync(path, Record("s1", "train A", 2.5f, 0.004), ct);
            // A process killed mid-append leaves a partial final line.
            await File.AppendAllTextAsync(path, "{\"SessionId\":\"s2\",\"Optic", ct);

            // Readable in the torn state: the complete record survives, the torn one is skipped.
            var duringTear = await DatasetPsfStore.ReadAsync(path, cancellationToken: ct);
            duringTear.Keys.ShouldBe(["s1"]);

            // The next append heals it, so the torn line can never end up buried mid-file where a
            // downstream JSONL consumer would choke on it.
            await DatasetPsfStore.AppendAsync(path, Record("s3", "train A", 2.7f, 0.005), ct);
            var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToArray();
            lines.Length.ShouldBe(2);
            var healed = await DatasetPsfStore.ReadAsync(path, cancellationToken: ct);
            healed.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(["s1", "s3"]);
        }

        [Fact]
        public async Task Missing_File_ReadsEmpty_SoAFirstRunDegradesCleanly()
        {
            var ct = TestContext.Current.CancellationToken;
            var read = await DatasetPsfStore.ReadAsync(Path.Combine(_dir, "nope.jsonl"), cancellationToken: ct);
            read.ShouldBeEmpty();
        }

        [Fact]
        public async Task StoredRecords_RebuildTheSameReportAsMeasuringThemInOneRun()
        {
            // The store is only worth having if a report rebuilt from it equals the one an
            // uninterrupted run would have produced. Compared as the RENDERED markdown, because that
            // is the artifact, and because Report holds ImmutableArray<TrainReport>, whose equality is
            // by underlying-array reference: a record-equality assert here would pass or fail for
            // reasons unrelated to the numbers.
            var ct = TestContext.Current.CancellationToken;
            var records = new List<DatasetPsfNoiseReport.SessionPsf>
            {
                Record("s1", "train A", 2.5f, 0.004),
                Record("s2", "train A", 3.0f, 0.006),
                Record("s3", "train B", 2.0f, 0.002),
            };

            // "One run": every session measured in sequence into one accumulator.
            var oneRun = new DatasetPsfNoiseReport.Accumulator();
            foreach (var r in records)
            {
                oneRun.Add(r);
            }

            // "Resumed": the store read back in a different order, which is what actually happens
            // when sessions are measured across several runs.
            var path = Path.Combine(_dir, DatasetPsfStore.FileName);
            foreach (var r in records.AsEnumerable().Reverse())
            {
                await DatasetPsfStore.AppendAsync(path, r, ct);
            }
            var fromStore = new DatasetPsfNoiseReport.Accumulator();
            var stored = await DatasetPsfStore.ReadAsync(path, cancellationToken: ct);
            foreach (var id in stored.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                fromStore.Add(stored[id]);
            }

            var built = oneRun.Build();
            built.Sessions.ShouldBe(3);
            built.Trains.Length.ShouldBe(2); // the per-train split survives the round trip

            var expectedPath = Path.Combine(_dir, "one-run.md");
            var actualPath = Path.Combine(_dir, "from-store.md");
            await DatasetPsfNoiseReport.WriteMarkdownAsync(built, expectedPath, ct);
            await DatasetPsfNoiseReport.WriteMarkdownAsync(fromStore.Build(), actualPath, ct);
            (await File.ReadAllTextAsync(actualPath, ct)).ShouldBe(await File.ReadAllTextAsync(expectedPath, ct));
        }

        [Fact]
        public void A_BinCountMismatch_IsRefusedLoudly_NotFoldedInWrong()
        {
            // Only reachable if the radius-bin count changed between runs. Dropping the session is
            // the lesser evil versus binning 3 bins' samples into 5, but it must not be silent, so
            // the accumulator warns and the session count proves it was not folded.
            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 5);
            acc.Add(Record("s1", "train A", 2.5f, 0.004, bins: 3));
            acc.Build().Sessions.ShouldBe(0);

            acc.Add(Record("s2", "train A", 2.5f, 0.004, bins: 5));
            acc.Build().Sessions.ShouldBe(1);
        }
    }
}
