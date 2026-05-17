using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class IntegrationStrategySelectorTests
{
    // Hand-crafted probes that target each branch of the picker. Bytes/RAM
    // are chosen so the *ranking*, not just CanRun, exercises the right path.

    private static IntegrationProbe SmallGroup(long ramBytes = 32L * 1024 * 1024 * 1024) => new(
        FrameCount: 13,
        FrameWidth: 3008, FrameHeight: 3008, ChannelCount: 3,
        CanvasWidth: 3024, CanvasHeight: 3015,
        AvailableRamBytes: ramBytes,
        AvailableDiskBytes: 500L * 1024 * 1024 * 1024,
        StagingDir: "C:/tmp",
        StagingDiskKind: DiskKind.Ssd);

    private static IntegrationProbe BigGroup(long ramBytes = 8L * 1024 * 1024 * 1024, long diskBytes = 500L * 1024 * 1024 * 1024, DiskKind disk = DiskKind.Ssd) => new(
        FrameCount: 244,
        FrameWidth: 3008, FrameHeight: 3008, ChannelCount: 3,
        CanvasWidth: 3024, CanvasHeight: 3015,
        AvailableRamBytes: ramBytes,
        AvailableDiskBytes: diskBytes,
        StagingDir: "C:/tmp",
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
