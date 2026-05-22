using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Marker interface for image enhancers that estimate and remove a smooth
/// background gradient (light pollution wash, optical vignette, sky glow
/// asymmetries) from a linear astro frame. Output is the gradient-corrected
/// image; callers that also want the estimated background surface (for
/// diagnostic / inspection) call <see cref="EnhanceAndEstimateBackgroundAsync"/>
/// instead.
/// </summary>
/// <remarks>
/// <para>Sits at the head of the AI sharpen pipeline per Frank Sackenheim's
/// canonical order: gradient correction -> star removal -> stellar sharpening
/// / non-stellar deconv / denoise -> stretch. Running gradient correction
/// AFTER star removal works in principle but typically yields worse results
/// because the in-painted starless plate is itself a smoothed estimate of
/// background+structure, and gradient solvers want to see the raw (post-
/// calibration, pre-stretch) photon counts.</para>
///
/// <para>Domain semantics: linear-units in / linear-units out. The correction
/// is well-approximated as a linear-domain function (a subtraction of a
/// per-pixel offset surface). The output's overall brightness is preserved
/// by adding back <c>mean(background)</c> -- i.e. the gradient SHAPE is
/// removed, not the absolute sky level.</para>
///
/// <para>Concrete implementations include AI models (GraXpert BGE,
/// Steffenhir/GraXpert MIT-licensed ONNX trained on synthetic gradients)
/// and classical algorithms (polynomial / RBF / spline / kriging over
/// auto-sampled background patches). The interface lets either back the
/// pipeline so callers can swap without code changes.</para>
/// </remarks>
public interface IGradientCorrector : IImageEnhancer
{
    /// <summary>
    /// Variant of <see cref="IImageEnhancer.EnhanceAsync"/> that also returns
    /// the estimated background surface when the impl tracks one explicitly
    /// (AI models, polynomial / RBF / spline fits). Default impl runs
    /// <see cref="IImageEnhancer.EnhanceAsync"/> and returns the background
    /// as <c>null</c> -- so callers using the variant on a corrector that
    /// doesn't expose its surface get a graceful "no diagnostic available"
    /// signal rather than a thrown exception.
    /// </summary>
    /// <remarks>
    /// <para>The returned background, when non-null, is in the source data's
    /// linear-units coordinate space (same as the input image). Subtracting
    /// it from the source approximates the corrected output up to the
    /// implementation's brightness-preservation offset (see implementation
    /// docs for exact semantics).</para>
    ///
    /// <para>Caller owns disposal of both returned Images -- call
    /// <see cref="Image.Release"/> on each when done, including the
    /// background even when only the corrected output is needed downstream.</para>
    /// </remarks>
    async Task<(Image Corrected, Image? Background)> EnhanceAndEstimateBackgroundAsync(
        Image input, CancellationToken cancellationToken = default)
        => (await EnhanceAsync(input, cancellationToken), null);
}
