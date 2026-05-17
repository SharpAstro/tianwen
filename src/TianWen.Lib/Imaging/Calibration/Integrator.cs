using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Options for the stack <see cref="Integrator"/>: rejector + combiner +
/// normalization controls. v1 in-memory integrator: caller pre-warps each
/// frame to a common reference grid via <see cref="Image.WarpToReferenceGridAsync"/>.
/// </summary>
/// <param name="Rejector">Per-pixel-column outlier rejector. <c>null</c>
/// skips rejection; every frame contributes to the combine.</param>
/// <param name="Combiner">How to combine the kept entries.
/// <c>null</c> defaults to <see cref="MeanCombiner"/>.</param>
/// <param name="ApplyNormalization">When true (default), each frame is
/// normalized to a common median (controlled by
/// <paramref name="NormalizationTarget"/>) before stacking. Disable only
/// when callers have already normalized the frames.</param>
/// <param name="NormalizationTarget">Target median for per-frame
/// normalization. 0.5 (half of [0, 1]) gives output stretched into the
/// middle of the dynamic range; <c>~0.25</c> is typical for unstretched
/// linear data.</param>
public sealed record IntegrationOptions(
    IPixelRejector? Rejector = null,
    IPixelCombiner? Combiner = null,
    bool ApplyNormalization = true,
    float NormalizationTarget = 0.5f);

/// <summary>
/// Result of a stack integration: the master image, a per-pixel rejection
/// fraction map, and aggregate stats. The rejection map is a single-channel
/// image whose pixel values are <c>rejected / total</c> for that output
/// position averaged across input channels.
/// </summary>
public sealed record IntegrationResult(
    Image Master,
    Image RejectionMap,
    int FrameCount,
    long TotalRejections,
    double MeanRejectionRate);

/// <summary>
/// Stack integrator. Combines N pre-aligned light frames into a single master
/// via optional per-pixel rejection + combine. v1 holds all frames in memory
/// (~1 GB for 30x 3008^2 RGB); Phase 10 will add the
/// <c>MemoryMappedFitsSink</c> path for big mosaic stacks that exceed RAM.
/// </summary>
/// <remarks>
/// Pre-condition: every input <see cref="Image"/> shares shape (width, height,
/// channel count) AND is warped to the same reference grid so pixel index N
/// maps to the same sky position across frames. The CLI orchestrator (Phase
/// 13) handles the calibrate + register + warp pipeline before calling here.
/// </remarks>
public static class Integrator
{
    /// <summary>
    /// Integrates <paramref name="alignedFrames"/> into a master image.
    /// Parallelized over output rows; each worker rents per-row scratch
    /// buffers from <see cref="ArrayPool{T}"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Empty frame list or shape mismatch
    /// across frames.</exception>
    public static IntegrationResult Integrate(
        IReadOnlyList<Image> alignedFrames,
        IntegrationOptions? options = null)
    {
        options ??= new IntegrationOptions();
        var combiner = options.Combiner ?? new MeanCombiner();
        var rejector = options.Rejector;

        ValidateInput(alignedFrames);

        var n = alignedFrames.Count;
        var (channelCount, width, height) = alignedFrames[0].Shape;

        // Snapshot the input channel arrays so the hot loop is plain indexing.
        // [channel][frame] -> float[h, w]
        var inputChannels = SnapshotChannelArrays(alignedFrames, channelCount, n);

        // Per-frame normalization scalars per channel: out = (in - min) * scale.
        // Computed once per frame; reused for all output pixels.
        var (frameMin, frameScale) = options.ApplyNormalization
            ? ComputeNormalizationScalars(alignedFrames, options.NormalizationTarget, n, channelCount)
            : (null, null);

        // Master canvas + single-channel rejection map (average rejection
        // fraction across channels per output pixel; useful for QA + masking
        // heavily-rejected regions downstream). Both go through IIntegrationSink
        // so Phase 10's MemoryMappedFitsSink can drop in without touching the
        // hot loop. ArraySink is today's behaviour: heap float[][,].
        using var masterSink = new ArraySink(channelCount, width, height);
        using var rejectSink = new ArraySink(1, width, height);

        long totalRejections = 0;

        // One channel at a time. Parallel.For over rows of the current channel.
        for (var ch = 0; ch < channelCount; ch++)
        {
            var channelIdx = ch;
            var channelInputs = inputChannels[channelIdx];
            var minForCh = frameMin?[channelIdx];
            var scaleForCh = frameScale?[channelIdx];

            Parallel.For(0, height,
                localInit: () => new RowState
                {
                    Column = ArrayPool<float>.Shared.Rent(n),
                    KeepMask = ArrayPool<float>.Shared.Rent(n),
                    Rejections = 0,
                },
                body: (row, _, state) =>
                {
                    var column = state.Column;
                    var keepMask = state.KeepMask;
                    var columnSpan = column.AsSpan(0, n);
                    var maskSpan = keepMask.AsSpan(0, n);
                    // Sink row spans fetched once per row (sink-internal pointer
                    // arithmetic, not a per-pixel cost). Master span is written;
                    // reject span is read-modify-write to accumulate across
                    // channels (final /= channelCount happens below the channel loop).
                    var masterRow = masterSink.GetRow(channelIdx, row);
                    var rejectRow = rejectSink.GetRow(0, row);

                    for (var col = 0; col < width; col++)
                    {
                        // Fill column with normalized values (or raw, if normalization disabled).
                        for (var f = 0; f < n; f++)
                        {
                            var v = channelInputs[f][row, col];
                            if (!float.IsNaN(v) && minForCh is not null)
                            {
                                v = (v - minForCh[f]) * scaleForCh![f];
                            }
                            column[f] = v;
                        }

                        int kept;
                        if (rejector is not null)
                        {
                            kept = rejector.Reject(columnSpan, maskSpan);
                            state.Rejections += n - kept;
                        }
                        else
                        {
                            maskSpan.Fill(1f);
                            kept = n;
                        }

                        masterRow[col] = combiner.Combine(columnSpan, maskSpan);

                        // Accumulate per-pixel rejection rate across channels.
                        // Final divide by channelCount happens after the loop.
                        if (rejector is not null)
                        {
                            rejectRow[col] += (n - kept) / (float)n;
                        }
                    }
                    return state;
                },
                localFinally: state =>
                {
                    ArrayPool<float>.Shared.Return(state.Column);
                    ArrayPool<float>.Shared.Return(state.KeepMask);
                    Interlocked.Add(ref totalRejections, state.Rejections);
                });
        }

        // Average the rejection fraction across channels.
        if (rejector is not null && channelCount > 1)
        {
            var inv = 1f / channelCount;
            for (var y = 0; y < height; y++)
            {
                var rejectRow = rejectSink.GetRow(0, y);
                for (var x = 0; x < width; x++)
                {
                    rejectRow[x] *= inv;
                }
            }
        }

        var meanRate = n > 0
            ? (double)totalRejections / ((double)n * width * height * channelCount)
            : 0.0;

        var firstMeta = alignedFrames[0].ImageMeta;
        var masterImage = masterSink.FinaliseAsImage(
            BitDepth.Float32,
            maxValue: alignedFrames[0].MaxValue,
            minValue: 0f,
            pedestal: alignedFrames[0].Pedestal,
            meta: firstMeta);
        var rejectMapImage = rejectSink.FinaliseAsImage(
            BitDepth.Float32,
            maxValue: 1f,
            minValue: 0f,
            pedestal: 0f,
            meta: firstMeta);

        return new IntegrationResult(masterImage, rejectMapImage, n, totalRejections, meanRate);
    }

