using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Tile-pipelined Bayer drizzle: combines <see cref="DrizzleStrategy"/>'s
/// forward-projection algorithm (no debayer, no warp, no reject-combine)
/// with <see cref="TilePipelinedStrategy"/>'s strip-pipelined memory
/// layout (one strip's worth of flux/weight planes live at a time,
/// rather than the full canvas).
///
/// <para>Algorithm × memory-strategy is an orthogonal product:
/// <list type="bullet">
///   <item>Algorithm = Standard (debayer + warp + reject-combine) vs
///   Drizzle (forward-project Bayer samples).</item>
///   <item>Memory strategy = in-RAM canvas vs strip-pipelined vs
///   footprint-staged vs ...</item>
/// </list>
/// <see cref="DrizzleStrategy"/> sits at (Drizzle, in-RAM canvas) and
/// this strategy sits at (Drizzle, strip-pipelined). The kernel is
/// identical, just deposited into a strip-sized accumulator instead of
/// a canvas-sized one.</para>
///
/// <para><b>Memory profile</b>: peak = (calibrated-frame cache) +
/// (strip working set: 2 planes × 3 channels × <see cref="StripHeight"/> ×
/// canvasW × float) + (master flux + coverage maps, canvas-sized,
/// unavoidable since that's the output). The strip working set is
/// ~25 MB at 4032 wide × 256 rows × 24 B/cell vs ~400 MB for
/// full-canvas drizzle, scaling 4x harder at Phase 2 (2.0x output
/// scale).</para>
///
/// <para><b>Per-strip kernel</b>: each frame's contribution to a strip
/// is bounded by the source-pixel rect that inverse-projects to the
/// strip rect (via <see cref="CanvasGeometry.ProjectCanvasRectToSourceRect"/>).
/// Frames whose entire source projection misses the strip are skipped
/// entirely; for the rest we only iterate the relevant source sub-rect.
/// This trades a constant factor (re-iterating frame data per strip)
/// for the strip-size memory bound, which lets the strategy run on
/// memory-constrained hosts that can't afford full-canvas drizzle at
/// Phase 2 scales.</para>
/// </summary>
public sealed class TilePipelinedDrizzleStrategy : IIntegrationStrategy
{
    /// <summary>Output strip height in canvas rows. Matches
    /// <see cref="TilePipelinedStrategy.StripHeight"/> so the cost-model
    /// strip working-set comparison is apples-to-apples between the two
    /// tile-pipelined strategies.</summary>
    public const int StripHeight = 256;

    private readonly IntegrationCostModel _costs;
    private readonly int _minFrameCount;

    public TilePipelinedDrizzleStrategy(IntegrationCostModel? costs = null, int minFrameCount = DrizzleStrategy.AutoSelectMinFrameCount)
    {
        _costs = costs ?? new IntegrationCostModel();
        _minFrameCount = minFrameCount;
    }

    public IntegrationStrategyKind Kind => IntegrationStrategyKind.TilePipelinedDrizzle;

    /// <summary>Same drizzle-fidelity discount as <see cref="DrizzleStrategy"/>
    /// (0.92): the algorithm is identical, only the memory layout differs,
    /// so per-pixel output values must agree byte-for-byte between the two
    /// at the same pixfrac.</summary>
    public double FidelityScore => 0.92;

    public bool SupportsLiveStacking => false;

