using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TianWen.Lib;
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

        // Per-chunk input tensors come from the shared ArrayPool via
        // ArrayPoolHelper.Rent<float> (using-scoped SharedObject<T>). On a
        // multi-megapixel input this saves ~hundreds of MB of GC churn
        // (256 chunks * 3 channels * chunkSize * chunkSize * 4 bytes);
        // without pooling the inference loop spends a noticeable fraction
        // of its time in Gen2 GC.

        // NAFNet has 4 levels of stride-2 downsampling -> the spatial dims of
        // every tensor it sees must be divisible by 16 (= 2^4). Interior
        // chunks happen to satisfy this when chunkSize/overlap are chosen to,
        // but edge chunks at the right/bottom of the padded image are
        // whatever-was-left, often NOT divisible. We pad each chunk locally
        // up to the next multiple of 16, run inference, then crop the
        // output back. Padding strategy: replicate the source's rightmost
        // column + bottom row into the padded region (matches SAS Pro's
        // _pad2d_to_multiple(mode="reflect") for the typical case of 1-15
        // pixels of interior padding, and avoids the sharp-edge artefact
        // zero-padding would create).
        const int NafnetMultiple = 16;

        for (var i = 0; i < chunkCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var refChunk = srcChunksByChannel[0][i];
            var h = refChunk.Height;
            var w = refChunk.Width;
            var infH = ((h + NafnetMultiple - 1) / NafnetMultiple) * NafnetMultiple;
            var infW = ((w + NafnetMultiple - 1) / NafnetMultiple) * NafnetMultiple;
            var infPlaneStride = infH * infW;
            var tensorElementCount = modelChannels * infPlaneStride;

            // Rent from the shared pool via the using-scoped helper. The
            // SharedObject<T>'s AsMemory()/AsSpan() return the exact
            // requested length (ArrayPool.Rent itself may return a larger
            // buffer; SharedObject hides that detail).
            using var pooled = ArrayPoolHelper.Rent<float>(tensorElementCount);
            {
                var tensorMemory = pooled.AsMemory();
                var imageTensor = new DenseTensor<float>(tensorMemory, [1, modelChannels, infH, infW]);
                var imageSpan = pooled.AsSpan();

                // Pack source chunk(s) into the top-left of each model channel
                // slot with replicate-pad on the right/bottom.
                for (var modelCh = 0; modelCh < modelChannels; modelCh++)
                {
                    var sourceCh = tileMonoToModel ? 0 : modelCh;
                    var srcData = srcChunksByChannel[sourceCh][i].Data;
                    var chOffset = modelCh * infPlaneStride;

                    for (var y = 0; y < h; y++)
                    {
                        var srcRowOffset = y * w;
                        var dstRowOffset = chOffset + y * infW;
                        srcData.AsSpan(srcRowOffset, w).CopyTo(imageSpan.Slice(dstRowOffset, w));
                        if (infW > w)
                        {
                            // Replicate the rightmost source column across the padded
                            // columns (avoids sharp-edge artefacts in the network).
                            var rightmost = srcData[srcRowOffset + w - 1];
                            for (var x = w; x < infW; x++) imageSpan[dstRowOffset + x] = rightmost;
                        }
                    }
                    if (infH > h)
                    {
                        // Replicate the bottom (already replicate-padded on the
                        // right) row down to the padded height.
                        var lastRow = imageSpan.Slice(chOffset + (h - 1) * infW, infW);
                        for (var y = h; y < infH; y++)
                        {
                            lastRow.CopyTo(imageSpan.Slice(chOffset + y * infW, infW));
                        }
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

                // Crop the output back to the source chunk's actual h*w (drop
                // the replicate-padded right/bottom strip). Output buffers
                // belong to Chunk.Data which lives until Stitch runs, so we
                // can't pool these without restructuring Chunk's lifecycle --
                // leave them as fresh arrays for now.
                for (var c = 0; c < sourceChannels; c++)
                {
                    var outData = new float[h * w];
                    var chOffset = c * infPlaneStride;
                    for (var y = 0; y < h; y++)
                    {
                        outSpan.Slice(chOffset + y * infW, w).CopyTo(outData.AsSpan(y * w, w));
                    }
                    outputChunksByChannel[c][i] = refChunk with { Data = outData };
                }
            }
            // `pooled` returned to the pool on scope exit.
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
