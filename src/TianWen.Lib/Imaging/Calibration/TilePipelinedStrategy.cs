using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// PLAN-stacking Phase 8: re-read raw lights tile-by-tile, calibrate + warp +
/// normalize + reject + combine in memory per output strip. No staging on
/// disk; repeated raw reads are absorbed by the OS page cache when the raw
/// lights fit in free RAM, and otherwise still cost two full sequential
/// passes per integration (one for stats, one for the strip warps).
/// </summary>
/// <remarks>
/// <para>Fidelity sits fractionally below <see cref="InRamAllFramesStrategy"/>
/// (0.98 vs 1.00) only as a tiebreaker -- the math is identical, but per-strip
/// pre-normalisation rebuilds the warped pixels twice (once for stats, once
/// for integration) so float-rounding can shift the last bit of a few pixels.</para>
///
/// <para><b>Phase 8.2 implementation</b>: two-pass strip-pipelined.
/// <list type="number">
/// <item><b>Pass 1 (per-frame stats)</b>: for each frame, load raw + calibrate +
/// debayer + warp to canvas + compute <see cref="NormalizationStats"/> via
/// <see cref="Normalizer.ComputeStats(Image, Rectangle)"/>. Frame data is
/// discarded after stats land -- peak RAM during this pass is one frame's
/// worth of debayered + warped canvas (~4 x debayered).</item>
/// <item><b>Pass 2 (strip integration)</b>: for each canvas row-strip (default
/// 256 rows tall), iterate the N frames, load raw + calibrate + debayer +
/// <see cref="Image.WarpRegionAsync"/> just the strip, pre-normalize the strip
/// using the pass-1 stats, then integrate. The N strip slices live in RAM
/// together; peak is <c>N x strip + 1 x debayered + 1 x raw</c> instead of
/// <c>N x canvas</c>.</item>
/// </list>
/// </para>
///
/// <para>For 30 frames at 3008^2 RGB with 256-row strips: ~270 MB (strips) +
/// ~108 MB (single debayered in flight) instead of <see cref="InRamAllFramesStrategy"/>'s
/// ~3.3 GB. The wallclock cost is ~2x the in-RAM warp pass since pass 1 redoes
/// the full warp just to gather stats -- a future optimisation can compute
/// stats from the unwarped debayered image directly, but the resulting min/median
/// would diverge slightly from other strategies that compute stats on the
/// warped canvas with the optional intersection rect, so we keep the 2-pass
/// shape for fidelity.</para>
/// </remarks>
public sealed class TilePipelinedStrategy : IIntegrationStrategy
{
    /// <summary>Output strip height in canvas rows. 256 chosen so 30 frames at
    /// 3008 wide RGB hold ~270 MB simultaneously -- well below typical free
    /// RAM but big enough to amortise per-strip overhead (frame loop, GC).
    /// </summary>
    public const int StripHeight = 256;

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
        // Phase 8.2: peak RAM = (N strips) + (1 in-flight debayered) +
        // (1 in-flight raw + calibrated). The strip share dominates for big N.
        // We over-budget the in-flight slot at 4x debayered to cover the
        // calibrate -> debayer -> warp pipeline's transient peak.
        var stripBytes = (long)probe.CanvasWidth * StripHeight * probe.ChannelCount * sizeof(float);
        var stripsRam = stripBytes * probe.FrameCount;
        var inFlightRam = (long)probe.FrameWidth * probe.FrameHeight * probe.ChannelCount * sizeof(float) * 4;
        var ram = stripsRam + inFlightRam + probe.OutputRamBytes;
        var cap = budget.AllowedRam(probe);

