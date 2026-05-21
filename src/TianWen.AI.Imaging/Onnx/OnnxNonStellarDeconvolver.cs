using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TianWen.AI.Inference;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// AI4 NAFNet PSF-conditional deconvolver for the starless plate. Two ONNX
/// inputs: the image tensor and a scalar <c>psf01</c> in <c>[0, 1]</c> that
/// the network broadcasts internally and concatenates as a 4th input
/// channel. Delegates the chunked-inference pipeline to
/// <see cref="ChunkedNafnetRunner"/>; this class owns PSF estimation,
/// session management, and the per-call log line.
/// </summary>
public sealed class OnnxNonStellarDeconvolver(
    IModelResolver modelResolver,
    IPsfEstimator psfEstimator,
    ILogger<OnnxNonStellarDeconvolver>? logger = null,
    int chunkSize = 256,
    int overlap = 64)
    : INonStellarDeconvolver, IDisposable
{
    private const string Model = "deep_nonstellar_sharp_conditional_psf_AI4.onnx";
    private const int ModelChannels = 3;

    private readonly object _gate = new();
    private InferenceSession? _session;
    private bool _disposed;

    public string Name => "NonStellarDeconvolver (AI4 NAFNet, PSF-conditional)";

    public async Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.ChannelCount is not (1 or 3))
        {
            throw new NotSupportedException(
                $"OnnxNonStellarDeconvolver requires 1 or 3 channels, got {input.ChannelCount}.");
        }
        // Allow up to MaxValue=1.5 to tolerate small AI4 NAFNet overshoot when
        // chained as a pipeline stage (see Image.MtfUnstretch xmldoc -- network
        // excursions above [0, 1] are preserved as empirical max). Still
        // rejects miscalibrated inputs like raw [0, 65535] camera data.
        if (input.MaxValue > 1.5f)
        {
            throw new ArgumentException(
                $"OnnxNonStellarDeconvolver requires input normalised to ~[0, 1], got MaxValue={input.MaxValue}. " +
                "Use AstroImageDocument.AdoptImageAsync or Image.ScaleFloatValuesToUnitInPlace first.",
                nameof(input));
        }

        // PSF measurement runs against the LINEAR input (FindStarsAsync
        // expects unstretched data); everything else happens on the thread
        // pool inside Task.Run.
        var psf01 = await psfEstimator.EstimateAsync(input, cancellationToken);

        return await Task.Run(() => RunPipeline(input, psf01, cancellationToken), cancellationToken);
    }

    private Image RunPipeline(Image input, float psf01, CancellationToken ct)
    {
        var (sourceChannels, srcW, srcH) = input.Shape;
        logger?.LogDebug("OnnxNonStellarDeconvolver: input {W}x{H}x{C} psf01={Psf01:F3} chunkSize={Chunk} overlap={Overlap}",
            srcW, srcH, sourceChannels, psf01, chunkSize, overlap);

        var session = AcquireSession();
        var (imageInputName, scalarInputName, outputName) = OnnxIoNames.ImagePlusScalar(session);

        // PSF scalar tensor is shared across all chunks (whole-image psf01).
        var psfTensor = new DenseTensor<float>([1, 1]);
        psfTensor.Buffer.Span[0] = psf01;
        var extras = new[] { NamedOnnxValue.CreateFromTensor(scalarInputName, psfTensor) };

        var result = ChunkedNafnetRunner.Run(
            input, session, imageInputName, outputName,
            modelChannels: ModelChannels,
            chunkSize: chunkSize, overlap: overlap,
            extraInputs: extras,
            ct: ct);

        var megapixels = (sourceChannels * srcW * (double)srcH) / 1_000_000.0;
        var throughputMpps = result.TotalMs > 0 ? megapixels * 1000.0 / result.TotalMs : 0.0;
        logger?.LogInformation(
            "OnnxNonStellarDeconvolver.EnhanceAsync: {Model} {W}x{H}x{C} chunks={Chunks} stretchApplied={StretchApplied} psf01={Psf01:F3} " +
            "stretch={Stretch}ms prep={Prep}ms infer={Infer}ms stitch={Stitch}ms unstretch={Unstretch}ms " +
            "throughput={Mpps:F2} Mp/s total={Total}ms",
            Model, srcW, srcH, sourceChannels, result.ChunkCount, result.StretchApplied, psf01,
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
                logger?.LogInformation("OnnxNonStellarDeconvolver: loading {Model} from {Path}", Model, modelPath);
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
