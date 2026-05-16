using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Stage each warped frame to disk as float32 but only over its non-NaN
/// footprint AABB (not the full union canvas), then tile-integrate from
/// staging. Saves 5-50% of disk vs full-canvas staging depending on mount
/// motion -- low-motion runs barely move the needle, meridian-flipped runs
/// halve the disk bill.
/// </summary>
/// <remarks>
/// Today's <c>StreamingIntegrator</c> path is close to this strategy except
/// it stages the full canvas (NaN strips and all). Switching to footprint
/// staging is the cheapest disk-reducing change short of a full Phase 8.
/// </remarks>
public sealed class FootprintStagedStrategy : IIntegrationStrategy
{
    private readonly IntegrationCostModel _costs;
    private readonly double _footprintFraction;

    /// <param name="costs">Cost-model coefficients (default ok).</param>
    /// <param name="footprintFraction">Average per-frame non-NaN coverage as a
    /// fraction of the canvas. Defaults to 0.92 (typical low-motion run).
    /// Drops to ~0.5 for sessions with meridian flips; orchestrator can
    /// override once it computes the per-frame polygons.</param>
    public FootprintStagedStrategy(IntegrationCostModel? costs = null, double footprintFraction = 0.92)
    {
        _costs = costs ?? new IntegrationCostModel();
        _footprintFraction = footprintFraction;
    }

    public IntegrationStrategyKind Kind => IntegrationStrategyKind.FootprintStaged;

    /// <summary>Fractionally below tile-pipelined: full float32 precision
    /// retained, but a tiny bit of edge data is lost to the footprint crop
    /// vs the full union BB.</summary>
    public double FidelityScore => 0.95;

    public bool SupportsLiveStacking => false;

    public StrategyFit Evaluate(IntegrationProbe probe, ResourceBudget budget)
    {
        var ramCap = budget.AllowedRam(probe) - probe.OutputRamBytes;
        if (ramCap <= 0)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: probe.OutputRamBytes,
                EstimatedDiskBytes: 0,
                EstimatedDuration: _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: $"output buffer alone ({Format.GB(probe.OutputRamBytes)}) busts RAM budget");
        }

        var tile = IntegrationTileSizing.Side(probe, ramCap);
        if (tile < 0)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ramCap,
                EstimatedDiskBytes: 0,
                EstimatedDuration: _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: $"tile floor {IntegrationTileSizing.MinTileSide} px would need more than {Format.GB(ramCap)} for {probe.FrameCount} frames");
        }

        // Disk: per-frame footprint × N, write once + read once = 2×.
        var perFrameStaged = (long)(probe.CanvasBytes * _footprintFraction);
        var diskBytes = perFrameStaged * probe.FrameCount;
        var diskCap = budget.AllowedDisk(probe);
        if (diskBytes > diskCap)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: IntegrationTileSizing.TileRamBytes(tile, probe) + probe.OutputRamBytes,
                EstimatedDiskBytes: diskBytes,
                EstimatedDuration: _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: $"needs {Format.GB(diskBytes)} disk, cap {Format.GB(diskCap)} (footprint {_footprintFraction:P0})");
        }

        var ram = IntegrationTileSizing.TileRamBytes(tile, probe) + probe.OutputRamBytes;
        // One sequential write pass + one sequential read pass per frame.
        // Seeks are roughly per-frame + per-tile during read-back.
        var tileCount = ((probe.CanvasWidth + tile - 1) / tile) * ((probe.CanvasHeight + tile - 1) / tile);
        var io = _costs.DiskIo(
            bytes: diskBytes * 2,
            seeks: probe.FrameCount + tileCount * probe.FrameCount,
            kind: probe.StagingDiskKind);
        var eta = _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe) + io;

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: diskBytes,
            EstimatedDuration: eta,
            Rationale: $"tile {tile} px, {Format.GB(diskBytes)} disk ({_footprintFraction:P0} of canvas × {probe.FrameCount} frames), {DiskLabel(probe.StagingDiskKind)}");
    }

    private static string DiskLabel(DiskKind kind) => kind switch
    {
        DiskKind.Nvme => "NVMe",
        DiskKind.Ssd => "SSD",
        DiskKind.Hdd => "HDD (slow)",
        _ => "unknown disk",
    };

    public async ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct)
    {
        Directory.CreateDirectory(job.StagingDir);

        // When per-frame footprints are supplied, stage only the footprint
        // sub-region of each frame (v2 format). The reader NaN-pads outside
        // the footprint on stripe reads so the integrator's NaN-skipping
        // handles those pixels transparently. Without footprints, stage the
        // full canvas (v1 format) -- equivalent to the pre-strategy behavior.
        var hasFootprints = job.FrameFootprints is not null && job.FrameFootprints.Count > 0;
        var staged = new List<StagedAlignedFrame>(job.ExpectedFrameCount);
        try
        {
            var index = 0;
            await foreach (var warped in job.WarpedFrames(ct).WithCancellation(ct))
            {
                var stats = job.StatsRect.Width > 0 && job.StatsRect.Height > 0
                    ? Normalizer.ComputeStats(warped, job.StatsRect)
                    : Normalizer.ComputeStats(warped);

                var stagingPath = Path.Combine(job.StagingDir, $"frame_{index:D4}.bin");
                if (hasFootprints && index < job.FrameFootprints!.Count)
                {
                    var fp = job.FrameFootprints[index];
                    if (fp.Width > 0 && fp.Height > 0)
                    {
                        StreamingFrameStaging.WriteWithFootprint(warped, stagingPath, fp);
                    }
                    else
                    {
                        StreamingFrameStaging.Write(warped, stagingPath);
                    }
                }
                else
                {
                    StreamingFrameStaging.Write(warped, stagingPath);
                }

                var reader = new StreamingFrameReader(stagingPath);
                staged.Add(new StagedAlignedFrame(
                    reader,
                    warped.ImageMeta,
                    warped.MaxValue,
                    warped.Pedestal,
                    stats.PerChannelMin,
                    stats.PerChannelMedian));
                index++;
            }

            return StreamingIntegrator.Integrate(staged, job.Options);
        }
        finally
        {
            foreach (var s in staged) s.Dispose();
            try { Directory.Delete(job.StagingDir, recursive: true); }
            catch { /* best-effort; caller may inspect intermediates */ }
        }
    }
}
