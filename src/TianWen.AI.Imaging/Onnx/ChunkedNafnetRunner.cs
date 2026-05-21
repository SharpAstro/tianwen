using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TianWen.Lib.Imaging;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// Shared chunked-inference pipeline for the AI4 NAFNet enhancers. Each
/// concrete enhancer (<see cref="OnnxStarRemover"/>,
/// <see cref="OnnxStellarSharpener"/>,
/// <see cref="OnnxNonStellarDeconvolver"/>) supplies a session + IO names +
/// model channel count + any per-call extra inputs (e.g. the
/// PSF-conditional scalar) and gets a <see cref="ChunkedNafnetResult"/>
/// back with the output image plus per-phase timings.
/// </summary>
/// <remarks>
/// <para>Pipeline order: <see cref="Image.MtfStretch"/> ->
/// <see cref="ChunkedInference.AddBorder"/> per channel ->
/// <see cref="ChunkedInference.Split"/> per channel -> NCHW tensor pack +
/// <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>
/// per chunk -> tensor unpack ->
/// <see cref="ChunkedInference.Stitch"/> per channel ->
/// <see cref="ChunkedInference.RemoveBorder"/> per channel ->
/// <see cref="Image.MtfUnstretch"/>.</para>
///
/// <para>Channel-count handling. If the input source channel count differs
/// from the model's expected channel count, the runner tiles source channel
/// 0 across all model input slots (source=1, model=3 -- the canonical mono
/// case for stellar/deconv NAFNet models) and extracts only output channel
/// 0. If source matches model (3->3 or 1->1), a direct per-channel copy is
/// used. Source=3 with model=1 is rejected -- we never need it, and
/// silently down-mixing would be wrong.</para>
/// </remarks>
public static class ChunkedNafnetRunner
{
    /// <summary>
    /// Run one inference pass against <paramref name="session"/> over the
    /// chunked + bordered representation of <paramref name="input"/>.
    /// </summary>
    /// <param name="input">Source image in <c>[0, 1]</c>.</param>
    /// <param name="session">Already-loaded ORT session.</param>
    /// <param name="imageInputName">Name of the image input on the ONNX
    /// model (resolved by the caller -- single-input models have one
    /// metadata key, multi-input models classify by rank).</param>
    /// <param name="outputName">Name of the output tensor.</param>
    /// <param name="modelChannels">Channel dimension the ONNX model expects
    /// on its image input -- 1 for <c>darkstar_mono_AI4.onnx</c>, 3 for
    /// every other AI4 NAFNet.</param>
    /// <param name="chunkSize">Tile size in pixels.</param>
    /// <param name="overlap">Inter-chunk overlap in pixels (must be &gt;= 2 *
    /// <see cref="AiNafnetInputs.StitchBorderPx"/> for the inner regions
    /// to abut without coverage gaps).</param>
    /// <param name="extraInputs">Additional ONNX inputs reused across every
    /// chunk -- e.g. the <c>psf01</c> scalar for the PSF-conditional
    /// non-stellar deconvolver. Pass <c>null</c> for single-input models.</param>
    public static ChunkedNafnetResult Run(
        Image input,
        InferenceSession session,
        string imageInputName,
        string outputName,
        int modelChannels,
        int chunkSize,
        int overlap,
        IReadOnlyList<NamedOnnxValue>? extraInputs = null,
        CancellationToken ct = default)
    {
        var (sourceChannels, srcW, srcH) = input.Shape;
        ValidateChannels(sourceChannels, modelChannels);

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

        // 4. Inference loop. For source==model, copy each source channel into
        //    the matching model input slot. For source==1, model==3 (mono
        //    via tile-to-3), replicate source channel 0 into all 3 model
        //    slots and we'll later extract only output channel 0.
        var outputChunksByChannel = new ChunkedInference.Chunk[sourceChannels][];
        for (var c = 0; c < sourceChannels; c++) outputChunksByChannel[c] = new ChunkedInference.Chunk[chunkCount];

        var tileMonoToModel = sourceChannels == 1 && modelChannels == 3;

        for (var i = 0; i < chunkCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var refChunk = srcChunksByChannel[0][i];
            var h = refChunk.Height;
            var w = refChunk.Width;
            var planeStride = h * w;

            var imageTensor = new DenseTensor<float>([1, modelChannels, h, w]);
            var imageSpan = imageTensor.Buffer.Span;

            if (tileMonoToModel)
            {
                var ch0 = srcChunksByChannel[0][i].Data.AsSpan();
                for (var c = 0; c < modelChannels; c++)
                {
                    ch0.CopyTo(imageSpan.Slice(c * planeStride, planeStride));
                }
            }
            else
            {
                // sourceChannels == modelChannels (1->1 or 3->3): direct copy.
                for (var c = 0; c < modelChannels; c++)
                {
                    srcChunksByChannel[c][i].Data.AsSpan().CopyTo(imageSpan.Slice(c * planeStride, planeStride));
                }
            }

            // Build the input list. Each Run call's input list contains the
            // current chunk's image tensor plus any caller-supplied extras
            // (e.g. psf01). Extras don't change per chunk so the same
            // tensor reference is reused safely.
            var inputs = new List<NamedOnnxValue>(1 + (extraInputs?.Count ?? 0))
            {
                NamedOnnxValue.CreateFromTensor(imageInputName, imageTensor),
            };
            if (extraInputs is { Count: > 0 }) inputs.AddRange(extraInputs);

            using var result = session.Run(inputs);
            var outputTensor = result[0].AsTensor<float>();
            var outSpan = outputTensor.ToDenseTensor().Buffer.Span;

            // Extract sourceChannels channels from the modelChannels output
            // (mono via tile-to-3 -> only channel 0; otherwise per-channel
            // direct).
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

        // 6. RemoveBorder + rebuild Image data jagged array.
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

        return new ChunkedNafnetResult(
            unstretched, chunkCount, stretchMs, prepMs, inferMs, stitchMs, unstretchMs, totalMs);
    }

    private static void ValidateChannels(int sourceChannels, int modelChannels)
    {
        if (sourceChannels is not (1 or 3))
        {
            throw new NotSupportedException(
                $"ChunkedNafnetRunner requires 1 or 3 source channels, got {sourceChannels}.");
        }
        if (modelChannels is not (1 or 3))
        {
            throw new NotSupportedException(
                $"ChunkedNafnetRunner requires modelChannels 1 or 3, got {modelChannels}.");
        }
        if (sourceChannels == 3 && modelChannels == 1)
        {
            // We never need this. The darkstar-mono model is 1-ch; mono
            // sources go there. Color sources go to the 3-ch model. Tiling
            // 3-channel source down to 1 (e.g. by luminance) would silently
            // lose chrominance and produce surprising output.
            throw new NotSupportedException(
                "ChunkedNafnetRunner: cannot feed a 3-channel source to a 1-channel model -- " +
                "would require luminance reduction that loses chrominance information.");
        }
    }
}

/// <summary>
/// Per-call result + timing breakdown returned by <see cref="ChunkedNafnetRunner.Run"/>.
/// The enhancer that called the runner emits its own log line using these
/// values so individual enhancers retain their distinct log categories.
/// </summary>
public sealed record ChunkedNafnetResult(
    Image Output,
    int ChunkCount,
    long StretchMs,
    long PrepMs,
    long InferMs,
    long StitchMs,
    long UnstretchMs,
    long TotalMs);
