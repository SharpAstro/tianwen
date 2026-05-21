using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
/// AI4 NAFNet stellar-sharpening enhancer. Tightens per-star PSF on a
/// stars-only plate (the <c>Source - Starless</c> diff produced by the
/// pipeline). Wraps inference with the canonical MTF round-trip
/// (<see cref="Image.MtfStretch"/> -> AddBorder -> Split -> session.Run ->
/// Stitch -> RemoveBorder -> <see cref="Image.MtfUnstretch"/>).
/// </summary>
/// <remarks>
/// <para>The <c>deep_sharp_stellar_AI4.onnx</c> model is exported with a
/// fixed 3-channel input -- there is no mono variant in the AI4 bundle. For
/// mono inputs we tile the single channel across all three input channels,
/// run inference, then take channel 0 of the output. This matches SAS Pro's
/// <c>np.tile(inp, (1, 3, 1, 1))</c> + <c>y = out[0, 0]</c> pattern in
/// <c>sharpen_engine.py</c>. Stellar sharpening is achromatic by nature, so
/// the achromatic-replication round-trip preserves the intended effect.</para>
///
/// <para>Unlike <see cref="OnnxStarRemover"/>, this model has a single ONNX
/// input (no PSF scalar) -- the network learned the kernel implicitly from
/// the training distribution. Compare with the PSF-conditional non-stellar
/// deconvolver (planned Phase 4) which takes an explicit scalar PSF input.</para>
/// </remarks>
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
                $"OnnxStellarSharpener requires 1 or 3 channels, got {input.ChannelCount}. " +
                "Debayer Bayer inputs upstream and split multi-band narrowband stacks into separate calls.");
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
        logger?.LogDebug("OnnxStellarSharpener: input {W}x{H}x{C}, chunkSize={Chunk}, overlap={Overlap}, border={Border}",
            srcW, srcH, sourceChannels, chunkSize, overlap, AiNafnetInputs.StitchBorderPx);

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

        // 4. Inference loop. The model always sees 3 channels; for mono input
        //    we replicate channel 0 across the 3 input slots and extract just
        //    channel 0 of the output (matches SAS Pro's mono-via-tile flow).
        var session = AcquireSession();
        var inputName = session.InputMetadata.Keys.GetEnumerator().AsSingle();
        var outputName = session.OutputMetadata.Keys.GetEnumerator().AsSingle();

        var outputChunksByChannel = new ChunkedInference.Chunk[sourceChannels][];
        for (var c = 0; c < sourceChannels; c++) outputChunksByChannel[c] = new ChunkedInference.Chunk[chunkCount];

        for (var i = 0; i < chunkCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var refChunk = srcChunksByChannel[0][i];
            var h = refChunk.Height;
            var w = refChunk.Width;
            var planeStride = h * w;

            var inputTensor = new DenseTensor<float>(new[] { 1, ModelChannels, h, w });
            var inputSpan = inputTensor.Buffer.Span;

            if (sourceChannels == 1)
            {
                // Tile-to-3: replicate the single source channel into all 3
                // input slots. Achromatic by construction (output channel 0 is
                // what we keep after inference).
                var ch0 = srcChunksByChannel[0][i].Data.AsSpan();
                ch0.CopyTo(inputSpan.Slice(0, planeStride));
                ch0.CopyTo(inputSpan.Slice(planeStride, planeStride));
                ch0.CopyTo(inputSpan.Slice(2 * planeStride, planeStride));
            }
            else
            {
                // sourceChannels == 3 -- direct per-channel copy.
                for (var c = 0; c < ModelChannels; c++)
                {
                    srcChunksByChannel[c][i].Data.AsSpan().CopyTo(inputSpan.Slice(c * planeStride, planeStride));
                }
            }

            using var result = session.Run(
                [NamedOnnxValue.CreateFromTensor(inputName, inputTensor)]);
            var outputTensor = result[0].AsTensor<float>();
            var outSpan = outputTensor.ToDenseTensor().Buffer.Span;

            // Extract sourceChannels channels (1 for mono -- just channel 0;
            // 3 for RGB) from the always-3-channel output.
            for (var c = 0; c < sourceChannels; c++)
            {
                var outData = new float[planeStride];
                outSpan.Slice(c * planeStride, planeStride).CopyTo(outData);
                outputChunksByChannel[c][i] = refChunk with { Data = outData };
            }
        }
        var inferMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();

        // 5. Stitch chunks back per (source) channel.
        var stitchedChannels = new float[sourceChannels][];
        for (var c = 0; c < sourceChannels; c++)
        {
            stitchedChannels[c] = new float[paddedW * paddedH];
            ChunkedInference.Stitch(outputChunksByChannel[c], stitchedChannels[c], paddedW, paddedH, border);
        }
        ct.ThrowIfCancellationRequested();

        // 6. RemoveBorder + rebuild jagged Image data.
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
            "OnnxStellarSharpener.EnhanceAsync: {Model} {W}x{H}x{C} chunks={Chunks} " +
            "stretch={Stretch}ms prep={Prep}ms infer={Infer}ms stitch={Stitch}ms unstretch={Unstretch}ms " +
            "throughput={Mpps:F2} Mp/s total={Total}ms",
            Model, srcW, srcH, sourceChannels, chunkCount,
            stretchMs, prepMs, inferMs, stitchMs, unstretchMs,
            throughputMpps, totalMs);

        return unstretched;
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

/// <summary>
/// Local copy of <c>OnnxStarRemover.EnumeratorExt.AsSingle</c>. Kept duplicate
/// rather than promoted to a shared utility because the next phase
/// (<c>OnnxNonStellarDeconvolver</c>) needs a two-input variant -- once
/// three enhancers share the same metadata-introspection shape we'll lift
/// this into a single helper.
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
