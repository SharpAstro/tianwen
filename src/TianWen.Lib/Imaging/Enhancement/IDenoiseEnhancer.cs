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
    /// and <paramref name="progress"/> and delegates to the variant overload (correct for SAS:
    /// it has no RC tuning and only coarse step-boundary progress). The RC nxt wrapper overrides
    /// this to read <see cref="EnhanceTuning"/> and relay NDJSON progress, ignoring the variant
    /// (nxt is a single model).
    /// </summary>
    Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        => EnhanceAsync(input, variant, cancellationToken);
}

/// <summary>
/// Selects the AI4 NAFNet denoise model weight bundle. Mirrors the SAS Pro
/// <c>denoise_engine.py</c> variant flags (<c>lite</c>, <c>walking</c>).
/// </summary>
public enum DenoiseVariant
{
    /// <summary>Standard AI4 NAFNet weights -- highest quality, slowest.
    /// File: <c>deep_denoise_{mono,color}_AI4.onnx</c>.</summary>
    Default = 0,

    /// <summary>Lite variant (half-width NAFNet, ~2x faster, slightly less
    /// effective on faint detail). File:
    /// <c>deep_denoise_{mono,color}_AI4_lite.onnx</c>.</summary>
    Lite = 1,

    /// <summary>"Walking-noise" variant trained on dither-correlated /
    /// pattern noise (the slow drift artefact common to long uncalibrated
    /// stacks). File: <c>deep_denoise_{mono,color}_AI4_1w.onnx</c>.</summary>
    Walking = 2,
}
