using System;
using System.Drawing;
using TianWen.Lib;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Route-only async helper for the viewer's AI "Enhance" action, mirroring
/// <see cref="ViewerActions.PlateSolveAsync"/>: runs the <see cref="SharpenPipeline"/> on the
/// current document's linear image off the render thread, surfaces per-step progress on the
/// viewer status line, and returns a fresh enhanced document (the caller swaps it in on the
/// render thread). Lives in <c>TianWen.UI.Abstractions</c> -- it only touches
/// <see cref="SharpenPipeline"/> (which is in <c>TianWen.Lib</c>), never the ONNX/RC-Astro
/// concretions, so the abstraction layering holds.
/// </summary>
public static class EnhanceActions
{
    /// <summary>
    /// Enhances <paramref name="source"/>'s linear image with <paramref name="pipeline"/> and
    /// adopts the result into a new <see cref="AstroImageDocument"/> (carrying over WCS + file
    /// provenance). Returns <c>null</c> on cancellation, failure, or an empty result -- in every
    /// such case <see cref="ViewerState.StatusMessage"/> is set to a human-readable reason.
    /// Does NOT mutate <see cref="ViewerState.IsEnhancing"/>: the controller owns that lifecycle
    /// flag (set when the task is kicked, cleared on the render thread when the result is applied).
    /// </summary>
    public static async Task<AstroImageDocument?> EnhanceAsync(
        AstroImageDocument source,
        ViewerState state,
        SharpenPipeline pipeline,
        EnhanceOptions options,
        DebayerAlgorithm debayerAlgorithm,
        Rectangle? crop = null,
        CancellationToken cancellationToken = default)
    {
        // A crop is applied to the INPUT, not left for the display to hide afterwards. Every enhancer
        // here is a spatial model, and the canvas ring is exact zero -- a hard cliff a CNN reads as
        // structure and smears inward, which is what a border still visible after a gradient correction
        // actually is. Note the pipeline cannot be told to ignore it instead: SharpenPipeline fills
        // non-finite samples with the channel mean at its boundary (SAS ONNX and RC-Astro both
        // normalise without NaN awareness), so masking is not available, and exact zeros pass through
        // untouched.
        //
        // The result IS the crop: smaller pixels, a WCS translated to match, and SourceCrop recording
        // where it came from. Costs one full-size copy of the kept region, against a pipeline that runs
        // for a minute and a half.
        var input = source.UnstretchedImage;
        var wcs = source.Wcs;
        if (crop is { } region)
        {
            input = input.Crop(region);
            wcs = wcs?.CroppedTo(region.X, region.Y);
        }

        // BlurX-first program when a deblurrer is registered (RC-Astro), else the SAS-shaped
        // canonical -- the same selection MasterPostProcessor makes, via the shared factories
        // (single source of truth for the step program). Linear in / linear out: the viewer
        // applies its own stretch, so no final stretch step is included.
        var request = pipeline.SupportsDeblur
            ? SharpenRequest.DeblurFirst(input)
            : SharpenRequest.Canonical(input);

        // Per-step progress -> viewer status line. Runs on the background thread; these scalar
        // writes to ViewerState are the only writers during the run and the render thread reads
        // snapshots (a stale read just shows a slightly old %), matching the load-task pattern.
        // SYNCHRONOUS on purpose: Progress<T> posts each report to the pool (there is no context inside
        // Task.Run), so one queued during the run can land after the final status below and leave the
        // line reading "Enhancing: ..." on a finished enhance. See SynchronousProgress.
        var progress = new SynchronousProgress<EnhanceProgress>(p =>
        {
            var overall = p.StepCount > 0
                ? (p.StepIndex + Math.Clamp(p.StepPercent, 0f, 1f)) / p.StepCount * 100f
                : 0f;
            state.EnhanceProgressPct = overall;
            state.StatusMessage = $"Enhancing: {p.StepName} ({overall:F0}%)";
            state.NeedsRedraw = true;
        });

        SharpenResult result;
        try
        {
            result = await pipeline.ProcessAsync(request, options, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            state.StatusMessage = "Enhance cancelled";
            return null;
        }
        catch (Exception ex)
        {
            state.StatusMessage = $"Enhance failed: {StatusText.FromException(ex)}";
            return null;
        }

        if (result.Final is not { } enhanced)
        {
            state.StatusMessage = "Enhance produced no image";
            return null;
        }

        // Adopt the enhanced linear image into a fresh document (normalises to [0,1] + computes
        // stretch stats). AdoptImageAsync consumes `enhanced`, which we own outright (a pipeline
        // output, never the caller's buffer). WCS + provenance path carry over so overlays/coords
        // still resolve on the enhanced view.
        //
        // WithZeroPedestal FIRST, because a gradient-corrected frame's pedestal no longer describes
        // its pixels: the AI corrector adds its model's median back per plane and accumulates that
        // level onto the pedestal field, so the enhanced image can carry a pedestal far above its own
        // median (0.019361 against 0.00072 on the 10P drizzle master -- twenty-five times over). The
        // stretch subtracts the pedestal from the median to place its shadow point, so every channel
        // went negative and the frame rendered as a flat crimson wash. The stats are computed by the
        // adopt, so the rewrap has to happen on the way IN rather than at render time. Same call the
        // stacking preview has always made; it shares the arrays, so this costs no pixels.
        var doc = await AstroImageDocument.AdoptImageAsync(
            enhanced.WithZeroPedestal(), debayerAlgorithm, wcs, source.FilePath, crop, cancellationToken)
            .ConfigureAwait(false);
        // Both canonical programs run a GradientCorrectionStep, so the background of what comes back
        // has been flattened and levelled. The display has to know: an Auto stretch would otherwise
        // resolve to Unlinked and neutralise an already-neutral background per channel, which fits
        // three curves to the noise between them (P30's crimson frame).
        doc.MarkBackgroundExtracted();
        state.StatusMessage = $"Enhanced ({(pipeline.SupportsDeblur ? "BlurX-first" : "SAS")})";
        return doc;
    }
}
