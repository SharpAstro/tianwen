using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using TianWen.AI.Inference;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// AI4 NAFNet noise-reduction enhancer. Selects between
/// <c>deep_denoise_mono_AI4*.onnx</c> (1 channel) and
/// <c>deep_denoise_color_AI4*.onnx</c> (3 channels) by input
/// <see cref="Image.ChannelCount"/>, and between Default / Lite / Walking
/// weight bundles by the <see cref="DenoiseVariant"/> argument.
/// Single ONNX input (no PSF conditional); delegates the actual pipeline
/// to <see cref="ChunkedNafnetRunner"/>.
/// </summary>
/// <remarks>
/// <para>Domain semantics: linear-units in / linear-units out, and the
/// transformation is well-approximated as a linear-domain function of the
/// input (local grain suppression; no histogram macro-shape changes).
/// Chains cleanly with other linear-domain processing.</para>
///
/// <para>Session lifecycle: registered as a singleton via
/// <c>AddTianWenAi</c>; lazily creates one <see cref="InferenceSession"/>
/// per (channel-count, variant) pair, cached for the lifetime of the
/// instance and released on <see cref="Dispose"/>.</para>
/// </remarks>
public sealed class OnnxDenoiser(
    IModelResolver modelResolver,
    ILogger<OnnxDenoiser>? logger = null,
    int chunkSize = 256,
    int overlap = 64)
    : IDenoiseEnhancer, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(int Channels, DenoiseVariant Variant), InferenceSession> _sessions = [];
    private bool _disposed;

    public string Name => "Denoiser (AI4 NAFNet)";

    public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        => EnhanceAsync(input, DenoiseVariant.Default, cancellationToken);

    public async Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.ChannelCount is not (1 or 3))
        {
            throw new NotSupportedException(
                $"OnnxDenoiser requires 1 or 3 channels (mono / RGB), got {input.ChannelCount}.");
        }
        // Allow up to MaxValue=1.5 to tolerate the small overshoot AI4 NAFNet
        // models produce above [0, 1] (Image.MtfUnstretch tracks the empirical
        // max, not a clamped one -- see its xmldoc). The check still rejects
        // miscalibrated inputs like raw [0, 65535] camera data.
        if (input.MaxValue > 1.5f)
        {
            throw new ArgumentException(
                $"OnnxDenoiser requires input normalised to ~[0, 1], got MaxValue={input.MaxValue}. " +
                "Use AstroImageDocument.AdoptImageAsync or Image.ScaleFloatValuesToUnitInPlace first.",
                nameof(input));
        }

        return await Task.Run(() => RunPipeline(input, variant, cancellationToken), cancellationToken);
    }

    private Image RunPipeline(Image input, DenoiseVariant variant, CancellationToken ct)
    {
        var (sourceChannels, srcW, srcH) = input.Shape;
        var modelName = ModelFileNameFor(sourceChannels, variant);
        logger?.LogDebug("OnnxDenoiser: input {W}x{H}x{C} variant={Variant} model={Model} chunkSize={Chunk} overlap={Overlap}",
            srcW, srcH, sourceChannels, variant, modelName, chunkSize, overlap);

        var session = AcquireSession(sourceChannels, variant);
        var (imageInputName, outputName) = OnnxIoNames.SingleInput(session);

        var result = ChunkedNafnetRunner.Run(
            input, session, imageInputName, outputName,
            modelChannels: sourceChannels,   // denoise models match source: mono->1, color->3
            chunkSize: chunkSize, overlap: overlap,
            extraInputs: null,
            ct: ct);

        var megapixels = (sourceChannels * srcW * (double)srcH) / 1_000_000.0;
        var throughputMpps = result.TotalMs > 0 ? megapixels * 1000.0 / result.TotalMs : 0.0;
        logger?.LogInformation(
            "OnnxDenoiser.EnhanceAsync: {Model} variant={Variant} {W}x{H}x{C} chunks={Chunks} stretchApplied={StretchApplied} " +
            "stretch={Stretch}ms prep={Prep}ms infer={Infer}ms stitch={Stitch}ms unstretch={Unstretch}ms " +
            "throughput={Mpps:F2} Mp/s total={Total}ms",
            modelName, variant, srcW, srcH, sourceChannels, result.ChunkCount, result.StretchApplied,
            result.StretchMs, result.PrepMs, result.InferMs, result.StitchMs, result.UnstretchMs,
            throughputMpps, result.TotalMs);

        return result.Output;
    }

    /// <summary>
    /// Resolves the (channels, variant) pair to the on-disk ONNX file name.
    /// Mono lite has an upstream filename typo (<c>..lite..onnx</c>); we
    /// honour the disk reality rather than fight it.
    /// </summary>
    private static string ModelFileNameFor(int channels, DenoiseVariant variant) => (channels, variant) switch
    {
        (1, DenoiseVariant.Default) => "deep_denoise_mono_AI4.onnx",
        (3, DenoiseVariant.Default) => "deep_denoise_color_AI4.onnx",
        (1, DenoiseVariant.Lite)    => "deep_denoise_mono_AI4_lite..onnx",
        (3, DenoiseVariant.Lite)    => "deep_denoise_color_AI4_lite.onnx",
        (1, DenoiseVariant.Walking) => "deep_denoise_mono_AI4_1w.onnx",
        (3, DenoiseVariant.Walking) => "deep_denoise_color_AI4_1w.onnx",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, $"Unsupported (channels={channels}, variant={variant}) pair."),
    };

    private InferenceSession AcquireSession(int channels, DenoiseVariant variant)
    {
        var key = (channels, variant);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sessions.TryGetValue(key, out var session))
            {
                var modelName = ModelFileNameFor(channels, variant);
                var modelPath = modelResolver.Resolve(modelName);
                logger?.LogInformation("OnnxDenoiser: loading {Model} from {Path}", modelName, modelPath);
                using var options = ExecutionProviderResolver.CreateSessionOptions(deviceId: 0, logger: logger);
                session = new InferenceSession(modelPath, options);
                _sessions[key] = session;
            }
            return session;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values) session.Dispose();
            _sessions.Clear();
            _disposed = true;
        }
    }
}
