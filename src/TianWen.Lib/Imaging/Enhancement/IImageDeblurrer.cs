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
/// <para>RC-Astro-only today: the role is backed solely by the bxt CLI, and TianWen has
/// no whole-frame deblur model. <see cref="SharpenPipeline.CanonicalProgram"/> puts it
/// first wherever it serves; the split program without it
/// (<see cref="SharpenRequest.Canonical"/>) does not use it.</para>
/// </remarks>
public interface IImageDeblurrer : IImageEnhancer
{
}
