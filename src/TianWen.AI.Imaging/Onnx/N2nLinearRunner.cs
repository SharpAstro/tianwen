using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TianWen.Lib;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// Chunked inference for the in-house Noise2Noise denoiser: the LINEAR-domain sibling of
/// <see cref="ChunkedNafnetRunner"/>. Shares its tiling helpers (<see cref="ChunkedInference"/>)
/// and its border convention, and differs in the three ways that made reusing it impossible.
/// </summary>
/// <remarks>
/// <para><b>No MTF round-trip.</b> <see cref="ChunkedNafnetRunner"/> opens with
/// <c>ApplyInputStretch</c>, because the AI4 NAFNets were trained on stretched inputs. This net
/// was trained on linear <c>[0, 1]</c> tiles taken straight from stacked masters, so stretching
/// first would be plain train/inference skew. The input is fed verbatim.</para>
///
/// <para><b>The conditioning is per tile, and lives inside the graph.</b> The model takes a
/// fourth plane holding the tile's own measured background sigma, which is what makes its
/// denoising strength an input rather than a constant baked in at training time. The exported
/// graph computes that plane itself from the tile it is handed, so this runner never touches it
/// -- deliberately. The alternative (host-computed sigma, passed alongside) invites computing it
/// once for the whole image, which still runs, still looks like a denoiser, and feeds the model a
/// number it never saw during training. <see cref="ChunkedNafnetRunner"/>'s <c>extraInputs</c>
/// could not express it either way: those are documented as reused across every chunk.</para>
///
/// <para><b>Fixed 256 px tiles, read off the graph.</b> This UNet has two pooling levels and needs
/// spatial dims divisible by 4, not NAFNet's 16 -- but the binding constraint is stricter than
/// either: the graph declares <c>[N, 3, 256, 256]</c> because the sigma estimator's support region
/// IS the tile (see <see cref="OnnxIoNames.ImageInputTileSize"/>). Every chunk is therefore padded
/// up to exactly the declared size, never to a multiple.</para>
///
/// <para><b>The user-facing strength is the blend, not the graph's <c>strength</c> input.</b>
/// Measured over four held-out sessions, lying to the model about sigma is the worse of the two
/// dials on three independent counts: it saturates well before "barely touch it" (at a 6.7x
/// understatement three of four observers still sat below the noise level the blend reaches at
/// a = 0.1), its reachable range varies by 4x between observers so one knob position means
/// different things on different data, and fabricated point sources RISE by 2.6x to 6.3x toward
/// its gentle end -- told its input is clean, the model reads noise as signal and sharpens it.
/// The blend is a convex combination of two images that already exist, so it is exactly monotone,
/// spans the full range to "untouched" by construction, and cannot invent. The graph input is
/// pinned to 1.0 here and kept only because removing it would mean re-exporting.</para>
/// </remarks>
internal static class N2nLinearRunner
{
    /// <summary>
    /// Run one denoise pass over <paramref name="input"/> and blend the result back toward it.
    /// </summary>
    /// <param name="input">Source image, linear and normalised to <c>[0, 1]</c> -- the same scale
    /// the trainer saw (<see cref="Image.UnitScaleDivisor"/>).</param>
    /// <param name="blend">User-facing strength in <c>(0, 1]</c>: the output is
    /// <c>input + blend * (denoised - input)</c>, so 1.0 is the model's full opinion and values
    /// below it walk back toward the untouched input.</param>
    /// <param name="overlap">Inter-chunk overlap; must be at least
    /// <c>2 * <see cref="AiNafnetInputs.StitchBorderPx"/></c> for the retained inner regions to
    /// abut without leaving a gap.</param>
    public static N2nRunResult Run(
        Image input,
        InferenceSession session,
        string imageInputName,
        string strengthInputName,
        string outputName,
        float blend,
        int overlap,
        CancellationToken ct = default)
    {
        var (channels, srcW, srcH) = input.Shape;
        var border = AiNafnetInputs.StitchBorderPx;

        // The tile size is the model's, not ours. A dynamic spatial axis would mean this graph is
        // not the one this runner was written for, so it is an error rather than a fallback.
        var (declaredH, declaredW) = OnnxIoNames.ImageInputTileSize(session, imageInputName);
        if (declaredH is not { } tileH || declaredW is not { } tileW)
        {
            throw new InvalidOperationException(
                $"N2nLinearRunner: '{imageInputName}' must declare a fixed tile size, got " +
                $"[{declaredH?.ToString() ?? "dynamic"}, {declaredW?.ToString() ?? "dynamic"}]. " +
                "The sigma-conditioning plane is measured over the tile, so the tile size is part " +
                "of the model contract.");
        }
        if (overlap < 2 * border)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlap), overlap,
                $"must be at least 2 * StitchBorderPx ({2 * border}) so the retained inner regions abut.");
        }

        var modelChannels = OnnxIoNames.ImageInputChannels(session, imageInputName, fallback: channels);
        if (channels != modelChannels)
        {
            throw new NotSupportedException(
                $"N2nLinearRunner: model takes {modelChannels} channels and the source has {channels}. " +
                "Unlike the AI4 family this net has no mono weight bundle and was trained purely on " +
                "one-shot-colour masters, so replicating a mono channel across the three slots would " +
                "be feeding it a distribution nobody has measured it on.");
        }

        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();

        // 1. Border the source so its own edges are covered by a chunk interior, then tile.
        var paddedChannels = new float[channels][];
        int paddedW = 0, paddedH = 0;
        for (var c = 0; c < channels; c++)
        {
            paddedChannels[c] = ChunkedInference.AddBorder(
                input.GetChannelSpan(c), srcW, srcH, border, out paddedW, out paddedH);
        }
        ct.ThrowIfCancellationRequested();

        var chunksByChannel = new ImmutableArray<ChunkedInference.Chunk>[channels];
        for (var c = 0; c < channels; c++)
        {
            chunksByChannel[c] = ChunkedInference.Split(paddedChannels[c], paddedW, paddedH, tileW, overlap);
        }
        var chunkCount = chunksByChannel[0].Length;
        var prepMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();

        // 2. Inference. Every chunk is fed at exactly the declared tile size; edge chunks come out
        //    of Split clipped to the source bounds, so they are replicate-padded up (never
        //    zero-padded -- a hard edge is structure the net would try to preserve).
        var outChunksByChannel = new ChunkedInference.Chunk[channels][];
        for (var c = 0; c < channels; c++) outChunksByChannel[c] = new ChunkedInference.Chunk[chunkCount];

        var planeStride = tileH * tileW;
        var strengthTensor = new DenseTensor<float>(new[] { 1.0f }.AsMemory(), ReadOnlySpan<int>.Empty);
        var strengthValue = NamedOnnxValue.CreateFromTensor(strengthInputName, strengthTensor);

        for (var i = 0; i < chunkCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var refChunk = chunksByChannel[0][i];
            var h = refChunk.Height;
            var w = refChunk.Width;
            if (h > tileH || w > tileW)
            {
                throw new InvalidOperationException(
                    $"N2nLinearRunner: chunk {i} is {w}x{h}, larger than the model tile {tileW}x{tileH}.");
            }

            using var pooled = ArrayPoolHelper.Rent<float>(channels * planeStride);
            var tensorMemory = pooled.AsMemory();
            var imageTensor = new DenseTensor<float>(tensorMemory, [1, channels, tileH, tileW]);
            var span = pooled.AsSpan();

            for (var c = 0; c < channels; c++)
            {
                var srcData = chunksByChannel[c][i].Data;
                var chOffset = c * planeStride;
                for (var y = 0; y < h; y++)
                {
                    var dstRow = chOffset + y * tileW;
                    srcData.AsSpan(y * w, w).CopyTo(span.Slice(dstRow, w));
                    if (tileW > w)
                    {
                        var rightmost = srcData[y * w + w - 1];
                        span.Slice(dstRow + w, tileW - w).Fill(rightmost);
                    }
                }
                if (tileH > h)
                {
                    var lastRow = span.Slice(chOffset + (h - 1) * tileW, tileW);
                    for (var y = h; y < tileH; y++)
                    {
                        lastRow.CopyTo(span.Slice(chOffset + y * tileW, tileW));
                    }
                }
            }

            using var result = session.Run(
            [
                NamedOnnxValue.CreateFromTensor(imageInputName, imageTensor),
                strengthValue,
            ]);
            var outSpan = result[0].AsTensor<float>().ToDenseTensor().Buffer.Span;

            for (var c = 0; c < channels; c++)
            {
                var srcChunk = chunksByChannel[c][i];
                var outData = new float[h * w];
                var chOffset = c * planeStride;
                for (var y = 0; y < h; y++)
                {
                    outSpan.Slice(chOffset + y * tileW, w).CopyTo(outData.AsSpan(y * w, w));
                }
                RestoreLevel(srcChunk.Data.AsSpan(0, h * w), outData);
                outChunksByChannel[c][i] = srcChunk with { Data = outData };
            }
        }
        var inferMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();

        // 3. Stitch, un-border, and blend back toward the input in one pass over the plane. The
        //    blend is applied here rather than per chunk because it is linear, so the two are
        //    identical and this way it is stated once.
        var outChannelData = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var stitched = new float[paddedW * paddedH];
            ChunkedInference.Stitch(outChunksByChannel[c], stitched, paddedW, paddedH, border);
            var unpadded = ChunkedInference.RemoveBorder(stitched, paddedW, paddedH, border);

            var plane = new float[srcH, srcW];
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], srcW * srcH);
            if (blend >= 1.0f)
            {
                unpadded.AsSpan().CopyTo(dst);
            }
            else
            {
                var src = input.GetChannelSpan(c);
                for (var k = 0; k < dst.Length; k++)
                {
                    dst[k] = src[k] + blend * (unpadded[k] - src[k]);
                }
            }
            outChannelData[c] = plane;
        }
        var stitchMs = phaseSw.ElapsedMilliseconds;

        var output = new Image(outChannelData, BitDepth.Float32, input.MaxValue, input.MinValue, 0f, input.ImageMeta);
        return new N2nRunResult(output, chunkCount, tileW, prepMs, inferMs, stitchMs, totalSw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Add back the constant that makes <paramref name="denoised"/> share
    /// <paramref name="source"/>'s median. Per channel, per chunk, in place.
    /// </summary>
    /// <remarks>
    /// <para><b>This is a correction for a measured defect, not a stylistic touch-up.</b> The net
    /// carries a learned prior about sky level from its eight training sessions and drags an input
    /// toward it: over 49 held-out tiles the shift in a channel's median correlates with that
    /// channel's input level at <b>-0.988</b>, and only -0.278 with its noise. Because the prior is
    /// per channel it does not land equally on R, G and B -- the worst held-out tile moved
    /// R +0.017, G +0.002, B +0.048, which is a heavy blue cast, not an offset. Any master whose
    /// sky sits below the training set's would have come out miscoloured.</para>
    ///
    /// <para><b>It cannot cost anything the model was selected on.</b> A per-channel constant moves
    /// neither a standard deviation nor a background sigma nor a local star amplitude: measured,
    /// the per-channel std changes by at most 3.7e-9 and the background sigma by 1.7e-7, i.e. by
    /// float round-off. So the frontier numbers this checkpoint was picked on are unaffected, and
    /// the correction is free.</para>
    ///
    /// <para><b>Per chunk rather than per image, because that is where the shift is produced.</b>
    /// The prior acts on each tile's own local level, so a single global offset would correct the
    /// average and leave the variation between tiles as a low-frequency stain. Correcting locally
    /// removes it where it is made, and the chunk overlap is averaged by
    /// <see cref="ChunkedInference.Stitch"/>, so neighbouring corrections blend rather than
    /// stepping at a seam.</para>
    ///
    /// <para>The median, not the mean: on an astro frame it is dominated by background rather than
    /// by however many stars the tile happens to hold, so a bright chunk and an empty one are
    /// corrected on the same footing.</para>
    /// </remarks>
    private static void RestoreLevel(ReadOnlySpan<float> source, Span<float> denoised)
    {
        using var scratch = ArrayPoolHelper.Rent<float>(source.Length * 2);
        var buffer = scratch.AsSpan();
        var srcScratch = buffer[..source.Length];
        var denScratch = buffer.Slice(source.Length, denoised.Length);
        source.CopyTo(srcScratch);
        denoised.CopyTo(denScratch);

        // MedianFast reorders its input, which is why both go through scratch copies.
        var offset = StatisticsHelper.MedianFast(srcScratch) - StatisticsHelper.MedianFast(denScratch);
        if (offset == 0f || !float.IsFinite(offset)) return;
        for (var i = 0; i < denoised.Length; i++) denoised[i] += offset;
    }
}

/// <summary>
/// Per-call result + timing breakdown from <see cref="N2nLinearRunner.Run"/>. There is no
/// stretch/unstretch pair here (this net is linear-domain), which is the visible difference from
/// <see cref="ChunkedNafnetResult"/>.
/// </summary>
internal sealed record N2nRunResult(
    Image Output,
    int ChunkCount,
    int TileSize,
    long PrepMs,
    long InferMs,
    long StitchMs,
    long TotalMs);
