using System;
using TianWen.Lib.Geometry;
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
        // Per-frame, per-CFA-COLOUR normalisation -- the same mechanism every other integration
        // strategy applies (Integrator / TilePipelinedStrategy normalise each of a debayered frame's
        // R/G/B planes independently via Normalizer.ComputeStats' per-ChannelCount loop), now here
        // too via Normalizer.ComputeCfaStats/ApplyCfa: Red, Green and Blue photosites are each mapped
        // onto job.Options.NormalizationTarget with their OWN scale before deposit. A raw Bayer plane
        // is ChannelCount=1 (three colours sharing one array), so the per-channel discretion every
        // other strategy gets for free needs the CFA traversal instead.
        //
        // A single WHOLE-FRAME scalar (the first version of this fix, "fix(stacking): drizzle
        // normalises per frame") is not enough: it is dominated by green (2x the photosites of red or
        // blue) and cannot follow a per-colour sky drift -- measured on a real session, G background
        // fell 46% while R/G and B/G held within ~3%, a small but real chromatic drift the pooled
        // scalar cannot see. Combined with drizzle's uneven per-CFA-phase deposit weighting (measured
        // 13-30% per-frame variance -- see DrizzleKernel/StackingPipeline comments and
        // docs/architecture/stacking-render-pipeline.md), that residual per-colour drift still baked a
        // phase-locked colour bias into the master, just a smaller one than the unnormalised original.
        // Falls back to the previous unnormalised behaviour (scale by the frame's own UnitScaleDivisor
        // at the very end) only when a caller explicitly disables normalisation.
        //
        // This DOES change matched-star R/G, B/G colour ratios substantially relative to an unfixed or
        // single-scalar master (measured ~3.7x, ~1.6x) -- checked deliberately, and it is expected, not
        // a regression: a per-colour scale divides every pixel of that colour (star included) by that
        // colour's own sky level, which is exactly what Integrator/TilePipelinedStrategy's EXISTING
        // per-channel normalisation already does to every debayered master (confirmed: it washes
        // RgbBayerSyntheticFixture's R/G/B medians to EXACTLY 1.0000, not the raw gain ratio). Raw
        // camera colour was never meant to survive integration on EITHER path -- SPCC restores it
        // afterward, against real Gaia photometry, on the finished master. See
        // docs/architecture/stacking-render-pipeline.md § 1 for the full comparison.
        var applyNormalization = job.Options.ApplyNormalization;
        var normalizationTarget = job.Options.NormalizationTarget;
        var sourceMaxValue = 1.0f;
        // The first frame's pedestal, which an UNNORMALISED drizzle keeps in its data (every sample is
        // divided by sourceMaxValue and nothing is subtracted), so the master states it in those units.
        var sourcePedestal = 0f;
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
                // UnitScaleDivisor, not MaxValue: the canonical [0, 1] divisor, which prefers a
                // DECLARED full scale (ImageMeta.SensorFullScaleAdu, i.e. a FITS SATURATE card) over
                // the frame's own observed peak. A no-op for raw subs, which declare nothing and fall
                // back to the peak exactly as before. It matters for a frame whose peak is not its
                // full scale -- a per-frame starless plate, whose brightest pixel was a star that has
                // been removed, so its peak understates its scale by ~7x.
                sourceMaxValue = frame.RawCfa.UnitScaleDivisor;
                sourcePedestal = frame.RawCfa.Pedestal;
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
            // Normalise BEFORE deposit, exactly like TilePipelinedStrategy normalises its debayered-
            // but-not-yet-warped frame: stats are whole-frame (there is no canvas-space StatsRect to
            // restrict to here -- the raw CFA plane is still in SOURCE coordinates, pre-warp), and the
            // transform applies unchanged afterwards. Per-CFA-colour (not the pooled whole-plane
            // scalar) -- see the comment above this loop.
            // Unnormalised, the sky still has to agree across frames at every phase, so a caller that
            // keeps the linear scale (the dataset bake) shifts each colour onto one reference sky.
            var raw = applyNormalization
                ? Normalizer.ApplyCfa(frame.RawCfa, Normalizer.ComputeCfaStats(frame.RawCfa), normalizationTarget)
                : job.Options.DrizzleSkyReference is { } skyReference
                    ? Normalizer.OffsetCfaToReference(frame.RawCfa, Normalizer.ComputeCfaStats(frame.RawCfa), skyReference)
                    : frame.RawCfa;
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
                new PixelRect(0, 0, srcW, srcH),
                badPixelMask, hasBadPixelMask);

            frameCount++;
        }

        if (frameCount == 0 || refMeta is null)
        {
            throw new InvalidOperationException("DrizzleStrategy received zero frames from RawBayerFrames.");
        }

        // Final divide -- master[c, y, x] = flux / weight, NaN where weight is zero. The kernel
        // helper mutates flux in place and returns the covered-cell count for the coverage-rate
        // stat. Shared with TilePipelinedDrizzleStrategy so the post-processing path is identical
        // regardless of memory layout. When normalisation ran, every deposited sample already sits
        // near [0, 1] (median ~= normalizationTarget) -- dividing by sourceMaxValue here as well
        // would double-scale, so invMax is the identity in that case; it only falls back to the raw
        // ADU-to-unit conversion when a caller explicitly disabled normalisation.
        var invMax = applyNormalization
            ? 1f
            : sourceMaxValue > 0f ? 1f / sourceMaxValue : 1f;
        var totalCells = (long)canvasH * canvasW * 3;
        var coveredCells = DrizzleKernel.FinaliseDivide(flux, weight, invMax, canvasH, canvasW);

        var master = IntegratedMaster.Labelled(new Image(
            data: flux,
            bitDepth: BitDepth.Float32,
            maxValue: 1.0f,
            minValue: 0f,
            pedestal: sourcePedestal * invMax,
            imageMeta: refMeta.Value), normalised: applyNormalization);

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
            MeanRejectionRate: (double)uncovered / totalCells,
            RejectionMapIsCoverage: true);
    }
}
