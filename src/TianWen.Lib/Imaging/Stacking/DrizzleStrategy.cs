using System;
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
        // Opt-in only. The selector still routes here when the user passes
        // --strategy BayerDrizzle (preferred-override bypasses CanRun).
        return new StrategyFit(
            CanRun: false,
            EstimatedRamBytes: probe.OutputRamBytes * 2, // flux + weight planes
            EstimatedDiskBytes: 0,
            EstimatedDuration: TimeSpan.Zero,
            Rationale: "BayerDrizzle is opt-in only (--strategy BayerDrizzle)");
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

        await foreach (var frame in job.RawBayerFrames(ct).WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (refMeta is null)
            {
                refMeta = frame.RawCfa.ImageMeta;
                sourceMaxValue = frame.RawCfa.MaxValue;
            }

            var meta = frame.RawCfa.ImageMeta;
            var pattern = meta.SensorType.GetBayerPatternMatrix(meta.BayerOffsetX, meta.BayerOffsetY);
            var transform = frame.TransformToCanvas;
            var raw = frame.RawCfa;
            var srcW = raw.Width;
            var srcH = raw.Height;

            for (var ySrc = 0; ySrc < srcH; ySrc++)
            {
                for (var xSrc = 0; xSrc < srcW; xSrc++)
                {
                    var v = raw[0, ySrc, xSrc];
                    if (float.IsNaN(v)) continue;

                    // Forward-warp pixel CENTER to canvas space.
                    // The +0.5 / -0.5 dance converts integer pixel indices
                    // to/from centre-of-pixel coordinates so transform's
                    // identity at (0,0) maps the source (0,0) pixel onto
                    // the canvas (0,0) pixel.
                    var p = Vector2.Transform(new Vector2(xSrc + 0.5f, ySrc + 0.5f), transform);
                    var xW = p.X - 0.5f;
                    var yW = p.Y - 0.5f;

                    // Square drop on the output grid.
                    var xLo = xW - halfP;
                    var xHi = xW + halfP;
                    var yLo = yW - halfP;
                    var yHi = yW + halfP;

                    var x0 = Math.Max(0, (int)MathF.Floor(xLo));
                    var x1 = Math.Min(canvasW - 1, (int)MathF.Ceiling(xHi) - 1);
                    var y0 = Math.Max(0, (int)MathF.Floor(yLo));
                    var y1 = Math.Min(canvasH - 1, (int)MathF.Ceiling(yHi) - 1);
                    if (x1 < x0 || y1 < y0) continue;

                    var ch = pattern[ySrc & 1, xSrc & 1];
                    var fluxCh = flux[ch];
                    var weightCh = weight[ch];

                    for (var yc = y0; yc <= y1; yc++)
                    {
                        var dy = MathF.Min(yc + 1f, yHi) - MathF.Max(yc, yLo);
                        if (dy <= 0f) continue;
                        for (var xc = x0; xc <= x1; xc++)
                        {
                            var dx = MathF.Min(xc + 1f, xHi) - MathF.Max(xc, xLo);
                            if (dx <= 0f) continue;
                            var area = dx * dy;
                            fluxCh[yc, xc] += v * area;
                            weightCh[yc, xc] += area;
                        }
                    }
                }
            }

            frameCount++;
        }

        if (frameCount == 0 || refMeta is null)
        {
            throw new InvalidOperationException("DrizzleStrategy received zero frames from RawBayerFrames.");
        }

        // Final divide -- master[c, y, x] = (flux / weight) / sourceMaxValue;
        // NaN where weight is zero. Dividing by sourceMaxValue puts the
        // master into [0, 1] (matching the Integrator's per-frame
        // normalization output), so MasterPostProcessor's MaxValue=1.0
        // relabel pass and Image.Histogram's MaxValue<=1.0 branch both
        // see a self-consistent (range, label) pair. We reuse the flux
        // buffer in-place for the master since it's allocated at the
        // right shape; weight stays as the coverage map.
        var invMax = sourceMaxValue > 0f ? 1f / sourceMaxValue : 1f;
        long coveredCells = 0;
        var totalCells = (long)canvasH * canvasW * 3;
        for (var c = 0; c < 3; c++)
        {
            var f = flux[c];
            var w = weight[c];
            for (var y = 0; y < canvasH; y++)
            {
                for (var x = 0; x < canvasW; x++)
                {
                    var wv = w[y, x];
                    if (wv > 0f)
                    {
                        f[y, x] = f[y, x] / wv * invMax;
                        coveredCells++;
                    }
                    else
                    {
                        f[y, x] = float.NaN;
                    }
                }
            }
        }

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
