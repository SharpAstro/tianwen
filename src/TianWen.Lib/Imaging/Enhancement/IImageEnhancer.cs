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
}
