using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
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
/// AI4 NAFNet PSF-conditional deconvolver for the starless plate. Two-input
/// ONNX model: the source image and a scalar <c>psf01</c> in <c>[0, 1]</c>
/// that the network broadcasts internally and concatenates as a 4th input
/// channel. Wraps inference with the canonical MTF round-trip + chunked
/// stitching, just like <see cref="OnnxStarRemover"/> and
/// <see cref="OnnxStellarSharpener"/>.
/// </summary>
/// <remarks>
/// <para>PSF measurement happens via the injected <see cref="IPsfEstimator"/>
/// on the linear input <i>before</i> stretching -- HFD-based detection
/// expects unstretched data. The default v1 estimator
/// (<c>HfdPsfEstimator</c>) returns the whole-image median FWHM/2 (or the
/// fallback radius if no usable stars are found, which is the typical case
/// when called on a starless plate); a future per-chunk estimator can land
/// without touching the deconvolver.</para>
///
/// <para>Mono handling: same as <see cref="OnnxStellarSharpener"/> -- the
/// model has a fixed 3-channel image input, so mono sources tile their
/// single channel into all 3 input slots and we extract only channel 0 of
/// the output.</para>
/// </remarks>
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
                $"OnnxNonStellarDeconvolver requires 1 or 3 channels, got {input.ChannelCount}. " +
                "Debayer Bayer inputs upstream and split multi-band narrowband stacks into separate calls.");
        }
        if (input.MaxValue > 1.0f + 1e-3f)
        {
            throw new ArgumentException(
                $"OnnxNonStellarDeconvolver requires input normalised to [0, 1], got MaxValue={input.MaxValue}. " +
                "Use AstroImageDocument.AdoptImageAsync or Image.ScaleFloatValuesToUnitInPlace first.",
                nameof(input));
        }

        // PSF measurement runs against the LINEAR input (FindStarsAsync
        // expects unstretched data). The estimator is async because it may
        // call FindStarsAsync; everything else stays in the same Task.Run
        // body for one thread-pool hop.
        var psf01 = await psfEstimator.EstimateAsync(input, cancellationToken);

        return await Task.Run(() => RunPipeline(input, psf01, cancellationToken), cancellationToken);
    }

    private Image RunPipeline(Image input, float psf01, CancellationToken ct)
    {
        var (sourceChannels, srcW, srcH) = input.Shape;
        logger?.LogDebug("OnnxNonStellarDeconvolver: input {W}x{H}x{C}, psf01={Psf01:F3}, chunkSize={Chunk}, overlap={Overlap}, border={Border}",
            srcW, srcH, sourceChannels, psf01, chunkSize, overlap, AiNafnetInputs.StitchBorderPx);

        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();

        // 1. MTF input-normalisation stretch.
        var stretched = input.MtfStretch(AiNafnetInputs.TargetMedian, out var origMin, out var balances);
        var stretchMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();
        ct.ThrowIfCancellationRequested();

        // 2. AddBorder per source channel.
        var border = AiNafnetInputs.StitchBorderPx;
        var paddedSrcChannels = new float[sourceChannels][];
        int paddedW = 0, paddedH = 0;
        for (var c = 0; c < sourceChannels; c++)
        {
            paddedSrcChannels[c] = ChunkedInference.AddBorder(
                stretched.GetChannelSpan(c), srcW, srcH, border, out paddedW, out paddedH);
        }
        ct.ThrowIfCancellationRequested();

        // 3. Split per source channel.
        var srcChunksByChannel = new ImmutableArray<ChunkedInference.Chunk>[sourceChannels];
        for (var c = 0; c < sourceChannels; c++)
        {
            srcChunksByChannel[c] = ChunkedInference.Split(paddedSrcChannels[c], paddedW, paddedH, chunkSize, overlap);
        }
        var chunkCount = srcChunksByChannel[0].Length;
        var prepMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();

        // 4. Inference loop. Two inputs: image tensor + psf01 scalar.
        var session = AcquireSession();
        var (imageInputName, psfInputName, outputName) = PickIoNames(session);

        var outputChunksByChannel = new ChunkedInference.Chunk[sourceChannels][];
        for (var c = 0; c < sourceChannels; c++) outputChunksByChannel[c] = new ChunkedInference.Chunk[chunkCount];

        // PSF scalar tensor is shared across all chunks (whole-image psf01 in v1).
        var psfTensor = new DenseTensor<float>([1, 1]);
        psfTensor.Buffer.Span[0] = psf01;

        for (var i = 0; i < chunkCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var refChunk = srcChunksByChannel[0][i];
            var h = refChunk.Height;
            var w = refChunk.Width;
            var planeStride = h * w;

            var imageTensor = new DenseTensor<float>([1, ModelChannels, h, w]);
            var imageSpan = imageTensor.Buffer.Span;

            if (sourceChannels == 1)
            {
                // Tile-to-3 for mono (same logic as OnnxStellarSharpener).
                var ch0 = srcChunksByChannel[0][i].Data.AsSpan();
                ch0.CopyTo(imageSpan.Slice(0, planeStride));
                ch0.CopyTo(imageSpan.Slice(planeStride, planeStride));
                ch0.CopyTo(imageSpan.Slice(2 * planeStride, planeStride));
            }
            else
            {
                for (var c = 0; c < ModelChannels; c++)
                {
                    srcChunksByChannel[c][i].Data.AsSpan().CopyTo(imageSpan.Slice(c * planeStride, planeStride));
                }
            }

            using var result = session.Run(
            [
                NamedOnnxValue.CreateFromTensor(imageInputName, imageTensor),
                NamedOnnxValue.CreateFromTensor(psfInputName, psfTensor),
            ]);
            var outputTensor = result[0].AsTensor<float>();
            var outSpan = outputTensor.ToDenseTensor().Buffer.Span;

            for (var c = 0; c < sourceChannels; c++)
            {
                var outData = new float[planeStride];
                outSpan.Slice(c * planeStride, planeStride).CopyTo(outData);
                outputChunksByChannel[c][i] = refChunk with { Data = outData };
            }
        }
        var inferMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();

        // 5. Stitch per channel.
        var stitchedChannels = new float[sourceChannels][];
        for (var c = 0; c < sourceChannels; c++)
        {
            stitchedChannels[c] = new float[paddedW * paddedH];
            ChunkedInference.Stitch(outputChunksByChannel[c], stitchedChannels[c], paddedW, paddedH, border);
        }
        ct.ThrowIfCancellationRequested();

        // 6. RemoveBorder + rebuild Image data.
        var outChannelData = new float[sourceChannels][,];
        for (var c = 0; c < sourceChannels; c++)
        {
            var unpadded = ChunkedInference.RemoveBorder(stitchedChannels[c], paddedW, paddedH, border);
            var plane = new float[srcH, srcW];
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], srcW * srcH);
            unpadded.AsSpan().CopyTo(dst);
            outChannelData[c] = plane;
        }
        var stitchMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();

        var inferenceResult = new Image(outChannelData, BitDepth.Float32, 1.0f, 0f, 0f, input.ImageMeta);

        // 7. Inverse MTF -> source units.
        var unstretched = inferenceResult.MtfUnstretch(origMin, balances);
        var unstretchMs = phaseSw.ElapsedMilliseconds;
        var totalMs = totalSw.ElapsedMilliseconds;

        var megapixels = (sourceChannels * srcW * (double)srcH) / 1_000_000.0;
        var throughputMpps = totalMs > 0 ? megapixels * 1000.0 / totalMs : 0.0;

        logger?.LogInformation(
            "OnnxNonStellarDeconvolver.EnhanceAsync: {Model} {W}x{H}x{C} chunks={Chunks} psf01={Psf01:F3} " +
            "stretch={Stretch}ms prep={Prep}ms infer={Infer}ms stitch={Stitch}ms unstretch={Unstretch}ms " +
            "throughput={Mpps:F2} Mp/s total={Total}ms",
            Model, srcW, srcH, sourceChannels, chunkCount, psf01,
            stretchMs, prepMs, inferMs, stitchMs, unstretchMs,
            throughputMpps, totalMs);

        return unstretched;
    }

    /// <summary>
    /// Classify the session's two inputs by tensor rank: the image is the
    /// 4-D input (NCHW), the PSF scalar is rank &lt;= 2 (e.g. [1, 1]). Same
    /// heuristic SAS Pro's <c>_ort_pick_io_names</c> uses.
    /// </summary>
    private static (string imageName, string psfName, string outputName) PickIoNames(InferenceSession session)
    {
        var inputs = session.InputMetadata;
        if (inputs.Count != 2)
            throw new InvalidOperationException(
                $"OnnxNonStellarDeconvolver: expected 2 inputs (image + PSF), got {inputs.Count}: " +
                string.Join(", ", inputs.Keys));

        string? imageName = null;
        string? psfName = null;
        foreach (var (name, meta) in inputs)
        {
            if (meta.Dimensions.Length <= 2)
                psfName = name;
            else
                imageName = name;
        }
        if (imageName is null || psfName is null)
            throw new InvalidOperationException(
                $"OnnxNonStellarDeconvolver: could not classify inputs by rank; got: " +
                string.Join(", ", inputs.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value.Dimensions)}]")));

        if (session.OutputMetadata.Count != 1)
            throw new InvalidOperationException(
                $"OnnxNonStellarDeconvolver: expected 1 output, got {session.OutputMetadata.Count}");
        var outputName = session.OutputMetadata.Keys.First();

        return (imageName, psfName, outputName);
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
