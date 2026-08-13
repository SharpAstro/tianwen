using Shouldly;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The timings exist to answer "where does a bake spend its time, per unit of work", so the tests are
/// about the ARITHMETIC and the DENOMINATOR rather than about clock values, which are not reproducible.
/// Everything here drives <see cref="StageTimings.Record"/> with a real (tiny) elapsed interval and
/// then asserts on the counters and the rendering.
/// </summary>
public class StageTimingsTests
{
    /// <summary>
    /// A stage recorded twice accumulates instead of appearing twice: the half-master pair records
    /// once per half and has to read as one stage, or its per-item cost is half of what it is.
    /// </summary>
    [Fact]
    public void RecordingOneStageTwiceAccumulatesRatherThanDuplicatingIt()
    {
        var t = new StageTimings();
        t.Record(StageNames.Halves, StageTimings.Start(), items: 25, pixels: 25_000);
        t.Record(StageNames.Halves, StageTimings.Start(), items: 24, pixels: 24_000);

        var stages = t.Snapshot();
        stages.Length.ShouldBe(1);
        stages[0].Name.ShouldBe(StageNames.Halves);
        stages[0].Items.ShouldBe(49);
        stages[0].Pixels.ShouldBe(49_000);
    }

    /// <summary>Stage order is first-record order, which is pipeline order for free -- so a table
    /// built from a snapshot reads top to bottom the way the run executed.</summary>
    [Fact]
    public void StagesKeepFirstRecordOrderEvenWhenAnEarlierOneIsToppedUpLater()
    {
        var t = new StageTimings();
        t.Record(StageNames.Measure, StageTimings.Start(), items: 10);
        t.Record(StageNames.Register, StageTimings.Start(), items: 10);
        t.Record(StageNames.Measure, StageTimings.Start(), items: 5);

        t.Snapshot().Select(s => s.Name).ShouldBe([StageNames.Measure, StageNames.Register]);
        t.Snapshot()[0].Items.ShouldBe(15);
    }

    /// <summary>
    /// The whole point of storing the volume beside the duration. A stage that repeats over TILES
    /// (export) and one that repeats over SUBS (integrate) both report a per-item cost that means
    /// what it says, which is what the log-derived reconstruction could not do: normalising export
    /// per input frame made a stage bound by creating small files read as a compute stage.
    /// </summary>
    [Fact]
    public void ThroughputUsesTheDenominatorTheStageActuallyRecorded()
    {
        var stage = new StageTimings.Stage("export", Seconds: 10.0, Items: 3300, Pixels: 3300L * 196_608);

        stage.MillisecondsPerItem.ShouldBe(10_000.0 / 3300, 0.001);
        stage.MegapixelsPerSecond.ShouldBe(3300L * 196_608 / 1e6 / 10.0, 0.001);
    }

    /// <summary>A stage with no items or no pixels must report 0, never divide by zero. The
    /// calibrate stage records time with no items by design, so this is a live case, not defensive
    /// padding.</summary>
    [Fact]
    public void AStageWithNoItemsOrNoPixelsReportsZeroRatherThanDividingByZero()
    {
        var noWork = new StageTimings.Stage("calibrate", Seconds: 2.5, Items: 0, Pixels: 0);
        noWork.MillisecondsPerItem.ShouldBe(0.0);
        noWork.MegapixelsPerSecond.ShouldBe(0.0);

        // Registration records items but deliberately no pixels: it reads star centroids only.
        var noPixels = new StageTimings.Stage(StageNames.Register, Seconds: 1.0, Items: 49, Pixels: 0);
        noPixels.MillisecondsPerItem.ShouldBe(1000.0 / 49, 0.001);
        noPixels.MegapixelsPerSecond.ShouldBe(0.0);

        // An instantaneous stage cannot produce an infinite rate.
        new StageTimings.Stage("x", Seconds: 0.0, Items: 5, Pixels: 5).MegapixelsPerSecond.ShouldBe(0.0);
    }

