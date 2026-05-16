using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// PLAN-stacking Phase 8: re-read raw lights as needed, calibrate + debayer +
/// warp + normalize + reject + combine in memory per output strip. Caches
/// debayered frames in RAM when memory allows so the common case becomes
/// "one decode per frame, just like InRam"; falls back to re-decode on miss
/// when memory is tight. The tile abstraction is about cache survivability
/// (debayered frames can be evicted and re-decoded without corrupting state),
/// not about a strict per-strip RAM bound.
/// </summary>
/// <remarks>
/// <para>Fidelity sits fractionally below <see cref="InRamAllFramesStrategy"/>
/// (0.98 vs 1.00) only as a tiebreaker. The math is identical when the cache
/// holds all N frames (pure InRam path). When some frames re-decode, the
/// pre-normalize step uses stats gathered from the debayered frame rather
/// than the warped canvas, which loses the <see cref="IntegrationJob.StatsRect"/>
/// intersection-clamp the other strategies apply -- a small numerical drift
/// for stacks with heavy edge rotation, undetectable for the typical
/// small-shift session.</para>
///
/// <para><b>Implementation</b>:
/// <list type="number">
/// <item><b>Pass 1 (per-frame stats + cache)</b>: for each frame, load raw +
/// calibrate + debayer. Compute <see cref="NormalizationStats"/> directly
/// from the debayered image (whole frame, NaN-ignoring). Conditionally cache
/// the debayered Image -- the cache cap is sized at strategy entry from the
/// current GC heap budget so we keep as many as fit without inducing
/// paging.</item>
/// <item><b>Pass 2 (strip integration)</b>: for each canvas row-strip, iterate
/// the N frames; if a frame is cached, warp the strip directly off the
/// cached debayered; if not, re-decode the raw + debayer (still in RAM)
/// and warp the strip. Pre-normalize each strip using pass-1 stats, then
/// integrate. Strip masters are copied into the final master at the strip's
/// row offset.</item>
/// </list>
/// </para>
///
/// <para>Memory profile: peak = (cached debayered set) + (N strips) +
/// (output master + rejection map) + (one in-flight raw + cal + debayered).
/// When cacheCount = N (roomy host) this is essentially the InRam profile
/// minus N x warped-canvas (we hold N x debayered, not N x warped). When
/// cacheCount = 0 (very tight host) this degrades to the strict "one frame
/// at a time" path with full re-decode per strip.</para>
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
        // Memory model: the cache can hold up to N debayered frames (~N x
        // FrameBytes) when there's headroom, but the strategy will function
        // down to (1 in-flight debayered) + (N strips) + (output + rejmap).
        // The "EstimatedRamBytes" we report is the optimistic ceiling -- if
        // we'd happily use N x debayered when available, report it so the
        // selector can apply the free-RAM soft penalty on tight hosts.
        var cacheRam = probe.FrameBytes * probe.FrameCount;
        var stripBytes = (long)probe.CanvasWidth * StripHeight * probe.ChannelCount * sizeof(float);
        var stripsRam = stripBytes * probe.FrameCount;
        var inFlightRam = probe.FrameBytes * 2; // raw + cal + debayered in flight
        var ram = cacheRam + stripsRam + inFlightRam + probe.OutputRamBytes;
        var cap = budget.AllowedRam(probe);

        // Wallclock estimate: best case (full cache) ~= InRam warp + stack pass.
        // Worst case (no cache) ~= 2x warp pass (pass 1 stats + pass 2 strips).
        // The selector ranker uses this number; we report the optimistic
        // best-case ETA since the cache typically does fit.
        var eta = _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe);

        // Hard gate: even the minimal footprint (no cache) must fit under
        // physical-RAM budget. That floor is strips + in-flight + output.
        var minRam = stripsRam + inFlightRam + probe.OutputRamBytes;
        if (minRam > cap)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: eta,
                Rationale: $"strip + output floor exceeds budget ({Format.GB(minRam)} > cap {Format.GB(cap)})");
        }

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: 0,
            EstimatedDuration: eta,
            Rationale: $"cached-debayered + strip pipeline (target {Format.GB(ram)}, floor {Format.GB(minRam)})");
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

        // ---------------- Pass 1: decode + stats, conditionally cache ----------------
        // Two-tier cache:
        // - `strongCache[f]` holds the first `cacheCap` frames with a strong
        //   reference so they're guaranteed-present during pass 2. `cacheCap`
        //   is sized from current GC headroom (~50% of free RAM).
        // - `weakCache[f]` holds *every* decoded frame in a WeakReference.
        //   Frames past the strong cap that the GC hasn't reclaimed yet are
        //   bonus hits -- we always lookup weakCache first, and only fall
        //   through to re-decode if both tiers miss. This costs ~16 bytes
        //   per slot (the WeakReference object itself) and turns "RAM was
        //   underutilised between pass 1 and pass 2" into a free speedup.
        var strongCache = new Image?[n];
        var weakCache = new WeakReference<Image>?[n];
        NormalizationStats[]? perFrameStats = opts.ApplyNormalization ? new NormalizationStats[n] : null;
        var channelCount = 0;
        long debayeredBytes = 0;
        Image? metaSeed = null;
        var cacheCap = 0;

        for (var f = 0; f < n; f++)
        {
            ct.ThrowIfCancellationRequested();
            var debayered = await DecodeCalibrateDebayerAsync(sources[f], calibrator, debayerAlg, ct);
            if (f == 0)
            {
                channelCount = debayered.ChannelCount;
                debayeredBytes = (long)debayered.Width * debayered.Height * channelCount * sizeof(float);
                metaSeed = debayered;
                // Compute cache cap after the first decode so we know the real
                // per-frame byte cost. Use 50% of currently-free heap as the
                // budget for the cache, leaving the other half for strips +
                // output + in-flight transients in pass 2.
                cacheCap = DecideCacheCap(n, debayeredBytes);
            }

            if (perFrameStats is not null)
            {
                // Stats from the debayered image (not the warped canvas). For
                // small rotations this differs negligibly from the
                // warped-canvas + statsRect path used by other strategies --
                // NaN is naturally absent in the unwarped data so the
                // intersection clamp is moot. Heavy-rotation stacks get a
                // small drift; the speed win is large.
                perFrameStats[f] = Normalizer.ComputeStats(debayered);
            }

            weakCache[f] = new WeakReference<Image>(debayered);
            if (f < cacheCap)
            {
                strongCache[f] = debayered;
            }
            // else: `debayered` only survives if the GC hasn't reclaimed it.
        }

        if (channelCount == 0 || metaSeed is null)
        {
            throw new InvalidOperationException("TilePipelinedStrategy: pass 1 produced no frames.");
        }

        // ---------------- Pass 2: strip-by-strip integration ----------------
        var masterData = Image.CreateChannelData(channelCount, canvasH, canvasW);
        var rejectMapData = Image.CreateChannelData(1, canvasH, canvasW);
        long totalRejections = 0;
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
                Image debayered;
                if (strongCache[f] is { } strong)
                {
                    debayered = strong;
                }
                else if (weakCache[f] is { } weak && weak.TryGetTarget(out var alive))
                {
                    // Bonus cache hit: the GC hadn't reclaimed this debayered
                    // yet. Adopt a strong reference for the rest of pass 2 so
                    // subsequent strips don't risk losing it to a collect.
                    debayered = alive;
                    strongCache[f] = alive;
                }
                else
                {
                    debayered = await DecodeCalibrateDebayerAsync(sources[f], calibrator, debayerAlg, ct);
                    // Re-register the weak reference -- if memory recovers, a
                    // later strip might still find this frame alive without
                    // another decode.
                    weakCache[f] = new WeakReference<Image>(debayered);
                }
                var strip = await debayered.WarpRegionAsync(sources[f].TransformToCanvas, stripRect, canvasW, canvasH, ct);
                if (perFrameStats is not null)
                {
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

    /// <summary>
    /// Decide how many debayered frames we'll keep in RAM during pass 1 so
    /// pass 2 can warp from cache instead of re-decoding. Sizes the cache at
    /// 50% of currently-free heap budget (the other 50% covers strip arrays,
    /// in-flight transients, integrator scratch, and any other background
    /// activity the host has in flight). Returns 0..n.
    /// </summary>
    internal static int DecideCacheCap(int n, long debayeredBytes)
    {
        if (debayeredBytes <= 0) return 0;
        var info = GC.GetGCMemoryInfo();
        var currentlyFree = Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        var cacheBudget = currentlyFree / 2;
        var maxByBytes = cacheBudget / debayeredBytes;
        if (maxByBytes <= 0) return 0;
        if (maxByBytes >= n) return n;
        return (int)maxByBytes;
    }

    private static async Task<Image> DecodeCalibrateDebayerAsync(
        RawLightSource source, Calibrator calibrator, DebayerAlgorithm debayerAlg, CancellationToken ct)
    {
        if (!Image.TryReadFitsFile(source.Path, out var raw))
        {
            throw new InvalidDataException($"TilePipelinedStrategy: failed to read raw FITS at {source.Path}");
        }
        var calibrated = calibrator.Apply(raw);
        return await calibrated.DebayerAsync(debayerAlg, cancellationToken: ct);
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
