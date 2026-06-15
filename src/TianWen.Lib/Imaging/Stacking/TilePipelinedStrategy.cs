using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// docs/plans/stacking.md Phase 8: re-read raw lights as needed, calibrate + debayer +
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

        // Wallclock estimate. Two passes:
        //   Pass 1: decode + debayer + warp + stats for every frame, once.
        //   Pass 2: per strip, for each frame:
        //           - cache hit  -> just warp the strip and reject+combine
        //           - cache miss -> re-decode + re-debayer + warp the strip
        //
        // Cache budget heuristic: freeRam / 4. The other quarter goes to strip
        // working set (N strips x frames-in-flight), output + rejmap buffers,
        // and GC headroom. Calibrated 2026-05-16 against the cancelled SoL
        // 244-frame run: 16.8 GB free / 4 = 4.2 GB cache; working set
        // (debayered 244 x ~27 MB) = 6.6 GB -> 36% miss rate -> ~142 s extra
        // debayer on pass-2. Combined with the rest of the model this yields
        // eta ~1600 s vs Float16Staged's ~647 s, matching the empirical
        // ~3x-slower observation that triggered this fix.
        var cacheRamBudget = probe.FreeRamBytes > 0 ? probe.FreeRamBytes / 4 : long.MaxValue;
        var cacheMissRate = _costs.EstimateCacheMissRate(probe, cacheRamBudget);
        var loadCalibrateCost = _costs.LoadAndCalibrateAllFrames(probe);
        var debayerCost = _costs.DebayerAllFrames(probe);
        var warpCost = _costs.WarpAllFrames(probe);
        var stackCost = _costs.StackAllFrames(probe);
        // Pass 1: load + calibrate + decode + debayer + warp + stats once per frame.
        // Pass 2: warp + stack again (each strip extracts a sub-rect from cache),
        //         plus load+calibrate+debayer rework for cache misses.
        var pass2RedecodeCost = TimeSpan.FromMilliseconds((loadCalibrateCost.TotalMilliseconds + debayerCost.TotalMilliseconds) * cacheMissRate);
        var eta = loadCalibrateCost + debayerCost + warpCost + pass2RedecodeCost + warpCost + stackCost;

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
            Rationale: $"cached-debayered + strip pipeline (target {Format.GB(ram)}, floor {Format.GB(minRam)})")
        {
            // Expose the floor so the selector's memory-pressure penalty
            // ranks us against what we MUST have (minRam), not what we'd
            // optimistically use (ram). Without this, the high target on
            // mosaic stacks (N x debayered frame size) makes this strategy
            // lose to staged alternatives even when the floor fits comfortably.
            FloorRamBytes = minRam,
        };
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
        var swStrat = System.Diagnostics.Stopwatch.StartNew();
        var stripsTotal = (canvasH + StripHeight - 1) / StripHeight;

        // ---------------- Pass 1: decode + calibrate + stats, register in cache ----------------
        // Phase 8.6: cache the CALIBRATED RAW (1-channel float, ~36 MB for a
        // 3008x3008 sensor) instead of the debayered RGB Image (3 channels,
        // ~108 MB). Three benefits:
        //  - 3x more frames fit the strong tier for the same RAM budget
        //    (cap 28 -> ~130 on 16 GB hosts with 6 GB free), which is the
        //    difference between a viable strategy and per-strip thrashing.
        //  - On cache miss in pass 2 we only redo Load + Calibrate, not the
        //    expensive AHD debayer.
        //  - Pass 2 does sub-region debayer (only the strip footprint) per
        //    strip-frame, so the 22x-vs-VNG AHD cost only applies to ~2% of
        //    the canvas area per call.
        // Stats are computed once per frame from a full debayer that we then
        // drop -- same wall-clock cost as today's pass 1.
        var perFrameStats = opts.ApplyNormalization ? new NormalizationStats[n] : null;

        // First frame sets up cache + metaSeed; subsequent frames reuse them.
        // Hoisted out of the loop so neither `cache` nor `metaSeed` needs a
        // null-forgiving operator anywhere downstream -- the compiler sees
        // them as definitely-assigned non-null locals from here on.
        ct.ThrowIfCancellationRequested();
        var firstCalibrated = DecodeCalibrate(sources[0], calibrator);
        var calibratedBytes = (long)firstCalibrated.Width * firstCalibrated.Height * firstCalibrated.ChannelCount * sizeof(float);
        var cache = new FrameCache(n, FrameCache.DecideCacheCap(n, calibratedBytes));
        var metaSeed = await firstCalibrated.DebayerAsync(debayerAlg, cancellationToken: ct);
        var channelCount = metaSeed.ChannelCount;
        if (perFrameStats is not null)
        {
            perFrameStats[0] = Normalizer.ComputeStats(metaSeed);
        }
        cache.Set(0, firstCalibrated);
        job.Progress?.Report(new IntegrationProgress(IntegrationPhase.LoadingFrames, 1, n, swStrat.Elapsed));

        for (var f = 1; f < n; f++)
        {
            ct.ThrowIfCancellationRequested();
            var calibrated = DecodeCalibrate(sources[f], calibrator);

            // Stats need the debayered frame, but we don't cache it.
            // Allocate locally, compute stats, let GC reclaim between
            // iterations (or sooner under pressure).
            if (perFrameStats is not null)
            {
                var debayeredOnce = await calibrated.DebayerAsync(debayerAlg, cancellationToken: ct);
                perFrameStats[f] = Normalizer.ComputeStats(debayeredOnce);
            }

            cache.Set(f, calibrated);
            job.Progress?.Report(new IntegrationProgress(IntegrationPhase.LoadingFrames, f + 1, n, swStrat.Elapsed));
        }

        var stripIdx = 0;

        // ---------------- Pass 2: strip-by-strip integration ----------------
        var masterData = Image.CreateChannelData(channelCount, canvasH, canvasW);
        var rejectMapData = Image.CreateChannelData(1, canvasH, canvasW);
        long totalRejections = 0;
        var stripOpts = opts with { ApplyNormalization = false };

        // Pre-allocate full-canvas destination channels for sub-region debayer.
        // The sub-region debayer fills only the strip-footprint rows + halo;
        // the rest stays as pool garbage (harmless because WarpRegionAsync
        // only samples inside the rect by construction). Reused across all
        // strip-frame calls -- one alloc instead of N * stripsTotal allocs.
        var rawW = firstCalibrated.Width;
        var rawH = firstCalibrated.Height;
        var debayerDestArrays = Image.CreateChannelData(channelCount, rawH, rawW);
        var debayerDestChannels = new Channel[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            debayerDestChannels[c] = new Channel(debayerDestArrays[c], default, 0f, 0f, (byte)c);
        }
        // Halo for projecting the canvas-strip back to source: 2 pixels.
        // WarpRegionAsync's SubpixelValue does 4-tap bilinear, so 1 px is the
        // strict minimum; +1 cushions floating-point round-off + small
        // rotation in the transform. AHD's internal halo (radius +
        // homogeneity + 1 for Phase 4 = 5 pixels) is added inside
        // DebayerRegionIntoAsync.
        const int projectionHalo = 2;

        for (var stripY0 = 0; stripY0 < canvasH; stripY0 += StripHeight)
        {
            ct.ThrowIfCancellationRequested();
            var stripH = Math.Min(StripHeight, canvasH - stripY0);
            var stripRect = new Rectangle(0, stripY0, canvasW, stripH);

            var stripFrames = new List<Image>(n);
            for (var f = 0; f < n; f++)
            {
                ct.ThrowIfCancellationRequested();
                Image calibrated;
                if (cache.TryGet(f, out var cached))
                {
                    calibrated = cached;
                }
                else
                {
                    // Miss in both tiers -- re-decode + recalibrate (no
                    // debayer; we redo that per-strip below).
                    calibrated = DecodeCalibrate(sources[f], calibrator);
                    cache.Set(f, calibrated);
                }

                var transform = sources[f].TransformToCanvas;
                var sourceRect = CanvasGeometry.ProjectCanvasRectToSourceRect(stripRect, transform, rawW, rawH, projectionHalo);

                // Sub-region debayer of the strip's source footprint (plus
                // halo) into the pre-allocated full-canvas dest. Only the
                // sourceRect rows hold valid AHD output; WarpRegionAsync
                // samples only inside that region by construction (its
                // bounds-check converts out-of-rect samples to NaN, which
                // the rejector ignores).
                var debayered = await calibrated.DebayerRegionIntoAsync(debayerDestChannels, debayerAlg, sourceRect, ct);
                var strip = await debayered.WarpRegionAsync(transform, stripRect, canvasW, canvasH, ct);
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

            stripIdx++;
            job.Progress?.Report(new IntegrationProgress(IntegrationPhase.Integrating, stripIdx, stripsTotal, swStrat.Elapsed));
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

    /// <summary>Load + calibrate, no debayer. Used by Phase 8.6 pass 1 to
    /// cache the calibrated raw (3x denser than debayered RGB in the strong
    /// tier) and by pass 2 cache-miss recovery. The actual debayer happens
    /// per-strip via <see cref="Image.DebayerRegionIntoAsync"/>.</summary>
    private static Image DecodeCalibrate(RawLightSource source, Calibrator calibrator)
    {
        if (!Image.TryReadFitsFile(source.Path, out var raw))
        {
            throw new InvalidDataException($"TilePipelinedStrategy: failed to read raw FITS at {source.Path}");
        }
        return calibrator.Apply(raw);
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