    /// <summary>The run-level roll-up sums by name across sessions and keeps first-seen order, so one
    /// table covers a whole bake.</summary>
    [Fact]
    public void MergeSumsByNameAcrossRunsAndKeepsFirstSeenOrder()
    {
        ImmutableArray<StageTimings.Stage> Session(double s, long items) =>
        [
            new StageTimings.Stage(StageNames.Measure, s, items, items * 9_000_000),
            new StageTimings.Stage(StageNames.Integrate, s * 2, items, items * 9_100_000),
        ];

        var merged = StageTimings.Merge([Session(10, 49), Session(20, 57)]);

        merged.Length.ShouldBe(2);
        merged[0].Name.ShouldBe(StageNames.Measure);
        merged[0].Seconds.ShouldBe(30.0, 0.001);
        merged[0].Items.ShouldBe(106);
        merged[1].Name.ShouldBe(StageNames.Integrate);
        merged[1].Seconds.ShouldBe(60.0, 0.001);
    }

    /// <summary>A stage that only some sessions ran (half-masters need a floor of subs) still lands
    /// in the roll-up, carrying only the sessions that ran it.</summary>
    [Fact]
    public void MergeAdmitsAStageOnlySomeSessionsRan()
    {
        var withHalves = StageTimings.Merge(
        [
            [new StageTimings.Stage(StageNames.Integrate, 10, 40, 0)],
            [
                new StageTimings.Stage(StageNames.Integrate, 10, 200, 0),
                new StageTimings.Stage(StageNames.Halves, 8, 200, 0),
            ],
        ]);

        withHalves.Length.ShouldBe(2);
        withHalves[1].Name.ShouldBe(StageNames.Halves);
        withHalves[1].Items.ShouldBe(200, "only the session that split into halves contributes");
    }

    [Fact]
    public void NothingTimedSaysSoRatherThanRenderingAnEmptyTable()
    {
        new StageTimings().Describe().ShouldBe("no stages timed");
        StageTimings.DescribeTable([]).ShouldBe("no stages timed");
    }

    /// <summary>
    /// The table is charged against the caller's OWN wall time, so the unaccounted row shows the gap
    /// between the stages and reality. A table that silently normalised to the sum of its own rows
    /// would always read 100% accounted and could never reveal that its boundaries had drifted.
    /// </summary>
    [Fact]
    public void TheTableReportsWhatTheStagesDoNotAccountFor()
    {
        var stages = ImmutableArray.Create(
            new StageTimings.Stage(StageNames.Measure, 60, 100, 900_000_000),
            new StageTimings.Stage(StageNames.Integrate, 120, 100, 910_000_000));

        var table = StageTimings.DescribeTable(stages, wallSeconds: 200);

        table.ShouldContain("unaccounted");
        table.ShouldContain("measure");
        table.ShouldContain("integrate");
        table.ShouldContain("TOTAL");
        // 20 s of 200 s is 10%.
        table.ShouldContain("10.0%");
    }

    /// <summary>The one-line form carries the per-item cost, and omits the throughput for a stage
    /// that recorded no pixels rather than printing a meaningless 0.0 Mpx/s beside it.</summary>
    [Fact]
    public void TheOneLineFormOmitsThroughputForAStageThatTouchedNoPixels()
    {
        var line = StageTimings.Describe(
        [
            new StageTimings.Stage(StageNames.Register, 1.2, 49, 0),
            new StageTimings.Stage(StageNames.Integrate, 30.0, 49, 450_000_000),
        ]);

        line.ShouldContain("register 1.2s 24ms/it");
        line.ShouldNotContain("register 1.2s 24ms/it 0.0Mpx/s");
        line.ShouldContain("integrate 30.0s 612ms/it 15.0Mpx/s");
        line.ShouldContain("31.2s total");
    }

