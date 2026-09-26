using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// An enhancer that can say, at ENHANCE time, whether it will serve an input: a deferred RC-Astro role
/// answers by its licence, the in-house denoiser by the channel count and whether its weights are on
/// disk, a passthrough never. An enhancer that does not implement this serves whatever it is handed.
/// </summary>
/// <remarks>
/// Asked by <see cref="SharpenPipeline.CapabilitiesFor"/> right before a program is built, which is
/// the first enhance, so a deferred role's licence probe still never runs at DI build. It takes the
/// CHANNEL COUNT rather than the image because the pipeline debayers a 1-channel mosaic before any step
/// sees it: the question is what the role will be handed, not what the caller holds.
/// </remarks>
public interface IEnhancerAvailability
{
    /// <summary>Whether this enhancer will serve an input of <paramref name="channelCount"/> channels
    /// under <paramref name="options"/>.</summary>
    bool CanServe(int channelCount, EnhanceOptions options);

    /// <summary>
    /// Whether <paramref name="role"/> is registered AND serves an input of <paramref name="channelCount"/>
    /// channels under <paramref name="options"/>: the ONE rule every caller asks. A registration is not
    /// an answer, because a deferred RC-Astro role is registered on every host and serves only where the
    /// product is installed and licensed; asking whether it is null reads it as present on a machine
    /// where its first use throws.
    /// </summary>
    public static bool Serves([NotNullWhen(true)] IImageEnhancer? role, int channelCount, EnhanceOptions options)
        => role is not null && (role is not IEnhancerAvailability availability || availability.CanServe(channelCount, options));

    /// <summary>
    /// <see cref="Serves"/> for a gate asked before the input exists (a CLI flag, a group about to be
    /// read): whether <paramref name="role"/> serves a mono or a colour input, the two shapes a role is
    /// handed. A role that serves only one of them still fails clearly at the input it declines.
    /// </summary>
    public static bool ServesAny([NotNullWhen(true)] IImageEnhancer? role, EnhanceOptions options)
        => Serves(role, 1, options) || Serves(role, 3, options);
}

/// <summary>
/// Which roles can serve one input under one set of options, answered by
/// <see cref="SharpenPipeline.CapabilitiesFor"/>. The canonical program is built from it
/// (<see cref="LinearEnhanceProgram.For(EnhanceCapabilities)"/>), so no program asks for a role that
/// nothing serves: without a star remover the program is whole-frame, and a role gains a backend (an
/// in-house model, an RC-Astro licence) by registration alone, with no program edited.
/// </summary>
/// <param name="Deblur">A whole-frame deblurrer (RC-Astro BlurXTerminator, licensed).</param>
/// <param name="GradientCorrection">A gradient corrector (GraXpert, else the classical fit, so in
/// practice always).</param>
/// <param name="StarRemoval">A star remover (RC-Astro StarXTerminator today).</param>
/// <param name="StellarSharpen">A stellar sharpener (none today).</param>
/// <param name="Deconvolve">A starless-plate deconvolver (RC-Astro today; the in-house one at E7).</param>
/// <param name="Denoise">A denoiser for this input (RC-Astro NoiseXTerminator, else the in-house N2N
/// model for 3-channel input).</param>
public readonly record struct EnhanceCapabilities(
    bool Deblur,
    bool GradientCorrection,
    bool StarRemoval,
    bool StellarSharpen,
    bool Deconvolve,
    bool Denoise)
{
    /// <summary>Every role present: the split program's two named shapes,
    /// <see cref="SharpenRequest.Canonical"/> (no deblur) and <see cref="SharpenRequest.DeblurFirst"/>.</summary>
    public static EnhanceCapabilities AllRoles(bool deblur) => new(deblur, true, true, true, true, true);
}
