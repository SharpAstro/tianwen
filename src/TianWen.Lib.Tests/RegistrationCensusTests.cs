using Shouldly;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The census exists to separate the two opposite causes of a registration wipe-out, so the tests
/// are one per cause plus the trend, and they use the numbers from the session that was misdiagnosed.
/// </summary>
public class RegistrationCensusTests
{
    /// <summary>
    /// Modelled on "Segaull+Thors_Helmet" / HIP 42861, whose 49 subs were dropped and written up as
    /// "genuinely too star-poor to register". Reconstructed from the Debug log, its real spread was
    /// stars 44/70/97 and quads 26/46/72 against a reference holding 58 quads, which is the PURITY
    /// case: both sides have plenty, none correspond. The census has to make that unmistakable.
    /// </summary>
    [Fact]
    public void TheMisdiagnosedSessionReadsAsHealthyCountsNotAStarPoorField()
    {
        var (stars, quads, hfd, ecc) = DegradingSession();

        var spread = RegistrationCensus.Measure(stars, quads, hfd, ecc);
        var line = RegistrationCensus.Describe(spread);

        spread.ShouldNotBeNull();
        spread.Subs.ShouldBe(48);
        spread.StarsMin.ShouldBe(44);
        spread.StarsMax.ShouldBe(97);
        // The whole point: every frame is far above the matching floor, so "star-poor" is refuted by
        // the numbers themselves rather than by re-parsing a Debug log weeks later.
        spread.StarHistogram[0].ShouldBe(0, "no frame belongs in the 0-24 bucket");
        spread.QuadsMin.ShouldBe(26);
        spread.QuadsMax.ShouldBe(72);
        spread.StarTrend.ShouldNotBeNull();
        spread.StarTrend.Value.ShouldBeLessThan(-0.3);

        line.ShouldContain("48 subs");
        line.ShouldContain("stars 44/");
        line.ShouldContain("/97");
        line.ShouldContain("quads 26/");
        line.ShouldContain("hfd 1.88/");
        line.ShouldContain("DEGRADING through the session");
    }

    /// <summary>The opposite cause: a genuinely sparse field, where the counts sit in the low buckets.</summary>
    [Fact]
    public void AGenuinelySparseFieldPutsEveryFrameInTheLowBuckets()
    {
        var stars = new List<int> { 9, 11, 8, 12, 10, 9 };
        var quads = new List<int> { 1, 2, 0, 2, 1, 1 };
        var hfd = new List<float> { 2.1f, 2.0f, 2.2f, 2.1f, 2.0f, 2.1f };
        var ecc = new List<float> { 0.3f, 0.3f, 0.31f, 0.3f, 0.29f, 0.3f };

        var spread = RegistrationCensus.Measure(stars, quads, hfd, ecc);
        var line = RegistrationCensus.Describe(spread);

        spread.ShouldNotBeNull();
        spread.StarHistogram[0].ShouldBe(6, "all six frames fall in the 0-24 bucket");
        line.ShouldContain("stars 8/");
        line.ShouldContain("0-24:6");
        // Flat, so no trend claim: a sparse field is not a degrading one and must not read as one.
        line.ShouldNotContain("DEGRADING");
    }

    /// <summary>
    /// A perfectly steady session has an UNDEFINED correlation, not a zero one. Reporting r=+0.00
    /// there would be a divide-by-zero dressed up as a measurement.
    /// </summary>
    [Fact]
    public void AConstantStarCountReportsNoTrendAtAllRatherThanZero()
    {
        var spread = RegistrationCensus.Measure(
            [500, 500, 500, 500, 500], [90, 90, 90, 90, 90],
            [3f, 3f, 3f, 3f, 3f], [0.2f, 0.2f, 0.2f, 0.2f, 0.2f]);

        spread.ShouldNotBeNull();
        spread.StarTrend.ShouldBeNull();
        RegistrationCensus.Describe(spread).ShouldNotContain("capture order");
    }

    [Fact]
    public void NoSurvivorsMeasuresToNothingAndSaysSo()
    {
        RegistrationCensus.Measure([], [], [], []).ShouldBeNull();
        RegistrationCensus.Describe(null).ShouldBe("no survivors to census");
    }

    /// <summary>
    /// Frames dropped at the star floor never reach quad-forming, so the quad list is shorter than
    /// the star list. The renderer must not assume they are parallel, and must SAY that the quad
    /// figures cover a subset, or they read as covering the session.
    /// </summary>
    [Fact]
    public void AShorterQuadListIsReportedAsCoveringOnlyItsOwnFrames()
    {
        var spread = RegistrationCensus.Measure(
            [12, 400, 420, 410], [80, 84, 82],
            [5f, 3f, 3.1f, 3f], [0.6f, 0.2f, 0.21f, 0.2f]);
        var line = RegistrationCensus.Describe(spread);

        spread.ShouldNotBeNull();
        spread.Subs.ShouldBe(4);
        spread.QuadFrames.ShouldBe(3);
        line.ShouldContain("4 subs");
        line.ShouldContain("quads 80/82/84");
        line.ShouldContain("over 3 of 4");
    }

