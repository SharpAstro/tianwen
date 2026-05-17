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
                EstimatedDuration: _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: $"output buffer alone ({Format.GB(probe.OutputRamBytes)}) busts RAM budget");
        }

        var tile = IntegrationTileSizing.Side(probe, ramCap);
        if (tile < 0)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ramCap,
                EstimatedDiskBytes: 0,
                EstimatedDuration: _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: $"tile floor {IntegrationTileSizing.MinTileSide} px would need more than {Format.GB(ramCap)} for {probe.FrameCount} frames");
        }

        // Disk: per-frame footprint × N_spilled, write once + read once = 2×.
        // Spill-to-disk: frames within the FrameCache strong tier stay in RAM
        // (no disk write, no read-back, full float32 precision); only the
        // overflow gets staged. Use canvas bytes for the cap (worst-case
        // sizing), same as the runtime FrameCache.DecideCacheCap call.
        var perFrameStaged = (long)(probe.CanvasBytes * _footprintFraction);
        var strongCap = FrameCache.DecideCacheCap(probe.FrameCount, probe.CanvasBytes);
        var diskFrames = Math.Max(0, probe.FrameCount - strongCap);
        var diskBytes = perFrameStaged * diskFrames;
        var diskCap = budget.AllowedDisk(probe);
        if (diskBytes > diskCap)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: IntegrationTileSizing.TileRamBytes(tile, probe) + probe.OutputRamBytes,
                EstimatedDiskBytes: diskBytes,
                EstimatedDuration: _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: $"needs {Format.GB(diskBytes)} disk for {diskFrames}/{probe.FrameCount} spilled frames, cap {Format.GB(diskCap)} (footprint {_footprintFraction:P0})");
        }

        var ram = IntegrationTileSizing.TileRamBytes(tile, probe) + probe.OutputRamBytes;
        // One sequential write pass + one sequential read pass per spilled
        // frame. Seeks are roughly per-spilled-frame + per-tile during the
        // read-back of spilled frames. Cached frames contribute zero IO.
        var tileCount = ((probe.CanvasWidth + tile - 1) / tile) * ((probe.CanvasHeight + tile - 1) / tile);
        var io = _costs.DiskIo(
            bytes: diskBytes * 2,
            seeks: diskFrames + tileCount * diskFrames,
            kind: probe.StagingDiskKind);
        var eta = _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe) + io;

        var spillNote = diskFrames == 0
            ? $"all {probe.FrameCount} frames fit in cache -> 0 disk"
            : $"{strongCap} cached + {diskFrames} spilled, {_footprintFraction:P0} of canvas";
        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: diskBytes,
            EstimatedDuration: eta,
            Rationale: $"tile {tile} px, {Format.GB(diskBytes)} disk -- {spillNote}, {DiskLabel(probe.StagingDiskKind)}");
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
        // RAM cache for warped frames: every reader registers a weak ref to
        // its warped Image so the chunk integrator can slice from RAM when
        // the GC hasn't reclaimed it. The strong-ref retention lives on the
        // FrameCache; on a roomy host all N frames stay alive end-to-end and
        // disk reads collapse to RAM slices. On a tight host weak refs die
        // and the reader transparently falls back to the staged-file path.
        FrameCache? cache = null;
        var swStrat = System.Diagnostics.Stopwatch.StartNew();
        var n = job.ExpectedFrameCount;
        try
        {
            var index = 0;
            await foreach (var warped in job.WarpedFrames(ct).WithCancellation(ct))
            {
                if (index == 0)
                {
                    var (c, w, h) = warped.Shape;
                    var frameBytes = (long)w * h * c * sizeof(float);
                    cache = new FrameCache(job.ExpectedFrameCount, FrameCache.DecideCacheCap(job.ExpectedFrameCount, frameBytes));
                }

                var stats = job.StatsRect.Width > 0 && job.StatsRect.Height > 0
                    ? Normalizer.ComputeStats(warped, job.StatsRect)
                    : Normalizer.ComputeStats(warped);

                // Spill-to-disk: frames within the cache strong tier stay in
                // RAM only -- no disk write, no read-back, full float32
                // fidelity from the warped image. Frames past the cap stage
                // as today (footprint-trimmed float32 when the footprint
                // hint is available; otherwise full canvas).
                StreamingFrameReader reader;
                if (index < cache!.StrongCap)
                {
                    reader = StreamingFrameReader.InMemoryOnly(warped);
                }
                else
                {
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

                    reader = new StreamingFrameReader(stagingPath);
                    reader.SetCachedImage(warped);
                }

                staged.Add(new StagedAlignedFrame(
                    reader,
                    warped.ImageMeta,
                    warped.MaxValue,
                    warped.Pedestal,
                    stats.PerChannelMin,
                    stats.PerChannelMedian));

                cache.Set(index, warped);
                index++;
                job.Progress?.Report(new IntegrationProgress(IntegrationPhase.LoadingFrames, index, n, swStrat.Elapsed));
            }

            job.Progress?.Report(new IntegrationProgress(IntegrationPhase.Integrating, 0, 1, swStrat.Elapsed));
            // Honour the selector's sink decision; null factory falls back to
            // ArraySink inside StreamingIntegrator.
            var first = staged[0].Reader;
            var masterSink = job.MasterSinkFactory?.Invoke(first.Channels, first.Width, first.Height);
            var result = StreamingIntegrator.Integrate(staged, job.Options, masterSink: masterSink);
            job.Progress?.Report(new IntegrationProgress(IntegrationPhase.Integrating, 1, 1, swStrat.Elapsed));
            return result;
        }
        finally
        {
            foreach (var s in staged) s.Dispose();
            try { Directory.Delete(job.StagingDir, recursive: true); }
            catch { /* best-effort; caller may inspect intermediates */ }
            // cache falls out of scope; FrameCache holds no native resources.
            _ = cache;
        }
    }
}
