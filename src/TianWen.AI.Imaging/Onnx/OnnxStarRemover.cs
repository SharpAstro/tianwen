using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using TianWen.AI.Inference;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// AI4 NAFNet star-removal enhancer. Selects between
/// <c>darkstar_mono_AI4.onnx</c> (1 channel) and
/// <c>darkstar_color_AI4.onnx</c> (3 channels) by input
/// <see cref="Image.ChannelCount"/> and delegates the actual pipeline to
/// <see cref="ChunkedNafnetRunner"/>.
/// </summary>
/// <remarks>
/// <para>Domain semantics: linear-units in / linear-units out, but the
/// transformation is NOT a linear-domain function of the input -- star
/// removal globally rewrites the histogram (stellar pixels collapse to the
/// local nebula level). See PLAN-ai-enhancement.md "Domain semantics" for
/// the implications when chaining with other linear-domain tools.</para>
///
/// <para>Session lifecycle: registered as a singleton via
/// <c>AddTianWenAi</c>; lazily creates one <see cref="InferenceSession"/>
/// per (mono/color) model. Sessions are cached for the lifetime of the
/// instance and released on <see cref="Dispose"/>.</para>
/// </remarks>
public sealed class OnnxStarRemover(
    IModelResolver modelResolver,
    ILogger<OnnxStarRemover>? logger = null,
    int chunkSize = 256,
    int overlap = 64)
    : IStarRemover, IDisposable
{
    private const string MonoModel = "darkstar_mono_AI4.onnx";
    private const string ColorModel = "darkstar_color_AI4.onnx";

    private readonly object _gate = new();
    private InferenceSession? _monoSession;
    private InferenceSession? _colorSession;
    private bool _disposed;

    public string Name => "StarRemover (AI4 NAFNet)";

    public async Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.ChannelCount is not (1 or 3))
        {
            throw new NotSupportedException(
                $"OnnxStarRemover requires 1 or 3 channels (mono / RGB), got {input.ChannelCount}. " +
                "Debayer Bayer inputs upstream and split multi-band narrowband stacks into separate calls.");
        }
        if (input.MaxValue > 1.0f + 1e-3f)
        {
            throw new ArgumentException(
                $"OnnxStarRemover requires input normalised to [0, 1], got MaxValue={input.MaxValue}. " +
                "Use AstroImageDocument.AdoptImageAsync or Image.ScaleFloatValuesToUnitInPlace first.",
                nameof(input));
        }

        return await Task.Run(() => RunPipeline(input, cancellationToken), cancellationToken);
    }

    private Image RunPipeline(Image input, CancellationToken ct)
    {
        var (sourceChannels, srcW, srcH) = input.Shape;
        var modelName = sourceChannels == 1 ? MonoModel : ColorModel;
        logger?.LogDebug("OnnxStarRemover: input {W}x{H}x{C} model={Model} chunkSize={Chunk} overlap={Overlap}",
            srcW, srcH, sourceChannels, modelName, chunkSize, overlap);

        var session = AcquireSession(sourceChannels);
        var (imageInputName, outputName) = OnnxIoNames.SingleInput(session);

        var result = ChunkedNafnetRunner.Run(
            input, session, imageInputName, outputName,
            modelChannels: sourceChannels,   // darkstar models match source: mono->1, color->3
            chunkSize: chunkSize, overlap: overlap,
            extraInputs: null,
            ct: ct);

        var megapixels = (sourceChannels * srcW * (double)srcH) / 1_000_000.0;
        var throughputMpps = result.TotalMs > 0 ? megapixels * 1000.0 / result.TotalMs : 0.0;
        logger?.LogInformation(
            "OnnxStarRemover.EnhanceAsync: {Model} {W}x{H}x{C} chunks={Chunks} " +
            "stretch={Stretch}ms prep={Prep}ms infer={Infer}ms stitch={Stitch}ms unstretch={Unstretch}ms " +
            "throughput={Mpps:F2} Mp/s total={Total}ms",
            modelName, srcW, srcH, sourceChannels, result.ChunkCount,
            result.StretchMs, result.PrepMs, result.InferMs, result.StitchMs, result.UnstretchMs,
            throughputMpps, result.TotalMs);

        return result.Output;
    }

    private InferenceSession AcquireSession(int channels)
    {
        var modelName = channels == 1 ? MonoModel : ColorModel;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ref InferenceSession? slot = ref channels == 1 ? ref _monoSession : ref _colorSession;
            if (slot is null)
            {
                var modelPath = modelResolver.Resolve(modelName);
                logger?.LogInformation("OnnxStarRemover: loading {Model} from {Path}", modelName, modelPath);
                using var options = ExecutionProviderResolver.CreateSessionOptions(deviceId: 0, logger: logger);
                slot = new InferenceSession(modelPath, options);
            }
            return slot;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _monoSession?.Dispose();
            _monoSession = null;
            _colorSession?.Dispose();
            _colorSession = null;
            _disposed = true;
        }
    }
}
