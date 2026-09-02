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
/// Chunked inference for the in-house Noise2Noise denoiser: linear <c>[0, 1]</c> in, linear out, and
/// the same MTF round-trip as <see cref="ChunkedNafnetRunner"/> in between. Shares its tiling helpers
/// (<see cref="ChunkedInference"/>) and its border convention, and differs in the two ways that made
/// reusing it impossible: the conditioning plane and the fixed tile.
/// </summary>
/// <remarks>
/// <para><b>The MTF round-trip is the exporter's, and it is the one preprocessing step there is.</b>
/// <c>DatasetTileExporter</c> stores every training tile after
/// <see cref="ChunkedNafnetRunner.ApplyInputStretch"/> (one whole-frame, per-channel stretch to a
/// median of 0.25; the trainer reads the bytes as stored), so that same call on the whole input,
/// before chunking, is what puts a frame in the domain the net was trained in, and
/// <see cref="Image.MtfUnstretch"/> with the parameters it returned brings the answer back to the
/// input's units before the blend. Until 2026-09-02 this runner fed a linear frame verbatim on the
/// belief that the net had "trained on linear tiles", which put a real master about 100x below the
/// level and the sigma of every tile it had seen. Measured on the 163-sub Bubble master
/// (<c>N2nSeamProbe</c>, one process, same file): the verbatim path removed 10 / 9 / 17 percent of
/// the noise (R/G/B), cut EVERY star's peak by about 30 percent whatever its brightness (amplitude
/// kept 0.70, flat from SNR 8 to 100+), and the level prior dragged each chunk by a median 0.074 in
/// linear units on a sky of 0.0019, some 740 input MADs that <see cref="RestoreLevel"/> then put
/// back (the reason the PR #184 seams were as large as they were). Through the exporter's stretch the
/// same weights remove 13 / 23 / 36 percent, keep 0.73 of a faint star's amplitude rising to 0.93 at
/// the bright end, move no background pixel by more than 10 MAD, and the per-chunk drag falls to
/// 0.0029 on a level of 0.25. That is hypothesis H0 of <c>docs/plans/denoiser-training.md</c>,
/// confirmed; the per-SNR table is in its run log.</para>
///
/// <para>The auto-detect inside <c>ApplyInputStretch</c> is part of the contract, not a convenience:
/// a frame whose median already sits above 0.125 is fed as it is, exactly as the exporter would have
/// stored it, so a pre-stretched input takes the same path in training and here, and a frame this
/// runner has stretched itself is not stretched twice.</para>
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
    /// the exporter normalised its frames to before stretching them
    /// (<see cref="Image.UnitScaleDivisor"/>).</param>
    /// <param name="blend">User-facing strength in <c>(0, 1]</c>: the output is
    /// <c>input + blend * (denoised - input)</c>, so 1.0 is the model's full opinion and values
    /// below it walk back toward the untouched input.</param>
    /// <param name="overlap">Inter-chunk overlap; must be at least
    /// <c>2 * <see cref="AiNafnetInputs.StitchBorderPx"/></c> for the retained inner regions to
    /// abut without leaving a gap. What is left over above that minimum,
    /// <c>overlap - 2 * border</c>, is the band the retained regions SHARE, and it is passed to
    /// <see cref="ChunkedInference.Stitch"/> as the feather width -- so at exactly the minimum the
    /// regions merely touch, there is nothing to blend across, and
    /// <see cref="RestoreLevel"/>'s per-chunk offsets step at the join. The default 64 against a
    /// 16 px border leaves 32 px to ramp over.</param>
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

        // 0. Into the training domain: the exporter's whole-frame stretch, or nothing when the
        //    auto-detect says the frame is already there (see the class remarks).
        var (stretched, stretchApplied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(input);
        var stretchMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();
        ct.ThrowIfCancellationRequested();

        // 1. Border the stretched frame so its own edges are covered by a chunk interior, then tile.
        var paddedChannels = new float[channels][];
        int paddedW = 0, paddedH = 0;
        for (var c = 0; c < channels; c++)
        {
            paddedChannels[c] = ChunkedInference.AddBorder(
                stretched.GetChannelSpan(c), srcW, srcH, border, out paddedW, out paddedH);
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
        // |offset| per (channel, chunk) from RestoreLevel, kept for the result: how far the net's
        // level prior dragged each tile is the number that says whether the input sat in the
        // net's training band (see the remarks on RestoreLevel).
        var levelOffsets = new float[channels * chunkCount];

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
                levelOffsets[c * chunkCount + i] = MathF.Abs(RestoreLevel(srcChunk.Data.AsSpan(0, h * w), outData));
                outChunksByChannel[c][i] = srcChunk with { Data = outData };
            }
        }
        var inferMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();
        var levelOffsetMax = 0f;
        foreach (var offset in levelOffsets) levelOffsetMax = MathF.Max(levelOffsetMax, offset);
        var levelOffsetMedian = StatisticsHelper.MedianFast(levelOffsets);

        // 3. Stitch and un-border, per channel, still in the net's domain.
        var inferredData = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var stitched = new float[paddedW * paddedH];
            ChunkedInference.Stitch(outChunksByChannel[c], stitched, paddedW, paddedH, border,
                featherPx: overlap - 2 * border);
            var unpadded = ChunkedInference.RemoveBorder(stitched, paddedW, paddedH, border);
            var plane = new float[srcH, srcW];
            unpadded.AsSpan().CopyTo(MemoryMarshal.CreateSpan(ref plane[0, 0], srcW * srcH));
            inferredData[c] = plane;
        }
        var stitchMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();

        // 4. Back to the input's units with the parameters the stretch returned, exactly as
        //    ChunkedNafnetRunner does; a frame that was fed as it is comes back as it is.
        var inferred = new Image(inferredData, BitDepth.Float32, 1.0f, 0f, 0f, input.ImageMeta);
        var restored = stretchApplied ? inferred.MtfUnstretch(origMin!, balances!) : inferred;

        // 5. Blend back toward the input, in the input's units, in one pass over the plane. The
        //    blend is applied here rather than per chunk because it is linear, so the two are
        //    identical and this way it is stated once.
        var outChannelData = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[srcH, srcW];
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], srcW * srcH);
            var denoised = restored.GetChannelSpan(c);
            if (blend >= 1.0f)
            {
                denoised.CopyTo(dst);
            }
            else
            {
                var src = input.GetChannelSpan(c);
                for (var k = 0; k < dst.Length; k++)
                {
                    dst[k] = src[k] + blend * (denoised[k] - src[k]);
                }
            }
            outChannelData[c] = plane;
        }
        var unstretchMs = phaseSw.ElapsedMilliseconds;

        var output = new Image(outChannelData, BitDepth.Float32, input.MaxValue, input.MinValue, 0f, input.ImageMeta);
        return new N2nRunResult(
            output, chunkCount, tileW, stretchApplied, stretchMs, prepMs, inferMs, stitchMs, unstretchMs,
            totalSw.ElapsedMilliseconds, levelOffsetMedian, levelOffsetMax);
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
    /// removes it where it is made.</para>
    ///
    /// <para><b>It is therefore this method that makes the stitch feather load-bearing, and the
    /// two must be read together.</b> Neighbouring chunks measure different pixels, so they get
    /// different offsets BY CONSTRUCTION -- that is the point of correcting locally, not a defect.
    /// An unweighted overlap average does not smooth that difference out: it renders a difference
    /// of D as two hard edges of D/2 at the shared band's boundaries, which measured 1.0x the
    /// background sigma (111x the local column-to-column variation) on a real master and read as a
    /// grid at the chunk stride. <see cref="ChunkedInference.Stitch"/> is passed a feather width
    /// for exactly this reason. Widening the correction's scope is NOT the alternative fix: a
    /// global offset reintroduces the stain this is here to remove.</para>
    ///
    /// <para>The median, not the mean: on an astro frame it is dominated by background rather than
    /// by however many stars the tile happens to hold, so a bright chunk and an empty one are
    /// corrected on the same footing.</para>
    /// </remarks>
    /// <returns>The offset that was added, 0 when nothing was (a non-finite median leaves the chunk alone).</returns>
    private static float RestoreLevel(ReadOnlySpan<float> source, Span<float> denoised)
    {
        using var scratch = ArrayPoolHelper.Rent<float>(source.Length * 2);
        var buffer = scratch.AsSpan();
        var srcScratch = buffer[..source.Length];
        var denScratch = buffer.Slice(source.Length, denoised.Length);
        source.CopyTo(srcScratch);
        denoised.CopyTo(denScratch);

        // MedianFast reorders its input, which is why both go through scratch copies.
        var offset = StatisticsHelper.MedianFast(srcScratch) - StatisticsHelper.MedianFast(denScratch);
        if (offset == 0f || !float.IsFinite(offset)) return 0f;
        for (var i = 0; i < denoised.Length; i++) denoised[i] += offset;
        return offset;
    }
}

/// <summary>
/// Per-call result + timing breakdown from <see cref="N2nLinearRunner.Run"/>, the shape of
/// <see cref="ChunkedNafnetResult"/> plus the two level-restore statistics. <see cref="StretchApplied"/>
/// is the auto-detect's decision (false for an input already in the training band);
/// <see cref="UnstretchMs"/> covers the inverse MTF and the blend, which share a pass.
/// <see cref="LevelOffsetMedianAbs"/> / <see cref="LevelOffsetMaxAbs"/> are |offset| over every
/// (channel, chunk) that <c>RestoreLevel</c> added, in the domain the net saw: a drag that is a
/// large fraction of the level says the input sat outside the training band, which is how the
/// verbatim-input defect would have been visible in a log line had this existed then.
/// </summary>
internal sealed record N2nRunResult(
    Image Output,
    int ChunkCount,
    int TileSize,
    bool StretchApplied,
    long StretchMs,
    long PrepMs,
    long InferMs,
    long StitchMs,
    long UnstretchMs,
    long TotalMs,
    float LevelOffsetMedianAbs,
    float LevelOffsetMaxAbs);
