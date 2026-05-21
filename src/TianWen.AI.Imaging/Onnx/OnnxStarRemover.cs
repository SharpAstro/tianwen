using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
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
/// AI4 NAFNet star-removal enhancer. Selects between
/// <c>darkstar_mono_AI4.onnx</c> (1 channel) and
/// <c>darkstar_color_AI4.onnx</c> (3 channels) by input
/// <see cref="Image.ChannelCount"/>. Wraps each inference call with the
/// canonical AI4 input prep: <see cref="Image.MtfStretch"/> ->
/// <see cref="ChunkedInference.AddBorder"/> -> tile + run ONNX session +
/// stitch -> <see cref="ChunkedInference.RemoveBorder"/> ->
/// <see cref="Image.MtfUnstretch"/>.
/// </summary>
/// <remarks>
/// Input range expectation: caller must pass an image already normalised to
/// <c>[0, 1]</c> (e.g. via <c>AstroImageDocument.AdoptImageAsync</c> or
/// <c>Image.ScaleFloatValuesToUnitInPlace</c>). The MTF primitive in
/// <see cref="Image.MidtonesTransferFunction"/> clamps to <c>[0, 1]</c>; if
/// fed wider-range data it silently saturates and the inference output is
/// garbage. We validate the bound and throw with a pointer to the right
/// helper rather than failing silently.
///
/// Session lifecycle: this class is registered as a singleton via
/// <c>AddTianWenAi</c> and lazily creates one <see cref="InferenceSession"/>
/// per (mono/color) model. Sessions are cached for the lifetime of the
/// instance and released on <see cref="Dispose"/>.
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

        // The actual session run is sync; offload to the thread pool so the
        // public API stays async-friendly without blocking the caller.
        return await Task.Run(() => RunPipeline(input, cancellationToken), cancellationToken);
    }

    private Image RunPipeline(Image input, CancellationToken ct)
    {
        var (channels, srcW, srcH) = input.Shape;
        logger?.LogDebug("OnnxStarRemover: input {W}x{H}x{C}, chunkSize={Chunk}, overlap={Overlap}, border={Border}",
            srcW, srcH, channels, chunkSize, overlap, AiNafnetInputs.StitchBorderPx);

        // 1. MTF input-normalisation stretch (per-channel pedestal + MTF that
        //    lands each channel's median at AiNafnetInputs.TargetMedian).
        var stretched = input.MtfStretch(AiNafnetInputs.TargetMedian, out var origMin, out var balances);
        ct.ThrowIfCancellationRequested();

        // 2. AddBorder per channel. NAFNet's tile boundaries produce visible
        //    artefacts; we pad first so the post-stitch border drop never
        //    eats real image data.
        var border = AiNafnetInputs.StitchBorderPx;
        var paddedChannels = new float[channels][];
        int paddedW = 0, paddedH = 0;
        for (var c = 0; c < channels; c++)
        {
            paddedChannels[c] = ChunkedInference.AddBorder(
                stretched.GetChannelSpan(c), srcW, srcH, border, out paddedW, out paddedH);
        }
        ct.ThrowIfCancellationRequested();

        // 3. Split each padded channel into matching chunks. Same parameters
        //    -> identical chunk layout across channels, so we can iterate
        //    chunks-per-position and pack into one (1, C, h, w) NCHW tensor.
        var chunksPerChannel = new ImmutableArray<ChunkedInference.Chunk>[channels];
        for (var c = 0; c < channels; c++)
        {
            chunksPerChannel[c] = ChunkedInference.Split(paddedChannels[c], paddedW, paddedH, chunkSize, overlap);
        }
        var chunkCount = chunksPerChannel[0].Length;

        // 4. Run inference chunk-by-chunk against the right model.
        var session = AcquireSession(channels);
        var inputName = session.InputMetadata.Keys.GetEnumerator().AsSingle();
        var outputName = session.OutputMetadata.Keys.GetEnumerator().AsSingle();
        var outputChunksByChannel = new ChunkedInference.Chunk[channels][];
        for (var c = 0; c < channels; c++) outputChunksByChannel[c] = new ChunkedInference.Chunk[chunkCount];

        for (var i = 0; i < chunkCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var refChunk = chunksPerChannel[0][i];
            var h = refChunk.Height;
            var w = refChunk.Width;
            var planeStride = h * w;

            var inputTensor = new DenseTensor<float>(new[] { 1, channels, h, w });
            var inputSpan = inputTensor.Buffer.Span;
            for (var c = 0; c < channels; c++)
            {
                chunksPerChannel[c][i].Data.AsSpan().CopyTo(inputSpan.Slice(c * planeStride, planeStride));
            }

            using var result = session.Run(
                [NamedOnnxValue.CreateFromTensor(inputName, inputTensor)]);
            var outputTensor = result[0].AsTensor<float>();
            var outSpan = outputTensor.ToDenseTensor().Buffer.Span;

            for (var c = 0; c < channels; c++)
            {
                var outData = new float[planeStride];
                outSpan.Slice(c * planeStride, planeStride).CopyTo(outData);
                outputChunksByChannel[c][i] = refChunk with { Data = outData };
            }
        }

        // 5. Stitch chunks back per channel, with the canonical border drop.
        var stitchedChannels = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            stitchedChannels[c] = new float[paddedW * paddedH];
            ChunkedInference.Stitch(outputChunksByChannel[c], stitchedChannels[c], paddedW, paddedH, border);
        }
        ct.ThrowIfCancellationRequested();

        // 6. RemoveBorder per channel; rebuild Image data jagged array.
        // CreateChannelData is internal to TianWen.Lib; allocate inline here.
        var outChannelData = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var unpadded = ChunkedInference.RemoveBorder(stitchedChannels[c], paddedW, paddedH, border);
            var plane = new float[srcH, srcW];
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], srcW * srcH);
            unpadded.AsSpan().CopyTo(dst);
            outChannelData[c] = plane;
        }

        // The stretched inference output is in [0, 1] (modulo small NAFNet
        // excursions); MaxValue=1.0 is a safe upper bound here -- MtfUnstretch
        // will compute the empirical max during the inverse pass anyway.
        var inferenceResult = new Image(outChannelData, BitDepth.Float32, 1.0f, 0f, 0f, input.ImageMeta);

        // 7. Inverse MTF -> source units. Linear-units output (but see
        //    PLAN-ai-enhancement.md "Domain semantics" for why star-removal
        //    output is not a linear-semantics function of input).
        return inferenceResult.MtfUnstretch(origMin, balances);
    }

    private InferenceSession AcquireSession(int channels)
    {
        var modelName = channels == 1 ? MonoModel : ColorModel;
        // Lazy-init under lock. Singleton lifetime + repeated Enhance calls
        // share the InferenceSession (compiling the EP graph is the costly
        // part of session creation).
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

/// <summary>
/// Tiny helper: pull the single string out of an enumerator, throwing if the
/// caller's assumption (input/output count == 1) is wrong. Used to extract
/// the bound input + output names from a session's metadata dictionaries.
/// </summary>
file static class EnumeratorExt
{
    public static string AsSingle(this IEnumerator<string> en)
    {
        if (!en.MoveNext()) throw new InvalidOperationException("expected at least one element");
        var only = en.Current;
        if (en.MoveNext()) throw new InvalidOperationException($"expected exactly one element; saw at least two ({only}, {en.Current}, ...)");
        return only;
    }
}
