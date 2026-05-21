namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Marker interface for image enhancers that sharpen the stellar component of
/// an astronomical frame -- the stars-only plate produced by
/// <c>Source - Starless</c> (where <c>Starless</c> comes from an
/// <see cref="IStarRemover"/>). Tightens the per-star PSF without altering
/// the underlying nebula / galaxy structure.
/// </summary>
/// <remarks>
/// <para>Conceptually pairs with <see cref="INonStellarDeconvolver"/>: in the
/// canonical <c>SharpenPipeline</c> the stars-only plate goes through this
/// enhancer while the starless plate goes through the deconvolver, and the
/// two are recombined at the end. Both are valid standalone enhancers and
/// can be applied independently.</para>
///
/// <para>Domain semantics: linear-units in / linear-units out (modulo the
/// MTF stretch dance) and -- unlike <see cref="IStarRemover"/> --
/// well-approximated as a linear-domain function of the input. The
/// transformation is local detail enhancement; no histogram macro-shape
/// changes. Chains cleanly with other linear-domain processing.</para>
/// </remarks>
public interface IStellarSharpener : IImageEnhancer
{
}
