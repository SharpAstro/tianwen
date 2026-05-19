using System;
using System.Drawing;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Bayer drizzle (Fruchter &amp; Hook 2002 style) -- forward-projects each
/// raw CFA pixel as a square "drop" onto the per-channel output grid with
/// weighted accumulation, then divides flux by total weight. The key
/// distinguishing property is what it AVOIDS: no AHD / VNG / bilinear
/// debayer interpolation. Each Bayer sample lands in its own colour
/// channel only (R, G, or B per <see cref="SensorType.GetBayerPatternMatrix"/>);
/// the "missing" R value at a G Bayer position is filled in by real R
/// measurements from other frames whose dither put a real R Bayer cell at
/// that same sky position. No channel value is ever interpolated -- every
/// master pixel's R, G, and B come from actual Bayer samples across the
/// stack.
///
/// <para>This is meaningful even at <see cref="DrizzleOptions.OutputScale"/>
/// = 10 (1.0x, same grid as the reference frame): the standard
/// calibrate-debayer-warp-stack path runs each frame through AHD's
/// gradient-based colour interpolation, which guesses 2/3 of every pixel's
/// channel values and introduces chromatic fringes near star edges + false-
/// colour speckle in noise. Drizzle skips that step entirely, so colour
/// fidelity is bounded by Bayer sample SNR, not by interpolation kernel
/// quality. Phase 2 will lift <see cref="DrizzleOptions.OutputScale"/> to
/// 20 (2.0x) and layer sub-Bayer resolution recovery on top of the existing
/// colour-fidelity win.</para>
///
/// <para>At <see cref="DrizzleOptions.Pixfrac"/> = 1.0 each drop covers a
/// full unit cell, so per-pixel work is O(4) output cells touched per input
/// pixel and coverage is robust at moderate frame counts (60+ recommended
/// for clean R/B fills under typical sub-pixel dither).</para>
///
/// <para>Opt-in only: <see cref="Evaluate"/> reports <c>CanRun = false</c>
/// to keep <see cref="IntegrationStrategySelector"/> from auto-picking it.
/// The user override path (<c>--strategy BayerDrizzle</c>) still routes
/// here. The pipeline gates frame count + Bayer pattern before invoking
/// the strategy, so this class assumes valid inputs.</para>
/// </summary>
public sealed class DrizzleStrategy : IIntegrationStrategy
{
    /// <summary>Default minimum matched-frame count for drizzle auto-select.
    /// Below this the per-channel coverage (~25% per Bayer position) is too
    /// sparse to fill R/B reliably under sub-pixel dither, producing NaN-
    /// riddled R/B planes. Matches <see cref="DrizzleOptions.MinFrameCount"/>'s
    /// default; the pipeline can override this per-run by constructing
    /// the strategy with a custom <c>minFrameCount</c> so
    /// <c>--drizzle-min-frames N</c> drives both the auto-pick gate here
    /// and the pre-strategy gate in <see cref="StackingPipeline"/>
    /// uniformly.</summary>
    public const int AutoSelectMinFrameCount = 60;

    private readonly IntegrationCostModel _costs;
    private readonly int _minFrameCount;

    public DrizzleStrategy(IntegrationCostModel? costs = null, int minFrameCount = AutoSelectMinFrameCount)
    {
        _costs = costs ?? new IntegrationCostModel();
        _minFrameCount = minFrameCount;
    }

    public IntegrationStrategyKind Kind => IntegrationStrategyKind.BayerDrizzle;

    /// <summary>Phase-1 drizzle at scale=10 is roughly forward-bilinear --
    /// fidelity is comparable to <see cref="InRamAllFramesStrategy"/> (1.00)
    /// on Bayer inputs that would otherwise be debayered. We score it lower
    /// (0.92) so a future selector that scores it head-to-head doesn't
    /// jump to drizzle without explicit user opt-in.</summary>
    public double FidelityScore => 0.92;

    public bool SupportsLiveStacking => false;

