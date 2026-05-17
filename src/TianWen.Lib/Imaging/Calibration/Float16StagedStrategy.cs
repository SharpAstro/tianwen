using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Stage the full warped canvas as float16 -- half the disk bytes of float32
/// staging. Pays a small CPU cost on the read path for the f16-&gt;f32
/// expansion (hardware <c>vcvtph2ps</c> is cheap; ~1 ns/px in practice) and
/// a fractional precision hit on the rejector's per-pixel column. For
/// astrophotography data with read noise on the order of 1-3 ADU, the lost
/// mantissa bits sit well below the noise floor.
/// </summary>
public sealed class Float16StagedStrategy : IIntegrationStrategy
{
    private readonly IntegrationCostModel _costs;

    public Float16StagedStrategy(IntegrationCostModel? costs = null)
    {
        _costs = costs ?? new IntegrationCostModel();
    }

    public IntegrationStrategyKind Kind => IntegrationStrategyKind.Float16Staged;

    /// <summary>Slight precision hit (10-bit mantissa vs 23) plus the
    /// footprint trim isn't applied -- below tile-pipelined but above
    /// chunked.</summary>
    public double FidelityScore => 0.92;

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

        // Half the bytes of float32 (full canvas, sizeof(half) = 2).
        var perFrameStaged = probe.CanvasBytes / 2;

        // Spill-to-disk: frames fitting in the FrameCache strong tier skip the
        // disk hit entirely. Project the strong cap from currently-free RAM
        // (same heuristic the runtime uses in FrameCache.DecideCacheCap).
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
                Rationale: $"needs {Format.GB(diskBytes)} disk for {diskFrames}/{probe.FrameCount} spilled frames, cap {Format.GB(diskCap)}");
        }

        var ram = IntegrationTileSizing.TileRamBytes(tile, probe) + probe.OutputRamBytes;

        var tileCount = ((probe.CanvasWidth + tile - 1) / tile) * ((probe.CanvasHeight + tile - 1) / tile);
        var io = _costs.DiskIo(
            bytes: diskBytes * 2,
            seeks: diskFrames + tileCount * diskFrames,
            kind: probe.StagingDiskKind);

        // f16->f32 unpack only for the spilled portion -- cached frames slice
        // straight from RAM with no unpack at all.
        var unpackPixels = (double)probe.CanvasBytes / sizeof(float) * diskFrames;
        var unpackMs = unpackPixels * _costs.CpuNsPerFloat16Unpack / 1e6;
        var eta = _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe) + io + System.TimeSpan.FromMilliseconds(unpackMs);

        var spillNote = diskFrames == 0
            ? $"all {probe.FrameCount} frames fit in cache -> 0 disk"
            : $"{strongCap} cached + {diskFrames} spilled to disk";
        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: diskBytes,
            EstimatedDuration: eta,
            Rationale: $"tile {tile} px, {Format.GB(diskBytes)} disk -- {spillNote} (float16)");
    }

    public async ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct)
    {
        Directory.CreateDirectory(job.StagingDir);

        // Same staging shape as FootprintStaged but pixel data is stored as
        // System.Half (2 bytes/pixel). The reader unpacks Half->float on
        // stripe reads so StreamingIntegrator is unchanged. Per-frame stats
        // are taken from the float32 warped image before staging so the
        // quantisation noise doesn't bias the normalisation min/median.
        var staged = new List<StagedAlignedFrame>(job.ExpectedFrameCount);
        // RAM cache for the warped float32 Image -- the staged file is
        // half-precision so a cache hit also avoids the Half->float unpack
        // on the read path, not just the disk seek.
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
                // RAM only -- no float16 write, no read-back, no quantisation
                // noise. The in-memory reader holds its own strong ref so the
                // frame is guaranteed alive for the whole integration. Frames
                // past the cap stage to disk as half precision and read back
                // with the existing unpack path.
                StreamingFrameReader reader;
                if (index < cache!.StrongCap)
                {
                    reader = StreamingFrameReader.InMemoryOnly(warped);
                }
                else
                {
                    var stagingPath = Path.Combine(job.StagingDir, $"frame_{index:D4}.bin");
                    StreamingFrameStaging.WriteHalf(warped, stagingPath);
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
            var result = StreamingIntegrator.Integrate(staged, job.Options);
            job.Progress?.Report(new IntegrationProgress(IntegrationPhase.Integrating, 1, 1, swStrat.Elapsed));
            return result;
        }
        finally
        {
            foreach (var s in staged) s.Dispose();
            try { Directory.Delete(job.StagingDir, recursive: true); }
            catch { /* best-effort */ }
            _ = cache;
        }
    }
}
