using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Single-input single-output image-to-image neural enhancer (deconvolution,
/// noise reduction, sharpening, star repair, etc.). Concrete implementations
/// live in <c>TianWen.AI.Imaging</c> and wrap an ONNX model loaded via
/// <c>TianWen.AI.Inference.ExecutionProviderResolver</c>; this interface
/// lives in <c>TianWen.Lib</c> alongside <c>IPlateSolver</c> so the core
/// library can take enhancers via DI without pulling in ONNX Runtime.
/// </summary>
public interface IImageEnhancer
{
    /// <summary>Human-readable name for diagnostics + UI selection.</summary>
    string Name { get; }

    /// <summary>
    /// Applies the enhancer to <paramref name="input"/> and returns a new
    /// <see cref="Image"/>. The input is not mutated; the caller retains
    /// ownership of both input and output buffers.
    /// </summary>
    Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Options-aware overload threading per-operation <see cref="EnhanceOptions"/> (backend
    /// selection + RC-Astro tuning) and an optional per-step <paramref name="progress"/> sink
    /// (0..1). The default impl ignores both and delegates to the param-less overload, so the
    /// SAS ONNX enhancers and test fakes need not override it; the RC-Astro wrappers and the
    /// <c>DeferredEnhancer</c> proxies do, to honour <see cref="EnhanceOptions.Backend"/> /
    /// <see cref="EnhanceOptions.Tuning"/> and relay NDJSON progress.
    /// </summary>
    Task<Image> EnhanceAsync(Image input, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        => EnhanceAsync(input, cancellationToken);
}
