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
}