    /// <summary>
    /// The store is the regression fixture's whole point: the skipped sessions stay in the source
    /// set, so one bake's numbers have to survive to be diffed against the next one's.
    /// </summary>
    [Fact]
    public async Task ASkippedSessionRoundTripsThroughTheStoreWithItsCensusIntact()
    {
        var dir = Directory.CreateTempSubdirectory("tw-skipstore");
        try
        {
            var path = Path.Combine(dir.FullName, DatasetSkipStore.FileName);
            var (stars, quads, hfd, ecc) = DegradingSession();
            var record = new DatasetSkipStore.SkippedSession(
                SessionId: "2025-12-28/Segaull+Thors_Helmet|ZWO ASI533MC Pro|HIP 42861",
                Reason: "fewer-than-2-registered",
                Survivors: 49,
                Registered: 1,
                SkippedTooFewStars: 0,
                SkippedNoQuadFit: 48,
                ReferenceFile: "2025-12-29_04-28-01__-5.10_60.00s_0048.fits",
                ReferenceStars: 89,
                ReferenceQuads: 58,
                Census: RegistrationCensus.Measure(stars, quads, hfd, ecc));

            await DatasetSkipStore.RecordAsync(path, record, cancellationToken: TestContext.Current.CancellationToken);
            var read = await DatasetSkipStore.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken);

            read.Count.ShouldBe(1);
            var back = read[record.SessionId];
            back.Reason.ShouldBe("fewer-than-2-registered");
            back.SkippedNoQuadFit.ShouldBe(48);
            back.ReferenceQuads.ShouldBe(58);
            back.Census.ShouldNotBeNull();
            back.Census.StarsMin.ShouldBe(44);
            back.Census.StarsMax.ShouldBe(97);
            back.Census.StarTrend.ShouldNotBeNull();
            // The histogram is stored rather than re-derived, because a SHIFTED histogram with an
            // unchanged median is exactly the bake-to-bake signal this file exists to preserve.
            back.Census.StarHistogram.Length.ShouldBe(RegistrationCensus.StarEdges.Length);
            RegistrationCensus.Describe(back.Census).ShouldBe(RegistrationCensus.Describe(record.Census));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Last-wins by id, appended never rewritten, so a session that starts or stops failing
    /// leaves both states on disk for comparison.</summary>
    [Fact]
    public async Task ASecondRecordForOneSessionWinsWithoutErasingTheFirst()
    {
        var dir = Directory.CreateTempSubdirectory("tw-skipstore2");
        try
        {
            var path = Path.Combine(dir.FullName, DatasetSkipStore.FileName);
            DatasetSkipStore.SkippedSession Make(string reason, int noFit) =>
                new("session-a", reason, 49, 1, 0, noFit, "ref.fits", 89, 58, null);

            await DatasetSkipStore.RecordAsync(path, Make("fewer-than-2-registered", 48), cancellationToken: TestContext.Current.CancellationToken);
            await DatasetSkipStore.RecordAsync(path, Make("gate-kept-below-min-subs", 3), cancellationToken: TestContext.Current.CancellationToken);

            var read = await DatasetSkipStore.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            read["session-a"].Reason.ShouldBe("gate-kept-below-min-subs");
            (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Length.ShouldBe(2, "the earlier record stays readable");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>No store path configured is the normal case for tests and the stacking CLI, and must
    /// be a no-op rather than a throw.</summary>
    [Fact]
    public async Task NoStorePathWritesNothingAndDoesNotThrow()
    {
        await DatasetSkipStore.RecordAsync(null, new DatasetSkipStore.SkippedSession(
            "s", "r", 0, 0, 0, 0, null, 0, 0, null), cancellationToken: TestContext.Current.CancellationToken);
        await DatasetSkipStore.RecordAsync("", new DatasetSkipStore.SkippedSession(
            "s", "r", 0, 0, 0, 0, null, 0, 0, null), cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 48 subs declining through the night, matching the real session's measured endpoints
    /// (stars 44..97, quads 26..72, HFD opening up from 1.88 px).
    /// </summary>
    private static (List<int> Stars, List<int> Quads, List<float> Hfd, List<float> Ecc) DegradingSession()
    {
        var stars = new List<int>();
        var quads = new List<int>();
        var hfd = new List<float>();
        var ecc = new List<float>();
        for (var i = 0; i < 48; i++)
        {
            stars.Add(i == 47 ? 44 : 97 - i);
            quads.Add(72 - (i * 46 / 47));
            hfd.Add(1.88f + (i * 0.05f));
            ecc.Add(0.48f);
        }
        return (stars, quads, hfd, ecc);
    }
}
