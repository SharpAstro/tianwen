using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Estimates the PSF radius of an image as a single scalar in the [0, 1]
/// "psf01" encoding the NAFNet conditional-PSF deconvolution models consume:
/// <c>psf01 = (log2(radius) - log2(1.0)) / (log2(8.0) - log2(1.0))</c>, where
/// <c>radius</c> is the empirical PSF half-width in pixels clamped to [1, 8].
/// </summary>
/// <remarks>
/// SetiAstroSuite Pro's <c>deep_nonstellar_sharp_conditional_psf_AI4.onnx</c>
/// takes this scalar as its second input and broadcasts it to a 4th input
/// channel internally -- the model uses it to pick the appropriate
/// deconvolution kernel. The chunk variant is provided for future SEP-style
/// per-chunk re-measurement; the v1 <see cref="HfdPsfEstimator"/> returns the
/// whole-image scalar from <see cref="EstimateAsync"/> for every chunk.
/// </remarks>
public interface IPsfEstimator
{
    /// <summary>
    /// Returns the whole-image psf01 scalar.
    /// </summary>
    Task<float> EstimateAsync(Image image, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a psf01 scalar specific to a chunk of the image. Callers that
    /// re-measure per chunk (capturing PSF variation across the field due to
    /// tilt, coma, mirror flop, etc.) can implement this; the default
    /// implementation delegates to the whole-image variant.
    /// </summary>
    Task<float> EstimateChunkAsync(Image image, int x0, int y0, int width, int height, CancellationToken cancellationToken = default)
        => EstimateAsync(image, cancellationToken);
}
