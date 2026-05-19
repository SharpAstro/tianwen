using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class IntegrationStrategySelectorTests
{
    // Hand-crafted probes that target each branch of the picker. Bytes/RAM
    // are chosen so the *ranking*, not just CanRun, exercises the right path.

    private static IntegrationProbe SmallGroup(long ramBytes = 32L * 1024 * 1024 * 1024, SensorType sensor = SensorType.Monochrome) => new(
        FrameCount: 13,
        FrameWidth: 3008, FrameHeight: 3008, ChannelCount: 3,
        CanvasWidth: 3024, CanvasHeight: 3015,
        AvailableRamBytes: ramBytes,
        AvailableDiskBytes: 500L * 1024 * 1024 * 1024,
        StagingDir: "C:/tmp",
        SensorType: sensor,
        StagingDiskKind: DiskKind.Ssd);

    private static IntegrationProbe BigGroup(long ramBytes = 8L * 1024 * 1024 * 1024, long diskBytes = 500L * 1024 * 1024 * 1024, DiskKind disk = DiskKind.Ssd, SensorType sensor = SensorType.Monochrome) => new(
        FrameCount: 244,
        FrameWidth: 3008, FrameHeight: 3008, ChannelCount: 3,
        CanvasWidth: 3024, CanvasHeight: 3015,
        AvailableRamBytes: ramBytes,
        AvailableDiskBytes: diskBytes,
        StagingDir: "C:/tmp",
        SensorType: sensor,
        StagingDiskKind: disk);

    [Fact]
    public void SmallGroup_FitsInRam_PicksInRamAllFrames()
    {
        var probe = SmallGroup();

        var selection = IntegrationStrategySelector.Pick(probe);

        selection.Chosen.Kind.ShouldBe(IntegrationStrategyKind.InRamAllFrames);
        var inRam = selection.Considered.Single(c => c.Strategy.Kind == IntegrationStrategyKind.InRamAllFrames);
        inRam.Fit.CanRun.ShouldBeTrue(inRam.Fit.Rationale);
    }

    [Fact]
    public void BigGroup_TightRam_FallsThroughToStagedOrTile()
    {
        // 244 frames * 100 MB ~= 24 GB; 8 GB RAM cap rules out InRam.
        var probe = BigGroup(ramBytes: 8L * 1024 * 1024 * 1024);

        var selection = IntegrationStrategySelector.Pick(probe);

        selection.Chosen.Kind.ShouldNotBe(IntegrationStrategyKind.InRamAllFrames);
        var inRam = selection.Considered.Single(c => c.Strategy.Kind == IntegrationStrategyKind.InRamAllFrames);
        inRam.Fit.CanRun.ShouldBeFalse();
        inRam.Fit.Rationale.ShouldContain("RAM");
    }

    [Fact]
    public void BigGroup_RGGB_PicksBayerDrizzle()
    {
        // RGGB sensor + 244 frames (above the 60-frame coverage gate) +
        // 16 GB host: drizzle should auto-win. The standard path's per-
        // frame AHD + warp + reject-combine cost (~1000 ms / frame) is
        // ~3-5x what drizzle's load+calibrate + forward-project (~300 ms /
        // frame) needs; under Balanced policy that speed advantage wins
        // over the 0.92-vs-0.98 fidelity discount.
        var probe = BigGroup(ramBytes: 16L * 1024 * 1024 * 1024, sensor: SensorType.RGGB);

        var selection = IntegrationStrategySelector.Pick(probe);

        selection.Chosen.Kind.ShouldBeOneOf(
            IntegrationStrategyKind.BayerDrizzle,
            IntegrationStrategyKind.TilePipelinedDrizzle);
        var drizzleCandidates = selection.Considered
            .Where(c => c.Strategy.Kind is IntegrationStrategyKind.BayerDrizzle
                or IntegrationStrategyKind.TilePipelinedDrizzle)
            .ToArray();
        drizzleCandidates.Length.ShouldBe(2,
            "both drizzle variants should appear in the considered list on RGGB+N>=60");
        drizzleCandidates.ShouldAllBe(c => c.Fit.CanRun);
    }

    [Fact]
    public void SmallGroup_RGGB_GatesOutBayerDrizzleByFrameCount()
    {
        // RGGB sensor BUT only 13 frames -- below the 60-frame coverage
        // gate. The per-Bayer-position coverage (~25% per channel) leaves
        // big NaN gaps in R and B at this N, so drizzle stays gated even
        // though the sensor type matches.
        var probe = SmallGroup(sensor: SensorType.RGGB);

        var selection = IntegrationStrategySelector.Pick(probe);

        selection.Chosen.Kind.ShouldBe(IntegrationStrategyKind.InRamAllFrames);
        var drizzle = selection.Considered.Single(c => c.Strategy.Kind == IntegrationStrategyKind.BayerDrizzle);
        drizzle.Fit.CanRun.ShouldBeFalse();
        drizzle.Fit.Rationale.ShouldContain("matched frames");
    }

    [Fact]
    public void BigGroup_Monochrome_GatesOutBayerDrizzleBySensorType()
    {
        // 244 frames but mono sensor -- no Bayer matrix to dispatch from,
        // so drizzle is meaningless. The selector falls back to the
        // standard path (TilePipelined / FootprintStaged / etc.)
        // depending on memory budget.
        var probe = BigGroup(ramBytes: 16L * 1024 * 1024 * 1024, sensor: SensorType.Monochrome);

        var selection = IntegrationStrategySelector.Pick(probe);

        selection.Chosen.Kind.ShouldNotBe(IntegrationStrategyKind.BayerDrizzle);
        selection.Chosen.Kind.ShouldNotBe(IntegrationStrategyKind.TilePipelinedDrizzle);
        var drizzle = selection.Considered.Single(c => c.Strategy.Kind == IntegrationStrategyKind.BayerDrizzle);
        drizzle.Fit.CanRun.ShouldBeFalse();
        drizzle.Fit.Rationale.ShouldContain("RGGB");
    }

    [Fact]
    public void RGGB_BelowDefaultMinFrames_AutoPicksDrizzleWhenMinFramesLowered()
    {
        // 50 frames, RGGB sensor. Default DrizzleStrategy uses
        // AutoSelectMinFrameCount = 60, so the stock pool gates drizzle
        // out. When the pipeline constructs a custom pool with
        // minFrameCount=50 (as it would under --drizzle-min-frames 50),
        // drizzle becomes auto-pickable. This proves the override
        // threads through to the auto-pick path, not just the pre-gate.
        var probe = new IntegrationProbe(
            FrameCount: 50,
            FrameWidth: 3008, FrameHeight: 3008, ChannelCount: 3,
            CanvasWidth: 3024, CanvasHeight: 3015,
            AvailableRamBytes: 16L * 1024 * 1024 * 1024,
            AvailableDiskBytes: 500L * 1024 * 1024 * 1024,
            StagingDir: "C:/tmp",
            SensorType: SensorType.RGGB,
            StagingDiskKind: DiskKind.Ssd);

        // Default pool: drizzle gated by 60-frame minimum.
        var defaultPick = IntegrationStrategySelector.Pick(probe);
        var defaultDrizzle = defaultPick.Considered.Single(c => c.Strategy.Kind == IntegrationStrategyKind.BayerDrizzle);
        defaultDrizzle.Fit.CanRun.ShouldBeFalse(
            "default pool with minFrameCount=60 should gate out drizzle at N=50");
        defaultDrizzle.Fit.Rationale.ShouldContain("matched frames");

        // Custom pool: drizzle constructed with minFrameCount=50, matching
        // what the pipeline would build when --drizzle-min-frames 50 is
        // set. Both drizzle variants now accept N=50; selector picks one
        // of them under Balanced policy.
        var customPool = IntegrationStrategySelector.DefaultStrategies()
            .Select(s => s.Kind switch
            {
                IntegrationStrategyKind.BayerDrizzle => (IIntegrationStrategy)new DrizzleStrategy(minFrameCount: 50),
                IntegrationStrategyKind.TilePipelinedDrizzle => new TilePipelinedDrizzleStrategy(minFrameCount: 50),
                _ => s,
            })
            .ToArray();
        var customPick = IntegrationStrategySelector.Pick(probe, pool: customPool);
        var customDrizzle = customPick.Considered.Single(c => c.Strategy.Kind == IntegrationStrategyKind.BayerDrizzle);
        customDrizzle.Fit.CanRun.ShouldBeTrue(
            $"custom pool with minFrameCount=50 should accept N=50 (rationale: {customDrizzle.Fit.Rationale})");
        customPick.Chosen.Kind.ShouldBeOneOf(
            IntegrationStrategyKind.BayerDrizzle,
            IntegrationStrategyKind.TilePipelinedDrizzle);
    }

    [Fact]
    public void HddPenalisesStagedStrategiesInRanking()
    {
        // Same probe, two disk kinds: SSD vs HDD. Force a balanced policy so
        // speed actually matters. Tile-pipelined doesn't stage so it should
        // jump up in the ranking when disk is slow.
        var ssdProbe = BigGroup(disk: DiskKind.Ssd);
        var hddProbe = BigGroup(disk: DiskKind.Hdd);

        var ssdPick = IntegrationStrategySelector.Pick(ssdProbe, policy: RankingPolicy.Balanced);
        var hddPick = IntegrationStrategySelector.Pick(hddProbe, policy: RankingPolicy.Balanced);

        var ssdFootprint = ssdPick.Considered.Single(c => c.Strategy.Kind == IntegrationStrategyKind.FootprintStaged).Fit;
        var hddFootprint = hddPick.Considered.Single(c => c.Strategy.Kind == IntegrationStrategyKind.FootprintStaged).Fit;

        // HDD takes longer for staged strategies than SSD does -- by a wide
        // margin given the seek penalty multiplier.
        hddFootprint.EstimatedDuration.ShouldBeGreaterThan(ssdFootprint.EstimatedDuration);
    }

    [Fact]
    public void UserOverride_BeatsFidelityRanking()
    {
        var probe = SmallGroup();

        var selection = IntegrationStrategySelector.Pick(
            probe,
            preferred: IntegrationStrategyKind.Float16Staged);

        selection.Chosen.Kind.ShouldBe(IntegrationStrategyKind.Float16Staged);
        selection.Notes.ShouldContain("user override");
    }

    [Fact]
    public void UserOverride_OnFailingFit_LogsWarningButStillPicks()
    {
        // Disk too small for any staged strategy -- but user insisted.
        var probe = BigGroup(
            ramBytes: 8L * 1024 * 1024 * 1024,
            diskBytes: 1024 * 1024); // 1 MB

        var selection = IntegrationStrategySelector.Pick(
            probe,
            preferred: IntegrationStrategyKind.Float16Staged);

        selection.Chosen.Kind.ShouldBe(IntegrationStrategyKind.Float16Staged);
        selection.Notes.ShouldContain("CanRun=false");
    }

    [Fact]
    public void LiveStackingProbe_FiltersToLiveAccumulator()
    {
        var probe = SmallGroup() with { LiveStacking = true };

        var selection = IntegrationStrategySelector.Pick(probe);

        selection.Chosen.Kind.ShouldBe(IntegrationStrategyKind.LiveAccumulator);
        // Batch strategies should not even appear in the considered list when
        // live stacking is requested -- they're filtered out, not gated out.
        selection.Considered.ShouldAllBe(c => c.Strategy.SupportsLiveStacking);
    }

    [Fact]
    public void BatchProbe_FiltersOutLiveAccumulator()
    {
        var probe = SmallGroup();

        var selection = IntegrationStrategySelector.Pick(probe);

        selection.Considered.Select(c => c.Strategy.Kind)
            .ShouldNotContain(IntegrationStrategyKind.LiveAccumulator);
    }

    [Fact]
    public void NoStrategyFits_Throws()
    {
        // Pathological: 244 frames, 100 MB RAM, 100 MB disk.
        var probe = BigGroup(
            ramBytes: 100L * 1024 * 1024,
            diskBytes: 100L * 1024 * 1024);

        Should.Throw<InvalidOperationException>(() => IntegrationStrategySelector.Pick(probe));
    }

    [Fact]
    public void SpeedFirstPolicy_PrefersFasterSurvivor()
    {
        // Construct a scenario where multiple strategies fit. Speed-first
        // should pick the one with the lowest EstimatedDuration even if its
        // fidelity is lower.
        var probe = BigGroup(ramBytes: 64L * 1024 * 1024 * 1024); // plenty of RAM

        var fidelityFirst = IntegrationStrategySelector.Pick(probe, policy: RankingPolicy.FidelityFirst);
        var speedFirst = IntegrationStrategySelector.Pick(probe, policy: RankingPolicy.SpeedFirst);

        // FidelityFirst should pick the top-fidelity survivor (InRam, if it
        // fits at 64 GB). SpeedFirst should pick the shortest-eta survivor.
        var fastestEta = fidelityFirst.Considered
            .Where(c => c.Fit.CanRun)
            .Min(c => c.Fit.EstimatedDuration);

        speedFirst.Considered
            .Single(c => c.Strategy.Kind == speedFirst.Chosen.Kind).Fit
            .EstimatedDuration.ShouldBe(fastestEta);
    }

    [Fact]
    public void RankingPolicy_Score_BlendsLinearly()
    {
        // Direct unit-test of the score helper -- no probe needed.
        var balanced = RankingPolicy.Balanced;
        balanced.Score(fidelity: 1.0, normalizedSpeed: 0.0).ShouldBe(0.5, tolerance: 1e-9);
        balanced.Score(fidelity: 0.0, normalizedSpeed: 1.0).ShouldBe(0.5, tolerance: 1e-9);
        balanced.Score(fidelity: 1.0, normalizedSpeed: 1.0).ShouldBe(1.0, tolerance: 1e-9);
        balanced.Score(fidelity: 0.5, normalizedSpeed: 0.5).ShouldBe(0.5, tolerance: 1e-9);
    }

    [Fact]
    public void MemoryPressurePenalty_LowFreeRam_DragsScoreVsAbundantFreeRam()
    {
        // Same probe except for FreeRamBytes. Score of the RAM-heavy InRam
        // candidate should drop when free RAM is tight relative to its
        // estimate. The penalty is "a small nudge" by design -- not enough
        // to override a much-higher-fidelity strategy on a roomy host, just
        // enough to bias the ranker when two strategies are close.
        // Tight = 1.0 GB free, between InRam's ~1.6 GB estimate (penalised)
        // and Float16Staged's ~0.7 GB estimate (still under free, no penalty).
        var tightProbe = SmallGroup() with { FreeRamBytes = 1024L * 1024 * 1024 };
        var roomyProbe = SmallGroup() with { FreeRamBytes = 16L * 1024 * 1024 * 1024 };

        var tightSel = IntegrationStrategySelector.Pick(tightProbe);
        var roomySel = IntegrationStrategySelector.Pick(roomyProbe);

        var tightInRamScore = tightSel.Considered.Single(c => c.Strategy.Kind == IntegrationStrategyKind.InRamAllFrames).Score;
        var roomyInRamScore = roomySel.Considered.Single(c => c.Strategy.Kind == IntegrationStrategyKind.InRamAllFrames).Score;
        tightInRamScore.ShouldBeLessThan(roomyInRamScore,
            "InRam should score lower when free RAM is tight (memory-pressure penalty kicked in)");

        // The penalty should be meaningful but not catastrophic. Score drops
        // 5-50% depending on the over-commit ratio (capped at 50%).
        var dropFraction = (roomyInRamScore - tightInRamScore) / roomyInRamScore;
        dropFraction.ShouldBeGreaterThan(0.01, "score drop should be measurable");
        dropFraction.ShouldBeLessThan(0.5 + 1e-9, "penalty caps at 50%");
    }

    [Fact]
    public void MemoryPressurePenalty_DoesNotKickIn_WhenFreeRamPlentiful()
    {
        // 32 GB physical, 16 GB free -- InRam's 1.6 GB fits both. Soft penalty
        // is 0, so the default FidelityFirst policy still picks InRam.
        var probe = SmallGroup() with { FreeRamBytes = 16L * 1024 * 1024 * 1024 };

        var selection = IntegrationStrategySelector.Pick(probe);

        selection.Chosen.Kind.ShouldBe(IntegrationStrategyKind.InRamAllFrames);
    }

    [Fact]
    public void MemoryPressurePenalty_DefaultsToOff_WhenFreeRamUnpopulated()
    {
        // Probes built without FreeRamBytes (the parameterless default 0) get
        // no penalty applied. Tests + callers that pre-date the field keep
        // their pre-penalty behaviour.
        var probe = SmallGroup(); // FreeRamBytes defaults to 0

        var selection = IntegrationStrategySelector.Pick(probe);

        selection.Chosen.Kind.ShouldBe(IntegrationStrategyKind.InRamAllFrames);
    }

    // Sink-kind decision: canvas-vs-RAM ratio. SmallGroup's canvas
    // (3024*3015*3*4 = ~109 MB) on 32 GB RAM = 0.3% -> InRamArray.
    [Fact]
    public void Selection_SmallCanvasPlentyOfRam_PicksInRamSink()
    {
        var probe = SmallGroup();
        var selection = IntegrationStrategySelector.Pick(probe);
        selection.Sink.ShouldBe(SinkKind.InRamArray);
    }

    // BigGroup canvas on 200 MB RAM -> 109 MB / 200 MB = 54% > 25% preferred
    // threshold, so the selector flips to MMF. Strategy ranking is unaffected.
    [Fact]
    public void Selection_CanvasOverPreferredThreshold_FlipsToMmap()
    {
        // Tight RAM so canvas / availableRam exceeds MmapPreferredCanvasRamFraction.
        var probe = SmallGroup(ramBytes: 200L * 1024 * 1024);
        var selection = IntegrationStrategySelector.Pick(probe, preferred: IntegrationStrategyKind.FootprintStaged);
        selection.Sink.ShouldBe(SinkKind.MemoryMappedFits);
    }

    [Fact]
    public void PickSinkKind_AtMandatoryThreshold_ReturnsMmap()
    {
        // Canvas takes 90% of available RAM -> well above mandatory cutoff.
        var probe = SmallGroup() with
        {
            AvailableRamBytes = (long)(SmallGroup().CanvasBytes / 0.9),
        };
        IntegrationStrategySelector.PickSinkKind(probe).ShouldBe(SinkKind.MemoryMappedFits);
    }

    [Fact]
    public void PickSinkKind_ZeroAvailableRam_FallsBackToInRam()
    {
        // Synthesised probes (no Snapshot call) leave AvailableRamBytes at 0.
        // Decision rule must not divide by zero; defaults to InRamArray so
        // legacy tests aren't surprised.
        var probe = SmallGroup() with { AvailableRamBytes = 0 };
        IntegrationStrategySelector.PickSinkKind(probe).ShouldBe(SinkKind.InRamArray);
    }
}
