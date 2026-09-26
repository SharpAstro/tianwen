using System;
using System.Threading;
using System.Threading.Tasks;

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
    /// <summary>
    /// Variant-aware overload. Concrete impls override to select the model
    /// (Default / Lite / Walking) based on <paramref name="variant"/>. The
    /// default impl ignores the variant and delegates to the base
    /// <see cref="IImageEnhancer.EnhanceAsync"/>, so test fakes /
    /// pass-through implementations don't need to override.
    /// </summary>
    Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, CancellationToken cancellationToken = default)
        => EnhanceAsync(input, cancellationToken);

    /// <summary>
    /// Variant + options + progress overload. Default impl drops <paramref name="options"/>
    /// and <paramref name="progress"/> and delegates to the variant overload (correct for an
    /// enhancer with no RC tuning and only coarse step-boundary progress). The RC nxt wrapper overrides
    /// this to read <see cref="EnhanceTuning"/> and relay NDJSON progress, ignoring the variant
    /// (nxt is a single model).
    /// </summary>
    Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        => EnhanceAsync(input, variant, cancellationToken);
}

/// <summary>
/// Selects a denoise model's weight bundle. The values come from the SETI Astro AI4 family, which
/// shipped each as its own weights and was removed on 2026-09-26; they stay as the extension point for
/// bundles of TianWen's own (walking noise is on the roadmap as a degradation of its own,
/// <c>docs/plans/model-training-roadmap.md</c> section 8). Only <see cref="Default"/> is served today:
/// RC-Astro NoiseXTerminator is one model, and the N2N denoiser has one bundle and refuses the others.
/// </summary>
public enum DenoiseVariant
{
    /// <summary>The standard bundle, and the only one any backend serves today.</summary>
    Default = 0,

    /// <summary>A faster, lighter bundle (AI4's was a half-width NAFNet, about 2x faster and slightly
    /// weaker on faint detail). No backend serves it today.</summary>
    Lite = 1,

    /// <summary>A bundle for walking noise (the drift streaks of an unguided, undithered run). No
    /// backend serves it today.</summary>
    Walking = 2,
}
