using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// PLAN-stacking Phase 8: re-read raw lights tile-by-tile, calibrate + warp +
/// normalize + reject + combine in memory per output tile. No staging on disk;
/// repeated raw reads are absorbed by the OS page cache when the raw lights
/// fit in free RAM, and otherwise still cost one full sequential pass per
/// integration (each tile pulls a small slice of each raw frame).
/// </summary>
/// <remarks>
/// <para>Fidelity sits fractionally below <see cref="InRamAllFramesStrategy"/>
/// (0.98 vs 1.00) only as a tiebreaker -- the math is identical, but per-tile
/// boundary halos sample raw pixels twice across tile borders, which can shift
/// the rejector's per-pixel column statistics by a hair.</para>
///
/// <para><b>Not yet runnable.</b> Phase 8 needs IntegrationJob v2 (raw light
/// paths + per-frame transforms + Calibrator), memory-mapped partial FITS
/// readers, sub-region debayer, and per-tile inverse-transform warp with
/// bilinear-sample halos. Until those primitives ship, <see cref="Evaluate"/>
/// returns <c>CanRun=false</c> so the selector falls through to a runnable
/// executor; the strategy still appears in the candidate table so the log
/// shows what the operating range would be once Phase 8 lands.</para>
/// </remarks>
public sealed class TilePipelinedStrategy : IIntegrationStrategy
{
    private readonly IntegrationCostModel _costs;

    public TilePipelinedStrategy(IntegrationCostModel? costs = null)
    {
        _costs = costs ?? new IntegrationCostModel();
    }

    public IntegrationStrategyKind Kind => IntegrationStrategyKind.TilePipelined;

    public double FidelityScore => 0.98;

    public bool SupportsLiveStacking => false;

    public StrategyFit Evaluate(IntegrationProbe probe, ResourceBudget budget)
    {
        // Project what RAM the eventual Phase 8 path would consume so the log
        // row carries a meaningful estimate even though we decline to run.
        var ramCap = budget.AllowedRam(probe) - probe.OutputRamBytes;
        var tile = ramCap > 0 ? IntegrationTileSizing.Side(probe, ramCap) : -1;
        var projectedRam = tile > 0
            ? IntegrationTileSizing.TileRamBytes(tile, probe) + probe.OutputRamBytes
            : probe.OutputRamBytes;
        var rawReadBytes = probe.FrameBytes * probe.FrameCount;
        var tileCount = tile > 0
            ? ((probe.CanvasWidth + tile - 1) / tile) * ((probe.CanvasHeight + tile - 1) / tile)
            : 1;
        var seeks = tileCount * probe.FrameCount;
        var io = _costs.DiskIo(rawReadBytes, seeks, probe.StagingDiskKind);
        var projectedEta = _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe) + io;

        var tileDesc = tile > 0 ? $"tile {tile} px" : "tile-sizing infeasible";
        return new StrategyFit(
            CanRun: false,
            EstimatedRamBytes: projectedRam,
            EstimatedDiskBytes: 0,
            EstimatedDuration: projectedEta,
            Rationale: $"Phase 8 not yet implemented ({tileDesc}, projected {Format.GB(projectedRam)} RAM)");
    }

    public ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct) =>
        throw new NotImplementedException(
            "TilePipelinedStrategy is the PLAN-stacking Phase 8 placeholder. Real implementation needs " +
            "IntegrationJob v2 with raw light paths + per-frame transforms + Calibrator, plus " +
            "memory-mapped partial FITS reads + sub-region debayer + per-tile inverse-transform warp " +
            "with bilinear-sample halos. Until those primitives ship the strategy declines to run " +
            "(Evaluate returns CanRun=false) so the selector falls through to a runnable executor.");
}