    public StrategyFit Evaluate(IntegrationProbe probe, ResourceBudget budget)
    {
        // RAM profile: calibrated-frame cache (1-channel raw, sized to N)
        // plus one strip's flux/weight working set plus the canvas-sized
        // master + coverage planes plus an in-flight calibrated raw.
        // Floor (when cache is empty under memory pressure) drops to just
        // the strip + master + in-flight; the cache evict + re-decode
        // path keeps the strategy running on hosts where the optimistic
        // target would otherwise bust.
        var calibratedFrameBytes = (long)probe.FrameWidth * probe.FrameHeight * sizeof(float);
        var cacheRam = calibratedFrameBytes * probe.FrameCount;
        var stripBytes = (long)probe.CanvasWidth * StripHeight * 3 * sizeof(float) * 2; // flux + weight, 3 channels
        var masterRam = (long)probe.CanvasWidth * probe.CanvasHeight * 3 * sizeof(float) * 2; // master + coverage canvases
        var inFlightRam = calibratedFrameBytes;
        var ram = cacheRam + stripBytes + masterRam + inFlightRam;
        var floor = stripBytes + masterRam + inFlightRam;

        // Same gating as DrizzleStrategy: RGGB-only + min frame count.
        // See DrizzleStrategy.Evaluate for the rationale on both gates.
        if (probe.SensorType != SensorType.RGGB)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: TimeSpan.Zero,
                Rationale: $"TilePipelinedDrizzle requires SensorType.RGGB (got {probe.SensorType})")
            { FloorRamBytes = floor };
        }
        if (probe.FrameCount < _minFrameCount)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: TimeSpan.Zero,
                Rationale: $"TilePipelinedDrizzle needs >= {_minFrameCount} matched frames for robust R/B coverage (got {probe.FrameCount})")
            { FloorRamBytes = floor };
        }
        if (floor > budget.AllowedRam(probe))
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: TimeSpan.Zero,
                Rationale: $"TilePipelinedDrizzle floor ({Format.GB(floor)}) exceeds budget ({Format.GB(budget.AllowedRam(probe))})")
            { FloorRamBytes = floor };
        }

        // Wall-time: same components as DrizzleStrategy (no debayer / no
        // warp / no reject-combine). The strip iteration adds a small
        // overhead for inverse-projection bounds + strip allocation;
        // covered by the same forward-project per-pixel constant. With
        // full cache the per-frame work is touched exactly once
        // regardless of strip count.
        var loadCalibrate = _costs.LoadAndCalibrateAllFrames(probe);
        var projectMs = (double)probe.FrameWidth * probe.FrameHeight * probe.FrameCount * _costs.CpuNsPerDrizzleProjectPixel / 1e6;
        var eta = loadCalibrate + TimeSpan.FromMilliseconds(projectMs);

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: 0,
            EstimatedDuration: eta,
            Rationale: $"strip-pipelined drizzle (target {Format.GB(ram)}, floor {Format.GB(floor)})")
        {
            FloorRamBytes = floor,
        };
    }

    public async ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct)
    {
        if (job.RawLightSources is null || job.Calibrator is null)
        {
            throw new InvalidOperationException(
                "TilePipelinedDrizzleStrategy requires job.RawLightSources + job.Calibrator. " +
                "The pipeline wires these when --strategy TilePipelinedDrizzle is selected.");
        }

        var sources = job.RawLightSources;
        var calibrator = job.Calibrator;
        var n = sources.Count;
        if (n == 0)
        {
            throw new InvalidOperationException("TilePipelinedDrizzleStrategy: RawLightSources is empty.");
        }

        var options = job.DrizzleOptions ?? new DrizzleOptions();
        if (options.OutputScale != DrizzleOptions.OutputScalePhase1)
        {
            throw new NotSupportedException(
                $"DrizzleOptions.OutputScale={options.OutputScale} is Phase 2; " +
                $"Phase 1 only supports OutputScale={DrizzleOptions.OutputScalePhase1} (1.0x grid).");
        }

        var canvasW = job.CanvasWidth;
        var canvasH = job.CanvasHeight;
        if (canvasW <= 0 || canvasH <= 0)
        {
            throw new InvalidOperationException(
                $"TilePipelinedDrizzleStrategy needs CanvasWidth/Height on the job (got {canvasW}x{canvasH}); " +
                "the pipeline computes these from the union-BB transform set.");
        }

        var pixfrac = options.Pixfrac;
        var halfP = pixfrac * 0.5f;
        var swStrat = System.Diagnostics.Stopwatch.StartNew();

        // Same bad-pixel-mask plumbing as DrizzleStrategy -- single-channel
        // raw Bayer plane, mask 0 is the right read.
        var badPixelMask = job.BadPixelMask is { Length: > 0 } m ? m[0] : default;
        var hasBadPixelMask = job.BadPixelMask is { Length: > 0 };

        // ---------------- Pass 1: load + calibrate every frame, cache ----------------
        // Cache calibrated 1-channel Bayer (~36 MB / frame at 3008^2) so
        // pass 2 can re-iterate per strip without re-reading FITS. Same
        // FrameCache + cap policy as TilePipelinedStrategy: roomy hosts
        // hold all N; tight hosts evict and re-decode on miss in pass 2.
        ct.ThrowIfCancellationRequested();
        var firstCalibrated = DecodeCalibrate(sources[0], calibrator);
        var calibratedBytes = (long)firstCalibrated.Width * firstCalibrated.Height * firstCalibrated.ChannelCount * sizeof(float);
        var cache = new FrameCache(n, FrameCache.DecideCacheCap(n, calibratedBytes));
        var refMeta = firstCalibrated.ImageMeta;
        var sourceMaxValue = firstCalibrated.MaxValue;
        // Cache the first frame BEFORE the loop so pass-2 can refer to it
        // through the cache uniformly with the other frames -- no special
        // "is this the first one?" branch.
        cache.Set(0, firstCalibrated);
        job.Progress?.Report(new IntegrationProgress(IntegrationPhase.LoadingFrames, 1, n, swStrat.Elapsed));

        for (var f = 1; f < n; f++)
        {
            ct.ThrowIfCancellationRequested();
            var calibrated = DecodeCalibrate(sources[f], calibrator);
            cache.Set(f, calibrated);
            job.Progress?.Report(new IntegrationProgress(IntegrationPhase.LoadingFrames, f + 1, n, swStrat.Elapsed));
        }

        // Bayer pattern is sensor geometry, not per-frame -- a meridian
        // flip changes the transform, not which physical filter sits over
        // each sensor pixel. If frames within a group reported different
        // BayerOffsetX/Y, they'd belong to different LightGroupKeys
        // upstream; mixing patterns within a group is a grouping bug, not
        // a runtime hazard. Cache once.
        var pattern = refMeta.SensorType.GetBayerPatternMatrix(refMeta.BayerOffsetX, refMeta.BayerOffsetY);
        var rawW = firstCalibrated.Width;
        var rawH = firstCalibrated.Height;

        // ---------------- Master + coverage canvases ----------------
        // Full-canvas accumulators for the FINAL output. Strip-local
        // flux/weight (below) fill these one strip at a time.
        var masterFlux = new float[3][,];
        var masterWeight = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            masterFlux[c] = new float[canvasH, canvasW];
            masterWeight[c] = new float[canvasH, canvasW];
        }

        // ---------------- Pass 2: strip-by-strip drizzle ----------------
        // Halo on projected source rect: drizzle's drop covers a unit cell
        // at most (pixfrac <= 1) plus rotation can land a source pixel's
        // drop on a strip cell up to ~sqrt(2)/2 + 0.5 ≈ 1.2 px outside the
        // axis-aligned inverse projection. 2 px of cushion handles that
        // plus floating-point rounding without iterating an unbounded
        // halo.
        const int projectionHalo = 2;
        var stripsTotal = (canvasH + StripHeight - 1) / StripHeight;
        var stripIdx = 0;

        for (var stripY0 = 0; stripY0 < canvasH; stripY0 += StripHeight)
        {
            ct.ThrowIfCancellationRequested();
            var stripH = Math.Min(StripHeight, canvasH - stripY0);
            var stripRect = new Rectangle(0, stripY0, canvasW, stripH);

            // Allocate strip-local flux/weight. Zero-initialised by .NET so
            // uncovered cells stay 0 -- the FinaliseDivide pass converts
            // those to NaN. We deliberately do NOT pool these: a 256x4032x3
            // pair (~24 MB) is fast to allocate vs the wallclock of an N-
            // frame deposit pass over the strip, and pooling would couple
            // the two strategies more tightly than it's worth.
            var stripFlux = new float[3][,];
            var stripWeight = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                stripFlux[c] = new float[stripH, canvasW];
                stripWeight[c] = new float[stripH, canvasW];
            }

            // Strip-local accumulators address [stripH x canvasW]. The
            // kernel takes the accumulator's canvas-coord origin via
            // (xStart, yStart) -- (0, stripY0) for this strip -- so
            // canvas cell (yc, xc) lands at stripFlux[c][yc - stripY0, xc].
            for (var f = 0; f < n; f++)
            {
                ct.ThrowIfCancellationRequested();
                if (!cache.TryGet(f, out var calibrated))
                {
                    // Tier miss: re-decode + recalibrate. Re-add to cache;
                    // the cache may evict another frame to make room.
                    calibrated = DecodeCalibrate(sources[f], calibrator);
                    cache.Set(f, calibrated);
                }

                var transform = sources[f].TransformToCanvas;
                var sourceRect = CanvasGeometry.ProjectCanvasRectToSourceRect(stripRect, transform, rawW, rawH, projectionHalo);
                if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
                {
                    // Frame's source pixels can't reach this strip -- skip
                    // entirely.
                    continue;
                }

                DrizzleKernel.IterateAndDeposit(
                    calibrated, transform, pattern, halfP,
                    stripFlux, stripWeight,
                    xStart: 0, xEnd: canvasW,
                    yStart: stripY0, yEnd: stripY0 + stripH,
                    sourceRect, badPixelMask, hasBadPixelMask);
            }

            // Copy strip-local accumulators into the canvas-sized master
            // and coverage planes. Row-wise memcpy via BlockCopy: faster
            // than nested-for on float[,] and keeps the strip life short.
            for (var c = 0; c < 3; c++)
            {
                var srcF = stripFlux[c];
                var srcW2 = stripWeight[c];
                var dstF = masterFlux[c];
                var dstW = masterWeight[c];
                for (var dy = 0; dy < stripH; dy++)
                {
                    Buffer.BlockCopy(srcF, dy * canvasW * sizeof(float),
                        dstF, (stripY0 + dy) * canvasW * sizeof(float),
                        canvasW * sizeof(float));
                    Buffer.BlockCopy(srcW2, dy * canvasW * sizeof(float),
                        dstW, (stripY0 + dy) * canvasW * sizeof(float),
                        canvasW * sizeof(float));
                }
            }

            stripIdx++;
            job.Progress?.Report(new IntegrationProgress(IntegrationPhase.Integrating, stripIdx, stripsTotal, swStrat.Elapsed));
        }

        // ---------------- Finalise: flux/weight, NaN where uncovered ----------------
        // Same post-processing as DrizzleStrategy via the shared helper.
        // Master and coverage map carry the FITS contract DrizzleStrategy
        // established (master in [0, 1], coverage as the rejection-map
        // sidecar, uncovered cells reported as "rejections" for the
        // existing writer gate).
        var invMax = sourceMaxValue > 0f ? 1f / sourceMaxValue : 1f;
        var totalCells = (long)canvasH * canvasW * 3;
        var coveredCells = DrizzleKernel.FinaliseDivide(masterFlux, masterWeight, invMax, canvasH, canvasW);

        var master = new Image(
            data: masterFlux,
            bitDepth: BitDepth.Float32,
            maxValue: 1.0f,
            minValue: 0f,
            pedestal: 0f,
            imageMeta: refMeta);
        var coverageMap = new Image(
            data: masterWeight,
            bitDepth: BitDepth.Float32,
            maxValue: 1.0f,
            minValue: 0f,
            pedestal: 0f,
            imageMeta: refMeta);

        var uncovered = totalCells - coveredCells;
        return new IntegrationResult(
            Master: master,
            RejectionMap: coverageMap,
            FrameCount: n,
            TotalRejections: uncovered,
            MeanRejectionRate: (double)uncovered / totalCells);
    }

    /// <summary>Load + calibrate, no debayer. Same contract as
    /// <see cref="TilePipelinedStrategy"/>'s private helper -- duplicated
    /// rather than promoted to a shared internal because it's a trivial
    /// 5-line wrapper around <see cref="Image.TryReadFitsFile"/> + the
    /// calibrator and DRYing it would couple the two strategies through
    /// a third file purely on convenience grounds.</summary>
    private static Image DecodeCalibrate(RawLightSource source, Calibrator calibrator)
    {
        if (!Image.TryReadFitsFile(source.Path, out var raw))
        {
            throw new InvalidDataException($"TilePipelinedDrizzleStrategy: failed to read raw FITS at {source.Path}");
        }
        return calibrator.Apply(raw);
    }
}
