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

        // Half the bytes of float32 (full canvas, sizeof(half) = 2).
        var perFrameStaged = probe.CanvasBytes / 2;
        var diskBytes = perFrameStaged * probe.FrameCount;
        var diskCap = budget.AllowedDisk(probe);
        if (diskBytes > diskCap)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: IntegrationTileSizing.TileRamBytes(tile, probe) + probe.OutputRamBytes,
                EstimatedDiskBytes: diskBytes,
                EstimatedDuration: _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: $"needs {Format.GB(diskBytes)} disk, cap {Format.GB(diskCap)}");
        }

        var ram = IntegrationTileSizing.TileRamBytes(tile, probe) + probe.OutputRamBytes;

        var tileCount = ((probe.CanvasWidth + tile - 1) / tile) * ((probe.CanvasHeight + tile - 1) / tile);
        var io = _costs.DiskIo(
            bytes: diskBytes * 2,
            seeks: probe.FrameCount + tileCount * probe.FrameCount,
            kind: probe.StagingDiskKind);

        // f16->f32 unpack on every read pixel.
        var unpackPixels = (double)probe.CanvasBytes / sizeof(float) * probe.FrameCount;
        var unpackMs = unpackPixels * _costs.CpuNsPerFloat16Unpack / 1e6;
        var eta = _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe) + io + System.TimeSpan.FromMilliseconds(unpackMs);

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: diskBytes,
            EstimatedDuration: eta,
            Rationale: $"tile {tile} px, {Format.GB(diskBytes)} disk (float16 × {probe.FrameCount} frames)");
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
        try
        {
            var index = 0;
            await foreach (var warped in job.WarpedFrames(ct).WithCancellation(ct))
            {
                var stats = job.StatsRect.Width > 0 && job.StatsRect.Height > 0
                    ? Normalizer.ComputeStats(warped, job.StatsRect)
                    : Normalizer.ComputeStats(warped);

                var stagingPath = Path.Combine(job.StagingDir, $"frame_{index:D4}.bin");
                StreamingFrameStaging.WriteHalf(warped, stagingPath);

                var reader = new StreamingFrameReader(stagingPath);
                reader.SetCachedImage(warped);
                staged.Add(new StagedAlignedFrame(
                    reader,
                    warped.ImageMeta,
                    warped.MaxValue,
                    warped.Pedestal,
                    stats.PerChannelMin,
                    stats.PerChannelMedian));

                if (index == 0)
                {
                    var (c, w, h) = warped.Shape;
                    var frameBytes = (long)w * h * c * sizeof(float);
                    cache = new FrameCache(job.ExpectedFrameCount, FrameCache.DecideCacheCap(job.ExpectedFrameCount, frameBytes));
                }
                cache!.Set(index, warped);
                index++;
            }

            return StreamingIntegrator.Integrate(staged, job.Options);
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