        // Wallclock: roughly 2x the in-RAM warp pass (pass 1 + pass 2) plus a
        // single stack pass. Disk I/O is whatever the raw frames cost to read
        // twice through the OS page cache; we charge 1x bandwidth since after
        // pass 1 the cache should hold them.
        var rawReadBytes = probe.FrameBytes * probe.FrameCount;
        var io = _costs.DiskIo(rawReadBytes, probe.FrameCount, probe.StagingDiskKind);
        var eta = (_costs.WarpAllFrames(probe) * 2.0) + _costs.StackAllFrames(probe) + io;

        if (ram > cap)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: eta,
                Rationale: $"strip RAM exceeds budget ({Format.GB(ram)} > cap {Format.GB(cap)})");
        }

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: 0,
            EstimatedDuration: eta,
            Rationale: $"2-pass strip-pipelined ({Format.GB(stripsRam)} strips + {Format.GB(inFlightRam)} in-flight, no staging)");
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

        var canvasW = job.CanvasWidth;
        var canvasH = job.CanvasHeight;
        if (canvasW <= 0 || canvasH <= 0)
        {
            throw new InvalidOperationException(
                $"TilePipelinedStrategy requires job.CanvasWidth + .CanvasHeight (got {canvasW}x{canvasH})");
        }

        var opts = job.Options;
        var statsRect = job.StatsRect;

        // ---------------- Pass 1: per-frame normalization stats ----------------
        // Walks each raw once: read + calibrate + debayer + warp full canvas +
        // gather per-channel min/median. Stats survive into pass 2; the
        // intermediate Images go out of scope and are GC-reclaimed before the
        // next iteration. Peak per iteration is ~4x debayered (raw + cal +
        // debayered + warped) plus the cached per-frame stats arrays which are
        // tiny (channelCount floats each).
        NormalizationStats[]? perFrameStats = null;
        var channelCount = 0;
        Image? metaSeed = null; // First-frame warped held briefly so master meta + maxValue propagate.

        if (opts.ApplyNormalization)
        {
            perFrameStats = new NormalizationStats[n];
        }

        for (var f = 0; f < n; f++)
        {
            ct.ThrowIfCancellationRequested();
            var warped = await LoadCalibrateDebayerWarpFullAsync(sources[f], calibrator, debayerAlg, canvasW, canvasH, ct);
            if (f == 0)
            {
                channelCount = warped.ChannelCount;
                metaSeed = warped;
            }
            if (perFrameStats is not null)
            {
                perFrameStats[f] = statsRect.IsEmpty
                    ? Normalizer.ComputeStats(warped)
                    : Normalizer.ComputeStats(warped, statsRect);
            }
            // metaSeed (when f == 0) holds the first warped; release of the
            // rest happens implicitly as `warped` falls out of scope.
        }

        if (channelCount == 0 || metaSeed is null)
        {
            throw new InvalidOperationException("TilePipelinedStrategy: pass 1 produced no frames.");
        }

        // ---------------- Pass 2: strip-by-strip integration ----------------
        var masterData = Image.CreateChannelData(channelCount, canvasH, canvasW);
        var rejectMapData = Image.CreateChannelData(1, canvasH, canvasW);
        long totalRejections = 0;

        // Disable Integrator's own normalization -- we pre-normalize each strip
        // using the pass-1 full-frame stats so the strip-level integrator sees
        // pixels in the same coordinate space as InRamAllFrames would.
        var stripOpts = opts with { ApplyNormalization = false };

        for (var stripY0 = 0; stripY0 < canvasH; stripY0 += StripHeight)
        {
            ct.ThrowIfCancellationRequested();
            var stripH = Math.Min(StripHeight, canvasH - stripY0);
            var stripRect = new Rectangle(0, stripY0, canvasW, stripH);

            var stripFrames = new List<Image>(n);
            for (var f = 0; f < n; f++)
            {
                ct.ThrowIfCancellationRequested();
                var strip = await LoadCalibrateDebayerWarpRegionAsync(
                    sources[f], calibrator, debayerAlg, stripRect, canvasW, canvasH, ct);
                if (perFrameStats is not null)
                {
                    // Pre-normalize in place using full-frame stats. The
                    // canonical Normalizer.Apply allocates -- it's cheap
                    // relative to the warp + debayer cost, and matches what
                    // the other strategies do.
                    strip = Normalizer.Apply(strip, perFrameStats[f], opts.NormalizationTarget);
                }
                stripFrames.Add(strip);
            }

            var stripResult = Integrator.Integrate(stripFrames, stripOpts);
            CopyStripIntoMaster(stripResult.Master, masterData, stripY0, channelCount);
            CopyStripIntoMaster(stripResult.RejectionMap, rejectMapData, stripY0, channelCount: 1);
            totalRejections += stripResult.TotalRejections;
        }

        var firstMeta = metaSeed.ImageMeta;
        var masterImage = new Image(
            data: masterData,
            bitDepth: BitDepth.Float32,
            maxValue: metaSeed.MaxValue,
            minValue: 0f,
            pedestal: metaSeed.Pedestal,
            imageMeta: firstMeta);
        var rejectMapImage = new Image(
            data: rejectMapData,
            bitDepth: BitDepth.Float32,
            maxValue: 1f,
            minValue: 0f,
            pedestal: 0f,
            imageMeta: firstMeta);

        var meanRate = n > 0
            ? (double)totalRejections / ((double)n * canvasW * canvasH * channelCount)
            : 0.0;

        return new IntegrationResult(masterImage, rejectMapImage, n, totalRejections, meanRate);
    }

    private static async Task<Image> LoadCalibrateDebayerWarpFullAsync(
        RawLightSource source, Calibrator calibrator, DebayerAlgorithm debayerAlg,
        int canvasW, int canvasH, CancellationToken ct)
    {
        if (!Image.TryReadFitsFile(source.Path, out var raw))
        {
            throw new InvalidDataException($"TilePipelinedStrategy: failed to read raw FITS at {source.Path}");
        }
        var calibrated = calibrator.Apply(raw);
        var debayered = await calibrated.DebayerAsync(debayerAlg, cancellationToken: ct);
        return await debayered.WarpToReferenceGridAsync(source.TransformToCanvas, canvasW, canvasH, ct);
    }

    private static async Task<Image> LoadCalibrateDebayerWarpRegionAsync(
        RawLightSource source, Calibrator calibrator, DebayerAlgorithm debayerAlg,
        Rectangle canvasRegion, int canvasW, int canvasH, CancellationToken ct)
    {
        if (!Image.TryReadFitsFile(source.Path, out var raw))
        {
            throw new InvalidDataException($"TilePipelinedStrategy: failed to read raw FITS at {source.Path}");
        }
        var calibrated = calibrator.Apply(raw);
        var debayered = await calibrated.DebayerAsync(debayerAlg, cancellationToken: ct);
        return await debayered.WarpRegionAsync(source.TransformToCanvas, canvasRegion, canvasW, canvasH, ct);
    }

    private static void CopyStripIntoMaster(Image stripImage, float[][,] masterData, int stripY0, int channelCount)
    {
        // Bulk-copy each strip channel into the master at the correct row
        // offset. Strip dimensions are stripImage.Height x stripImage.Width;
        // master dimensions match canvasH x canvasW.
        var stripH = stripImage.Height;
        var stripW = stripImage.Width;
        for (var ch = 0; ch < channelCount; ch++)
        {
            var stripCh = stripImage.GetChannelArray(ch);
            var masterCh = masterData[ch];
            for (var y = 0; y < stripH; y++)
            {
                // Block.CopyBlock pattern via Buffer.BlockCopy for row-major
                // float[,] is fastest, but float[,] doesn't surface a span
                // directly. Inner loop stays scalar so the cost is one full
                // sweep over master pixels regardless of strip count.
                for (var x = 0; x < stripW; x++)
                {
                    masterCh[stripY0 + y, x] = stripCh[y, x];
                }
            }
        }
    }
}
