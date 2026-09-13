using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
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
                BinsByChannel: [samples]);
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
            back.BinsByChannel.ShouldNotBeNull();
            written.BinsByChannel.ShouldNotBeNull();
            back.BinsByChannel.Length.ShouldBe(written.BinsByChannel.Length);
            back.BinsByChannel[0].Length.ShouldBe(written.BinsByChannel[0].Length);
            for (var b = 0; b < written.BinsByChannel[0].Length; b++)
            {
                back.BinsByChannel[0][b].Fwhm.ShouldBe(written.BinsByChannel[0][b].Fwhm);
                back.BinsByChannel[0][b].Ellipticity.ShouldBe(written.BinsByChannel[0][b].Ellipticity);
            }
        }

        /// <summary>
        /// The per-sub identity (file, epoch, computed and header air mass) travels aligned with the
        /// widths, NaN included, and a record from before it existed reads back with none rather than
        /// with an invented one. The alignment is the whole value of the columns: a width with no way
        /// to say which frame it belongs to cannot be split into a sharp and a soft manifest.
        /// </summary>
        [Fact]
        public async Task RoundTrip_KeepsThePerSubIdentityAlignedWithTheWidths()
        {
            var ct = TestContext.Current.CancellationToken;
            var path = Path.Combine(_dir, DatasetPsfStore.FileName);
            var plain = Record("2026-01-01|ASI533|M42", "ZWO ASI533MC Pro / Samyang @ 135mm", 2.5f, 0.004);
            DatasetPsfNoiseReport.SubIdentity.From(plain).ShouldBeNull("a record without the columns has no identity to carry");

            var epochs = new[]
            {
                new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 1, 12, 5, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 1, 12, 10, 0, TimeSpan.Zero),
            };
            var written = plain with
            {
                SubFile = ["D:/a/L_001.fits", "D:/a/L_002.fits", "D:/a/L_003.fits"],
                SubEpochUtc = epochs,
                SubAirmass = [1.05f, float.NaN, 1.31f],
                SubHeaderAirmass = [float.NaN, float.NaN, 1.30f],
                SubSelection = DatasetPsfNoiseReport.SubsRegistered,
                SubSiteFromFallback = true,
                SubFwhmGreen = [1.92f, float.NaN, 2.45f],
            };

            await DatasetPsfStore.AppendAsync(path, written, ct);
            var back = (await DatasetPsfStore.ReadAsync(path, cancellationToken: ct))[written.SessionId];

            back.SubFile.ShouldBe(written.SubFile);
            back.SubEpochUtc.ShouldBe(epochs);
            back.SubSelection.ShouldBe(DatasetPsfNoiseReport.SubsRegistered);
            back.SubSiteFromFallback.ShouldBe(true);
            plain.SubSiteFromFallback.ShouldBeNull("a record from before the column has no answer");
            back.SubFwhmGreen.ShouldNotBeNull();
            back.SubFwhmGreen[0].ShouldBe(1.92f);
            float.IsNaN(back.SubFwhmGreen[1]).ShouldBeTrue("a refused fit stays NaN through the file");
            plain.SubFwhmGreen.ShouldBeNull();
            DatasetPsfNoiseReport.SubIdentity.From(back).ShouldNotBeNull().UsedFallbackSite.ShouldBeTrue();
            back.SubAirmass.ShouldNotBeNull();
            back.SubAirmass.Length.ShouldBe(back.SubFwhm.Length);
            back.SubAirmass[0].ShouldBe(1.05f);
            float.IsNaN(back.SubAirmass[1]).ShouldBeTrue("an unknown air mass stays unknown through the file");
            back.SubAirmass[2].ShouldBe(1.31f);
            back.SubHeaderAirmass.ShouldNotBeNull();
            float.IsNaN(back.SubHeaderAirmass[0]).ShouldBeTrue();
            back.SubHeaderAirmass[2].ShouldBe(1.30f);

            var identity = DatasetPsfNoiseReport.SubIdentity.From(back);
            identity.ShouldNotBeNull();
            identity.File.ShouldBe(written.SubFile);
            identity.Selection.ShouldBe(DatasetPsfNoiseReport.SubsRegistered);
        }

        [Fact]
        public async Task RoundTrip_KeepsEachChannelsProfileSeparate_IncludingAnUnmeasurableOne()
        {
            var ct = TestContext.Current.CancellationToken;
            var path = Path.Combine(_dir, DatasetPsfStore.FileName);

            // Red wide, green narrow, blue unmeasurable: the real archive shape (green's profile is
            // ~35% narrower than red's, and blue is the channel that runs out of stars first). A
            // null in the middle of the array has to survive, or a session with one bad channel
            // would shift the others' indices and silently report green's PSF as blue's.
            var written = Record("2026-02-10|ASI533|HD 71526", "ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm", 2.9f, 0.004)
                with
            {
                MasterProfiles =
                [
                    new PsfProfileFit.Result(2.9097, 7.65, 0.1908, 1.9104, 400),
                    new PsfProfileFit.Result(1.9136, 7.10, 0.1053, 1.9981, 400),
                    null,
                ]
            };

            await DatasetPsfStore.AppendAsync(path, written, ct);
            var back = (await DatasetPsfStore.ReadAsync(path, cancellationToken: ct))[written.SessionId];

            back.MasterProfiles.ShouldNotBeNull();
            back.MasterProfiles.Length.ShouldBe(3);
            back.MasterProfiles[0].ShouldNotBeNull().Fwhm.ShouldBe(2.9097, 1e-6);
            back.MasterProfiles[0].ShouldNotBeNull().MoffatBeta.ShouldBe(7.65, 1e-6);
            back.MasterProfiles[1].ShouldNotBeNull().Fwhm.ShouldBe(1.9136, 1e-6);
            back.MasterProfiles[1].ShouldNotBeNull().StarsStacked.ShouldBe(400);
            back.MasterProfiles[2].ShouldBeNull();
        }

        [Fact]
        public async Task ReadingARecordFromBeforeProfilesWereMeasured_LeavesThemAbsentRatherThanFailing()
        {
            var ct = TestContext.Current.CancellationToken;
            var path = Path.Combine(_dir, DatasetPsfStore.FileName);

            // Every one of the 50 records in the live store was written before the profile existed,
            // so this is the ordinary read today, not an edge case. It must come back with no
            // profiles rather than throwing, since --force-psf is what fills them in.
            await DatasetPsfStore.AppendAsync(path, Record("s1", "train A", 3.0f, 0.009), ct);
            var back = (await DatasetPsfStore.ReadAsync(path, cancellationToken: ct))["s1"];

            (back.MasterProfiles is null || back.MasterProfiles.Length == 0).ShouldBeTrue();
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
        public void A_BinCountMismatch_DropsOnlyTheRadialSamples_NotTheWholeSession()
        {
            // Only reachable if the radius-bin count changed between runs, and binning 3 bins' samples
            // into 5 must never happen. It used to cost the whole session, which was heavier than
            // necessary: the per-sub metrics and the noise floor do not depend on the bin count at all,
            // so they are folded and only the mis-binned channel's samples are refused. Still loud
            // (the accumulator warns), and now countable: RadialSessions says how many sessions the
            // field-radius profile actually covers, so the loss is visible in the report rather than
            // showing up as a session that vanished.
            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 5);
            acc.Add(Record("s1", "train A", 2.5f, 0.004, bins: 3));

            var mismatched = acc.Build();
            mismatched.Sessions.ShouldBe(1, "the sub metrics and noise floor are bin-count independent");
            var trainA = mismatched.Trains.ShouldHaveSingleItem();
            trainA.RadialSessions.ShouldBe(0, "no channel could be binned");
            trainA.FieldRadiusProfiles.ShouldBeEmpty("mis-binned samples must not land in the wrong annuli");

            acc.Add(Record("s2", "train A", 2.5f, 0.004, bins: 5));
            var withGood = acc.Build();
            withGood.Sessions.ShouldBe(2);
            withGood.Trains.ShouldHaveSingleItem().RadialSessions.ShouldBe(1, "only the correctly binned one");
        }

        private static FrameInfo Light(float latitude, float longitude)
        {
            var meta = new ImageMeta(
                Instrument: "SVBONY SV605CC",
                ExposureStartTime: new DateTimeOffset(2025, 1, 14, 12, 5, 47, TimeSpan.Zero),
                ExposureDuration: TimeSpan.FromSeconds(60),
                FrameType: FrameType.Light,
                Telescope: "",
                PixelSizeX: 2.9f,
                PixelSizeY: 2.9f,
                FocalLength: 24,
                FocusPos: -1,
                Filter: Filter.None,
                BinX: 1,
                BinY: 1,
                CCDTemperature: -10f,
                SensorType: SensorType.RGGB,
                BayerOffsetX: 0,
                BayerOffsetY: 0,
                RowOrder: RowOrder.TopDown,
                Latitude: latitude,
                Longitude: longitude,
                Gain: 252,
                Offset: 20,
                TargetRA: 11.1833,
                TargetDec: -60.371);
            return new FrameInfo("frame_00001.fits", 100, 100, 1, BitDepth.Int16, meta);
        }

        /// <summary>SharpCap writes no site; the fallback fills it in where, and only where, the header
        /// has none, and the identity says that it did.</summary>
        [Fact]
        public void SubIdentity_UsesTheFallbackSiteOnlyWhereTheHeaderHasNone()
        {
            var melbourne = (LatitudeDeg: -37.877, LongitudeDeg: 145.1775);
            var noSite = new[] { Light(float.NaN, float.NaN) };
            var sited = new[] { Light(-37.877f, 145.1775f) };

            var bare = DatasetPsfNoiseReport.SubIdentity.From(noSite, DatasetPsfNoiseReport.SubsRegistered);
            float.IsNaN(bare.Airmass[0]).ShouldBeTrue("no site, no fallback, no air mass");
            bare.UsedFallbackSite.ShouldBeFalse();

            var filled = DatasetPsfNoiseReport.SubIdentity.From(noSite, DatasetPsfNoiseReport.SubsRegistered, melbourne);
            float.IsFinite(filled.Airmass[0]).ShouldBeTrue();
            filled.UsedFallbackSite.ShouldBeTrue();

            var fromHeader = DatasetPsfNoiseReport.SubIdentity.From(sited, DatasetPsfNoiseReport.SubsRegistered, (0.0, 0.0));
            fromHeader.UsedFallbackSite.ShouldBeFalse("a header site is never overridden");
            fromHeader.Airmass[0].ShouldBe(filled.Airmass[0], tolerance: 1e-4f);
        }
    }
}