    private struct RowState
    {
        public float[] Column;
        public float[] KeepMask;
        public long Rejections;
    }

    private static void ValidateInput(IReadOnlyList<Image> frames)
    {
        if (frames is null || frames.Count == 0)
        {
            throw new ArgumentException("Integrator needs at least one frame.", nameof(frames));
        }
        var first = frames[0];
        var (c, w, h) = first.Shape;
        for (var i = 1; i < frames.Count; i++)
        {
            var s = frames[i].Shape;
            if (s.ChannelCount != c || s.Width != w || s.Height != h)
            {
                throw new ArgumentException(
                    $"Frame {i} shape mismatch: expected {c}x{h}x{w}, got {s.ChannelCount}x{s.Height}x{s.Width}.",
                    nameof(frames));
            }
        }
    }

    private static float[][][,] SnapshotChannelArrays(IReadOnlyList<Image> frames, int channelCount, int frameCount)
    {
        // [channel][frame] -> float[height, width]
        var snapshot = new float[channelCount][][,];
        for (var ch = 0; ch < channelCount; ch++)
        {
            snapshot[ch] = new float[frameCount][,];
            for (var f = 0; f < frameCount; f++)
            {
                snapshot[ch][f] = frames[f].GetChannelArray(ch);
            }
        }
        return snapshot;
    }

    private static (float[][] Min, float[][] Scale) ComputeNormalizationScalars(
        IReadOnlyList<Image> frames, float target, int frameCount, int channelCount)
    {
        // Shape: [channel][frame] -> scalar.
        var min = new float[channelCount][];
        var scale = new float[channelCount][];
        for (var ch = 0; ch < channelCount; ch++)
        {
            min[ch] = new float[frameCount];
            scale[ch] = new float[frameCount];
        }

        for (var f = 0; f < frameCount; f++)
        {
            var stats = Normalizer.ComputeStats(frames[f]);
            for (var ch = 0; ch < channelCount; ch++)
            {
                var mn = stats.PerChannelMin[ch];
                var md = stats.PerChannelMedian[ch];
                min[ch][f] = mn;
                scale[ch][f] = md > mn ? target / (md - mn) : 1f;
            }
        }
        return (min, scale);
    }
}