    /// <summary>
    /// The store is what makes "did this bake get slower, and where" a diff instead of a log-parsing
    /// exercise, so a record has to survive the round trip with its stages intact.
    /// </summary>
    [Fact]
    public async Task ASessionTimingRoundTripsThroughTheStoreWithItsStagesIntact()
    {
        var dir = Directory.CreateTempSubdirectory("tw-timingstore");
        try
        {
            var path = Path.Combine(dir.FullName, DatasetTimingStore.FileName);
            var record = new DatasetTimingStore.SessionTiming(
                SessionId: "2026-02-14|ZWO ASI533MC Pro|Statue of Liberty Nebula",
                Camera: "ZWO ASI533MC Pro",
                Lights: 257,
                Registered: 255,
                CanvasWidth: 3054,
                CanvasHeight: 3035,
                MasterStrategy: nameof(IntegrationStrategyKind.BayerDrizzle),
                WallSeconds: 558.4,
                Stages:
                [
                    new StageTimings.Stage(StageNames.Measure, 96.2, 257, 257L * 9_048_064),
                    new StageTimings.Stage(StageNames.Register, 12.1, 257, 0),
                    new StageTimings.Stage("export", 152.3, 3300, 3300L * 196_608),
                ]);

            await DatasetTimingStore.RecordAsync(path, record, cancellationToken: TestContext.Current.CancellationToken);
            var read = await DatasetTimingStore.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken);

            read.Count.ShouldBe(1);
            var back = read[record.SessionId];
            back.Registered.ShouldBe(255);
            back.MasterStrategy.ShouldBe(nameof(IntegrationStrategyKind.BayerDrizzle));
            back.WallSeconds.ShouldBe(558.4, 0.001);
            // Field by field, not record equality: an ImmutableArray in a record compares by
            // REFERENCE, so ShouldBe on the whole record would pass or fail for reasons unrelated to
            // the contents.
            back.Stages.Length.ShouldBe(3);
            back.Stages[0].Name.ShouldBe(StageNames.Measure);
            back.Stages[0].Items.ShouldBe(257);
            back.Stages[2].Pixels.ShouldBe(3300L * 196_608);
            StageTimings.Describe(back.Stages).ShouldBe(StageTimings.Describe(record.Stages));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Last-wins by id, appended never rewritten, like the other two stores: a re-bake's
    /// timing must not erase the one it is being compared against.</summary>
    [Fact]
    public async Task ARebakesTimingWinsWithoutErasingThePriorOne()
    {
        var dir = Directory.CreateTempSubdirectory("tw-timingstore2");
        try
        {
            var path = Path.Combine(dir.FullName, DatasetTimingStore.FileName);
            DatasetTimingStore.SessionTiming Make(double wall) =>
                new("session-a", "cam", 49, 49, 3040, 3030, "BayerDrizzle", wall,
                    [new StageTimings.Stage(StageNames.Integrate, wall / 2, 49, 0)]);

            await DatasetTimingStore.RecordAsync(path, Make(120), cancellationToken: TestContext.Current.CancellationToken);
            await DatasetTimingStore.RecordAsync(path, Make(240), cancellationToken: TestContext.Current.CancellationToken);

            var read = await DatasetTimingStore.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            read["session-a"].WallSeconds.ShouldBe(240.0, 0.001);
            (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Length
                .ShouldBe(2, "the earlier timing stays readable, which is the whole point of comparing bakes");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>No store path configured is the normal case for tests and the stacking CLI: a no-op,
    /// never a throw. A diagnostic must not be able to take down the run it describes.</summary>
    [Fact]
    public async Task NoStorePathWritesNothingAndDoesNotThrow()
    {
        var record = new DatasetTimingStore.SessionTiming("s", "c", 1, 1, 1, 1, "x", 1.0, []);
        await DatasetTimingStore.RecordAsync(null, record, cancellationToken: TestContext.Current.CancellationToken);
        await DatasetTimingStore.RecordAsync("", record, cancellationToken: TestContext.Current.CancellationToken);
    }
}
