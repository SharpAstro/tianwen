using System;
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
        CancellationToken cancellationToken = default)
    {
        // BlurX-first program when a deblurrer is registered (RC-Astro), else the SAS-shaped
        // canonical -- the same selection MasterPostProcessor makes, via the shared factories
        // (single source of truth for the step program). Linear in / linear out: the viewer
        // applies its own stretch, so no final stretch step is included.
        var request = pipeline.SupportsDeblur
            ? SharpenRequest.DeblurFirst(source.UnstretchedImage)
            : SharpenRequest.Canonical(source.UnstretchedImage);

        // Per-step progress -> viewer status line. Runs on the background thread; these scalar
        // writes to ViewerState are the only writers during the run and the render thread reads
        // snapshots (a stale read just shows a slightly old %), matching the load-task pattern.
        var progress = new Progress<EnhanceProgress>(p =>
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
            state.StatusMessage = $"Enhance failed: {ex.Message}";
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
        var doc = await AstroImageDocument.AdoptImageAsync(
            enhanced, debayerAlgorithm, source.Wcs, source.FilePath, cancellationToken).ConfigureAwait(false);
        state.StatusMessage = $"Enhanced ({(pipeline.SupportsDeblur ? "BlurX-first" : "SAS")})";
        return doc;
    }
}
