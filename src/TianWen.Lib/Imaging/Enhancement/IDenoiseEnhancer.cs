namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Marker interface for image enhancers that suppress per-pixel noise --
/// shot, read, and thermal. Typically applied to the starless plate AFTER
/// <see cref="INonStellarDeconvolver"/> has run (PixInsight workflow: deconv
/// amplifies high-frequency content including noise; a noise reducer then
/// cleans up the amplified grain without re-blurring nebula detail).
/// </summary>
/// <remarks>
/// <para>Like <see cref="IStellarSharpener"/> and
/// <see cref="INonStellarDeconvolver"/>, this is a local detail-preserving
/// transformation: linear-units in / linear-units out AND well-approximated
/// as a linear-domain function of the input. Chains cleanly with other
/// linear-domain processing.</para>
///
/// <para>In the canonical <c>SharpenPipeline</c> the denoiser runs on the
/// post-deconvolution starless plate (or the raw starless plate when deconv
/// is disabled). Stand-alone use against any frame is also valid.</para>
/// </remarks>
public interface IDenoiseEnhancer : IImageEnhancer
{
}
