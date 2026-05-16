using System;
using System.Collections.Generic;
using System.IO;
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
        // Phase 8.0: scaffolding-only. RunAsync loads + calibrates + debayers +
        // warps every raw frame in full and hands the result to the in-memory
        // Integrator. Memory profile is identical to InRamAllFrames (no
        // partial-FITS yet) so we report InRam-equivalent RAM cost. Phase 8.1
        // (memory-mapped partial FITS reader) + Phase 8.2 (sub-region debayer
        // + per-tile warp) will switch this to the tile-bounded RAM profile
        // and make the strategy genuinely cheaper than InRam.
        var ram = probe.AllFramesRamBytes + probe.OutputRamBytes;
        var cap = budget.AllowedRam(probe);
        var rawReadBytes = probe.FrameBytes * probe.FrameCount;
        var io = _costs.DiskIo(rawReadBytes, probe.FrameCount, probe.StagingDiskKind);
        var eta = _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe) + io;

        if (ram > cap)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: eta,
                Rationale: $"Phase 8.0 still N x debayered in RAM ({Format.GB(ram)} > cap {Format.GB(cap)}); Phase 8.1/8.2 will fix this");
        }

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: 0,
            EstimatedDuration: eta,
            Rationale: $"Phase 8.0 scaffolding (N x debayered in RAM, {Format.GB(ram)} / {Format.GB(cap)}); raw-tile pipeline pending Phase 8.1");
    }

    public async ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct)
    {
        if (job.RawLightSources is null || job.Calibrator is null)
        {
            throw new InvalidOperationException(
                "TilePipelinedStrategy requires job.RawLightSources + job.Calibrator. The orchestrator " +
                "must build the raw-source list from the matched-frame transforms and pass the same " +
                "Calibrator used by the WarpedFrames producer for the other strategies.");
        }

        var sources = job.RawLightSources;
        var calibrator = job.Calibrator;
        var debayerAlg = job.DebayerAlgorithm;
        var n = sources.Count;
        if (n == 0)
        {
            throw new InvalidOperationException("TilePipelinedStrategy: RawLightSources is empty.");
        }

        var canvasWidth = job.CanvasWidth;
        var canvasHeight = job.CanvasHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            throw new InvalidOperationException(
                $"TilePipelinedStrategy requires job.CanvasWidth + .CanvasHeight (got {canvasWidth}x{canvasHeight})");
        }

        // Phase 8.0: load + calibrate + debayer + warp every raw frame to the
        // canvas, then hand off to the in-memory Integrator. This is the
        // scaffolding step -- output is byte-identical to InRamAllFrames
        // (same arithmetic on same inputs), and the memory profile is the
        // same too. Phase 8.1 will replace the per-frame full-canvas warp
        // pass with per-tile raw reads + partial debayer + partial warp so
        // peak RAM is bounded by tile-column rather than N x canvas.
        var warped = new List<Image>(n);
        for (var f = 0; f < n; f++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Image.TryReadFitsFile(sources[f].Path, out var raw))
            {
                throw new InvalidDataException(
                    $"TilePipelinedStrategy: failed to read raw FITS at {sources[f].Path}");
            }
            var calibrated = calibrator.Apply(raw);
            var debayered = await calibrated.DebayerAsync(debayerAlg, cancellationToken: ct);
            var w = await debayered.WarpToReferenceGridAsync(sources[f].TransformToCanvas, canvasWidth, canvasHeight, ct);
            warped.Add(w);
        }

        return Integrator.Integrate(warped, job.Options);
    }
}
