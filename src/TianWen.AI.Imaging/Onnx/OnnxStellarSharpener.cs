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
/// AI4 NAFNet stellar-sharpening enhancer. Tightens per-star PSF on a
/// stars-only plate. Single ONNX input (no PSF conditional); model is
/// always 3-channel so mono sources tile their single channel into all 3
/// model input slots and we extract output channel 0.
/// </summary>
public sealed class OnnxStellarSharpener(
    IModelResolver modelResolver,
    ILogger<OnnxStellarSharpener>? logger = null,
    int chunkSize = 256,
    int overlap = 64)
    : IStellarSharpener, IDisposable
{
    private const string Model = "deep_sharp_stellar_AI4.onnx";
    private const int ModelChannels = 3;

    private readonly object _gate = new();
    private InferenceSession? _session;
    private bool _disposed;

    public string Name => "StellarSharpener (AI4 NAFNet)";

    public async Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.ChannelCount is not (1 or 3))
        {
            throw new NotSupportedException(
                $"OnnxStellarSharpener requires 1 or 3 channels, got {input.ChannelCount}.");
        }
        if (input.MaxValue > 1.0f + 1e-3f)
        {
            throw new ArgumentException(
                $"OnnxStellarSharpener requires input normalised to [0, 1], got MaxValue={input.MaxValue}. " +
                "Use AstroImageDocument.AdoptImageAsync or Image.ScaleFloatValuesToUnitInPlace first.",
                nameof(input));
        }

        return await Task.Run(() => RunPipeline(input, cancellationToken), cancellationToken);
    }

    private Image RunPipeline(Image input, CancellationToken ct)
    {
        var (sourceChannels, srcW, srcH) = input.Shape;
        logger?.LogDebug("OnnxStellarSharpener: input {W}x{H}x{C} chunkSize={Chunk} overlap={Overlap}",
            srcW, srcH, sourceChannels, chunkSize, overlap);

        var session = AcquireSession();
        var (imageInputName, outputName) = OnnxIoNames.SingleInput(session);

        var result = ChunkedNafnetRunner.Run(
            input, session, imageInputName, outputName,
            modelChannels: ModelChannels,
            chunkSize: chunkSize, overlap: overlap,
            extraInputs: null,
            ct: ct);

        var megapixels = (sourceChannels * srcW * (double)srcH) / 1_000_000.0;
        var throughputMpps = result.TotalMs > 0 ? megapixels * 1000.0 / result.TotalMs : 0.0;
        logger?.LogInformation(
            "OnnxStellarSharpener.EnhanceAsync: {Model} {W}x{H}x{C} chunks={Chunks} stretchApplied={StretchApplied} " +
            "stretch={Stretch}ms prep={Prep}ms infer={Infer}ms stitch={Stitch}ms unstretch={Unstretch}ms " +
            "throughput={Mpps:F2} Mp/s total={Total}ms",
            Model, srcW, srcH, sourceChannels, result.ChunkCount, result.StretchApplied,
            result.StretchMs, result.PrepMs, result.InferMs, result.StitchMs, result.UnstretchMs,
            throughputMpps, result.TotalMs);

        return result.Output;
    }

    private InferenceSession AcquireSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is null)
            {
                var modelPath = modelResolver.Resolve(Model);
                logger?.LogInformation("OnnxStellarSharpener: loading {Model} from {Path}", Model, modelPath);
                using var options = ExecutionProviderResolver.CreateSessionOptions(deviceId: 0, logger: logger);
                _session = new InferenceSession(modelPath, options);
            }
            return _session;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            _disposed = true;
        }
    }
}
