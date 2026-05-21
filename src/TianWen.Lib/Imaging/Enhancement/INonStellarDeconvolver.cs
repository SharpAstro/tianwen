namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Marker interface for image enhancers that deconvolve the non-stellar
/// component of an astronomical frame -- a starless plate containing only
/// nebula / galaxy / dust / sky structure. PSF-conditional NAFNet: the
/// network takes both the image and a scalar PSF descriptor and reduces
/// the corresponding blur kernel.
/// </summary>
/// <remarks>
/// <para>PSF measurement is the impl's responsibility (typically via an
/// injected <see cref="IPsfEstimator"/>): take the median FWHM across
/// detected stars, halve to a radius, log2-encode into <c>[0, 1]</c> over
/// the training range <c>[1, 8]</c> px. The encoded scalar is broadcast and
/// concatenated as a 4th input channel inside the network.</para>
///
/// <para>Pairs with <see cref="IStellarSharpener"/> in the canonical
/// <c>SharpenPipeline</c>: the starless plate goes through deconvolution
/// while the stars-only plate goes through stellar sharpening, then both
/// recombine. Standalone use is also valid -- deconvolve any pre-starless
/// image.</para>
///
/// <para>Domain semantics: like stellar sharpening, this is a local
/// detail-preserving transformation -- linear-units in / linear-units out
/// AND well-approximated as a linear-domain function of the input. Chains
/// cleanly with other linear-domain processing.</para>
/// </remarks>
public interface INonStellarDeconvolver : IImageEnhancer
{
}
