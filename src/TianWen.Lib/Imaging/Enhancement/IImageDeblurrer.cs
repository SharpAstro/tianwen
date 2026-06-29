namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Full-image deconvolution / deblurring -- sharpens BOTH the stellar and the
/// non-stellar structure of the source frame in a single pass, the way RC-Astro
/// BlurXTerminator is used in a PixInsight OSC flow: run on the linear image
/// BEFORE star removal so the stars are tightened in place.
/// </summary>
/// <remarks>
/// <para>This is deliberately distinct from <see cref="INonStellarDeconvolver"/>,
/// which operates on the already-starless plate. A deblurrer tightens stars too,
/// so the downstream stars-only plate needs no separate stellar sharpening --
/// which is why the <c>DeblurFirst</c> canonical drops
/// <see cref="IStellarSharpener"/> entirely.</para>
///
/// <para>RC-Astro-only: the SETI Astro AI4 models have no full-image deblur (they
/// use the remove-stars -> sharpen-the-stars-plate split instead), so this role
/// is backed solely by the bxt CLI. The SAS-shaped canonical
/// (<see cref="SharpenRequest.Canonical"/>) does not use it.</para>
/// </remarks>
public interface IImageDeblurrer : IImageEnhancer
{
}
