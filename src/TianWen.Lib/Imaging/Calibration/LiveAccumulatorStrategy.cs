using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Frame-at-a-time online accumulator (Welford mean + variance) for live
/// stacking during a capture session. PLAN-stacking Phase 14
/// <c>LiveStacker</c>. Only selectable when the probe sets
/// <see cref="IntegrationProbe.LiveStacking"/> -- batch strategies expose
/// equivalent fidelity at lower complexity when all frames are available
/// up front.
/// </summary>
/// <remarks>
/// Welford state is one (mu, m2, n) triple per output pixel × channel; for
/// a 3008^2 RGB canvas that's ~108 MB of mu plus 108 MB of m2 plus a single
/// int32 frame count. No N-frame column stored, ever; every Accept(frame)
/// updates the running estimate in O(width × height × channels). Rejection
/// is mu-sigma clip with kappa=3 after a 24-frame bootstrap; outliers are
/// replaced by mu, not skipped, so the variance estimate stays valid.
/// </remarks>
public sealed class LiveAccumulatorStrategy : IIntegrationStrategy
{
    private readonly IntegrationCostModel _costs;

    public LiveAccumulatorStrategy(IntegrationCostModel? costs = null)
    {
        _costs = costs ?? new IntegrationCostModel();
    }

    public IntegrationStrategyKind Kind => IntegrationStrategyKind.LiveAccumulator;

    /// <summary>Online Welford with bootstrap mu-sigma; rejection is
    /// strictly weaker than offline per-pixel-column rejection across all
    /// N frames, but matches what's possible without buffering every
    /// frame.</summary>
    public double FidelityScore => 0.85;

    public bool SupportsLiveStacking => true;

    public StrategyFit Evaluate(IntegrationProbe probe, ResourceBudget budget)
    {
        // Welford state: mu (canvas) + m2 (canvas) + scratch (canvas) = 3x canvas.
        var ram = probe.CanvasBytes * 3 + probe.OutputRamBytes;
        var cap = budget.AllowedRam(probe);

        if (!probe.LiveStacking)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: "live stacking not requested -- batch strategies are higher fidelity");
        }

        if (ram > cap)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: $"needs {Format.GB(ram)} RAM for Welford state, cap {Format.GB(cap)}");
        }

        // Per-frame: decode + debayer + warp + single Welford update pass.
        // Stack cost reduces to one column-step (1 mul-add) per pixel per
        // frame -- the rejector + combiner of batch strategies collapses to
        // an online update.
        var eta = _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe);

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: 0,
            EstimatedDuration: eta,
            Rationale: $"Welford state {Format.GB(ram)} / {Format.GB(cap)}, per-frame online");
    }

    public ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct) =>
        throw new NotImplementedException(
            "LiveAccumulatorStrategy is PLAN-stacking Phase 14 -- the Welford online accumulator class " +
            "doesn't exist yet. Wire it up alongside the session-loop live preview, then this strategy " +
            "just becomes the dispatch adapter.");
}