    public StrategyFit Evaluate(IntegrationProbe probe, ResourceBudget budget)
    {
        // RAM profile: flux + weight planes are both canvas-sized at 3
        // channels (drizzle ignores ChannelCount and always emits RGB), plus
        // one in-flight calibrated 1-channel raw frame. No N-scaling on the
        // accumulators -- streaming drizzle holds everything full-canvas.
        var fluxWeightBytes = (long)probe.CanvasWidth * probe.CanvasHeight * 3 * sizeof(float) * 2;
        var inFlightRam = (long)probe.FrameWidth * probe.FrameHeight * sizeof(float); // 1-channel calibrated bayer
        var ram = fluxWeightBytes + inFlightRam;

        // CanRun gate: drizzle dispatches Bayer samples by physical filter
        // position, so it's RGGB-only. Below MinFrameCount the per-Bayer-
        // position coverage (~25% per channel) leaves swathes of R/B
        // uncovered -- worse than just running the standard path with AHD
        // interpolation. Selector gates this off rather than producing a
        // NaN-riddled master and surfacing the problem post-hoc.
        if (probe.SensorType != SensorType.RGGB)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: TimeSpan.Zero,
                Rationale: $"BayerDrizzle requires SensorType.RGGB (got {probe.SensorType})");
        }
        if (probe.FrameCount < _minFrameCount)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: TimeSpan.Zero,
                Rationale: $"BayerDrizzle needs >= {_minFrameCount} matched frames for robust R/B coverage (got {probe.FrameCount})");
        }
        if (ram > budget.AllowedRam(probe))
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: TimeSpan.Zero,
                Rationale: $"BayerDrizzle flux+weight planes ({Format.GB(ram)}) exceed budget ({Format.GB(budget.AllowedRam(probe))})");
        }

        // Wall-time = load+calibrate (every frame, full source) + forward-
        // project (every frame, full source, ~4 cells per pixel). No
        // debayer, no warp, no reject-combine -- those phases are
        // replaced by the drizzle deposit + final divide. Typical net is
        // 3-5x faster than the standard path on RGGB inputs.
        var loadCalibrate = _costs.LoadAndCalibrateAllFrames(probe);
        var projectMs = (double)probe.FrameWidth * probe.FrameHeight * probe.FrameCount * _costs.CpuNsPerDrizzleProjectPixel / 1e6;
        var eta = loadCalibrate + TimeSpan.FromMilliseconds(projectMs);

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: 0,
            EstimatedDuration: eta,
            Rationale: $"drizzle forward-project (no debayer; flux+weight {Format.GB(ram)})");
    }

    public async ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct)
    {
        if (job.RawBayerFrames is null)
        {
            throw new InvalidOperationException(
                "DrizzleStrategy needs IntegrationJob.RawBayerFrames; the pipeline " +
                "wires this on only when --strategy BayerDrizzle is selected.");
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
                $"DrizzleStrategy needs CanvasWidth/Height on the job (got {canvasW}x{canvasH}); " +
                "the pipeline computes these from the union-BB transform set.");
        }

        // Paired-plane accumulator: per-channel flux sum + per-channel
        // coverage-weight sum. The final master cell value is flux/weight;
        // cells with weight=0 land as NaN to mark uncovered regions.
        // 3 channels (R, G, B) baked in -- drizzle on a non-RGGB sensor
        // isn't meaningful and is gated out upstream.
        var flux = new float[3][,];
        var weight = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            flux[c] = new float[canvasH, canvasW];
            weight[c] = new float[canvasH, canvasW];
        }

        var pixfrac = options.Pixfrac;
        var halfP = pixfrac * 0.5f;
        ImageMeta? refMeta = null;
        // The source's MaxValue (camera full-well ADU, e.g. 4096 / 65535)
        // becomes our pre-normalization scale. We normalize the master to
        // [0, 1] at the end so MasterPostProcessor's MaxValue fix-up sees
        // a self-consistent pair (range = label = 1.0) -- the same
        // post-condition the InRamAllFrames / Integrator path satisfies
        // via per-frame normalization to NormalizationTarget=0.5.
        var sourceMaxValue = 1.0f;
        var frameCount = 0;
        // Bad-pixel mask is 1-channel (the raw Bayer plane is 1-channel
        // pre-debayer). We pick the first channel of the mask -- callers
        // wiring a multi-channel mask onto a Bayer drizzle producer
        // either have a bug or are mosaicking. Either way, channel 0 is
        // the right read for our raw CFA input.
        var badPixelMask = job.BadPixelMask is { Length: > 0 } m ? m[0] : default;
        var hasBadPixelMask = job.BadPixelMask is { Length: > 0 };

        await foreach (var frame in job.RawBayerFrames(ct).WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (refMeta is null)
            {
                refMeta = frame.RawCfa.ImageMeta;
                sourceMaxValue = frame.RawCfa.MaxValue;
            }

            var meta = frame.RawCfa.ImageMeta;
            var transform = frame.TransformToCanvas;
            // Bayer dispatch is direct: sensor pixel (ySrc, xSrc) has a
            // fixed physical Bayer color set by the sensor's filter array
            // -- the mount's pointing orientation doesn't change which
            // colour each pixel measured. The registration transform
            // handles the spatial mapping to canvas; the channel
            // assignment stays in sensor coordinates. (A previous
            // commit added an "M11 < 0 means flipped, swap BayerOffset"
            // path here -- that was incorrect and caused R/B channel
            // miscoloring in regions only post-flip frames covered.
            // Streaks seen in combined-flip drizzle masters come from
            // sub-pixel registration residual across the flip, not from
            // Bayer dispatch; the fix for those is per-frame
            // astrometric refinement, not a pattern swap.)
            var pattern = meta.SensorType.GetBayerPatternMatrix(meta.BayerOffsetX, meta.BayerOffsetY);
            var raw = frame.RawCfa;
            var srcW = raw.Width;
            var srcH = raw.Height;

            // Full-canvas deposit: iterate the entire source frame, accumulate
            // into the full-canvas flux/weight planes. The kernel handles the
            // chunked hot-pixel-mask fast path internally so the streaming
            // drizzle and the tile-pipelined variant share one deposit
            // implementation -- a previous version inlined the loop here and
            // diverged subtly from the half-pixel convention in the warp path,
            // producing dumbbell stars in combined-pier-flip output.
            DrizzleKernel.IterateAndDeposit(
                raw, transform, pattern, halfP,
                flux, weight,
                xStart: 0, xEnd: canvasW,
                yStart: 0, yEnd: canvasH,
                new Rectangle(0, 0, srcW, srcH),
                badPixelMask, hasBadPixelMask);

            frameCount++;
        }

        if (frameCount == 0 || refMeta is null)
        {
            throw new InvalidOperationException("DrizzleStrategy received zero frames from RawBayerFrames.");
        }

        // Final divide -- master[c, y, x] = (flux / weight) / sourceMaxValue;
        // NaN where weight is zero. The kernel helper mutates flux in place
        // and returns the covered-cell count for the coverage-rate stat.
        // Shared with TilePipelinedDrizzleStrategy so the post-processing
        // path is identical regardless of memory layout.
        var invMax = sourceMaxValue > 0f ? 1f / sourceMaxValue : 1f;
        var totalCells = (long)canvasH * canvasW * 3;
        var coveredCells = DrizzleKernel.FinaliseDivide(flux, weight, invMax, canvasH, canvasW);

        var master = new Image(
            data: flux,
            bitDepth: BitDepth.Float32,
            maxValue: 1.0f,
            minValue: 0f,
            pedestal: 0f,
            imageMeta: refMeta.Value);

        // Coverage map doubles as the rejection map: per-channel weight
        // accumulated; low-coverage cells are effectively "rejected" by
        // having less signal contribute to their average. We do NOT divide
        // weight by the number of frames here -- the raw weight sum is
        // more informative for QA (a cell with weight=10.0 saw 10 unit-
        // drops; weight<1.0 is suspiciously under-covered).
        var coverageMap = new Image(
            data: weight,
            bitDepth: BitDepth.Float32,
            maxValue: 1.0f,
            minValue: 0f,
            pedestal: 0f,
            imageMeta: refMeta.Value);

        // Drizzle has no kappa-sigma rejection by design -- the per-cell
        // weight IS the natural mask. We repurpose the IntegrationResult
        // rejection fields to expose drizzle coverage:
        //   TotalRejections    -> uncovered (weight==0) cells on the canvas
        //   RejectionMap       -> per-channel coverage weight buffer
        //   MeanRejectionRate  -> fraction of canvas cells uncovered
        // This piggybacks on the existing IntegrationFitsWriter contract:
        // the writer emits the rejection-map FITS when TotalRejections > 0,
        // which is exactly what we want -- the coverage map only lands on
        // disk when there are actually holes worth inspecting. A well-
        // dithered run with full coverage drops the side-car file entirely.
        var uncovered = totalCells - coveredCells;
        return new IntegrationResult(
            Master: master,
            RejectionMap: coverageMap,
            FrameCount: frameCount,
            TotalRejections: uncovered,
            MeanRejectionRate: (double)uncovered / totalCells);
    }
}
