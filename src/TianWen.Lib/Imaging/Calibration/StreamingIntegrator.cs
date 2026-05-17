using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// A staged aligned frame for the <see cref="StreamingIntegrator"/>. Owns a
/// <see cref="StreamingFrameReader"/> handle plus pre-computed per-channel
/// normalisation stats (median / min) so the integrator never has to load a full
/// frame into RAM to derive them.
/// </summary>
/// <remarks>
/// Construct these at warp time, while the warped <see cref="Image"/> is still
/// in RAM, then drop the Image. The reader holds only an open
/// <see cref="System.IO.FileStream"/>; the stats are a handful of floats.
/// Caller is responsible for disposing (closes the file handle and -- if the
/// staging file is in a temp dir owned by the caller -- the file is unlinked
/// when the caller cleans up the dir).
/// </remarks>
public sealed class StagedAlignedFrame : IDisposable
{
    /// <summary>The reader handle for the staging file.</summary>
    public StreamingFrameReader Reader { get; }
    /// <summary>Per-channel min, length = channel count. <c>null</c> = no normalisation.</summary>
    public float[]? PerChannelMin { get; }
    /// <summary>Per-channel median, length = channel count. <c>null</c> = no normalisation.</summary>
    public float[]? PerChannelMedian { get; }
    /// <summary>The image's <see cref="ImageMeta"/> at warp time. Carried so the
    /// integrator can stamp the output master with metadata without re-reading
    /// any frame.</summary>
    public ImageMeta Meta { get; }
    /// <summary>The image's <see cref="Image.MaxValue"/> at warp time -- carried
    /// for the same reason as <see cref="Meta"/>.</summary>
    public float MaxValue { get; }
    /// <summary>The image's <see cref="Image.Pedestal"/> at warp time.</summary>
    public float Pedestal { get; }

    public StagedAlignedFrame(
        StreamingFrameReader reader,
        ImageMeta meta,
        float maxValue,
        float pedestal,
        float[]? perChannelMin = null,
        float[]? perChannelMedian = null)
    {
        Reader = reader;
        Meta = meta;
        MaxValue = maxValue;
        Pedestal = pedestal;
        PerChannelMin = perChannelMin;
        PerChannelMedian = perChannelMedian;
    }

    public void Dispose() => Reader.Dispose();
}

/// <summary>
/// Streaming counterpart to <see cref="Integrator"/>. Reads each frame from a
/// <see cref="StreamingFrameStaging"/> file row-stripe by row-stripe so peak RAM
/// is bounded by the stripe buffer, not the total frame count. Same per-column
/// algorithm: optional rejector (<see cref="IPixelRejector"/>), combiner
/// (<see cref="IPixelCombiner"/>), and per-frame normalisation. Output master is
/// assembled in memory (a single full-res image, ~108 MB for 3008^2 RGB) and
/// returned alongside a single-channel rejection map.
/// </summary>
public static class StreamingIntegrator
{
    /// <summary>Default stripe height in rows. 64 keeps the per-stripe buffer
    /// at ~568 MB for 244 frames of 3008-wide RGB, which is comfortable on
    /// 8+ GB machines. Increase for fewer seeks at higher RAM cost.</summary>
    public const int DefaultStripeHeight = 64;

    /// <summary>Integrates <paramref name="alignedFrames"/> into a master image
    /// without ever holding more than <paramref name="stripeHeight"/> rows of
    /// pixel data from all frames in memory simultaneously.</summary>
    public static IntegrationResult Integrate(
        IReadOnlyList<StagedAlignedFrame> alignedFrames,
        IntegrationOptions? options = null,
        int stripeHeight = DefaultStripeHeight)
    {
        options ??= new IntegrationOptions();
        var combiner = options.Combiner ?? new MeanCombiner();
        var rejector = options.Rejector;

        ValidateInput(alignedFrames);

        var n = alignedFrames.Count;
        var first = alignedFrames[0].Reader;
        var channelCount = first.Channels;
        var width = first.Width;
        var height = first.Height;

        // Pre-compute per-frame normalisation scalars from the stats carried on
        // the staged frames. Shape [channel][frame] -> scalar, same as the
        // in-memory Integrator.
        float[][]? frameMin = null;
        float[][]? frameScale = null;
        if (options.ApplyNormalization && alignedFrames[0].PerChannelMedian is not null)
        {
            frameMin = new float[channelCount][];
            frameScale = new float[channelCount][];
            for (var ch = 0; ch < channelCount; ch++)
            {
                frameMin[ch] = new float[n];
                frameScale[ch] = new float[n];
            }
            for (var f = 0; f < n; f++)
            {
                var staged = alignedFrames[f];
                var mins = staged.PerChannelMin
                    ?? throw new ArgumentException($"Frame {f} has Median stats but no Min stats.");
                var meds = staged.PerChannelMedian!;
                for (var ch = 0; ch < channelCount; ch++)
                {
                    var mn = mins[ch];
                    var md = meds[ch];
                    frameMin[ch][f] = mn;
                    frameScale[ch][f] = md > mn ? options.NormalizationTarget / (md - mn) : 1f;
                }
            }
        }

        // Master canvas + reject map both flow through IIntegrationSink so
        // Phase 10's MemoryMappedFitsSink can substitute without touching the
        // stripe / row inner loops. ArraySink is today's heap-backed behaviour.
        using var masterSink = new ArraySink(channelCount, width, height);
        using var rejectSink = new ArraySink(1, width, height);

        long totalRejections = 0;

        // Stripe buffer reused across stripes. Shape: stripe row-major within
        // each (channel, frame): stripeRows * width floats per (channel, frame).
        // Stored as flat float[n] per (channel, stripeRowGlobal) -- we walk one
        // channel at a time so the same buffer slot serves every channel.
        // Total allocation: stripeHeight * width * n floats * sizeof(float).
        var stripeBufferSize = (long)stripeHeight * width * n;
        if (stripeBufferSize > int.MaxValue)
        {
            throw new ArgumentException(
                $"Stripe buffer would exceed int.MaxValue floats (stripeHeight={stripeHeight}, width={width}, n={n}). " +
                "Lower stripeHeight or split the run.");
        }
        var stripeBuffer = new float[stripeBufferSize];

        for (var ch = 0; ch < channelCount; ch++)
        {
            var channelIdx = ch;
            var minForCh = frameMin?[channelIdx];
            var scaleForCh = frameScale?[channelIdx];

            for (var stripeStart = 0; stripeStart < height; stripeStart += stripeHeight)
            {
                var stripeRows = Math.Min(stripeHeight, height - stripeStart);

                // Read [stripeRows x width] from each frame for this channel.
                // Each frame's stripe goes to buffer[f * stripeRows * width .. (f+1) * stripeRows * width].
                // Disk-bound; could parallelise across frames, but per-frame
                // mutex inside StreamingFrameReader would serialise anyway.
                var rowFloats = stripeRows * width;
                for (var f = 0; f < n; f++)
                {
                    var offset = f * rowFloats;
                    alignedFrames[f].Reader.ReadStripe(channelIdx, stripeStart, stripeRows, stripeBuffer.AsSpan(offset, rowFloats));
                }

                // Capture for closure.
                var sStart = stripeStart;
                var sRows = stripeRows;

                Parallel.For(0, sRows,
                    localInit: () => new RowState
                    {
                        Column = ArrayPool<float>.Shared.Rent(n),
                        KeepMask = ArrayPool<float>.Shared.Rent(n),
                        Rejections = 0,
                    },
                    body: (stripeRow, _, state) =>
                    {
                        var column = state.Column;
                        var keepMask = state.KeepMask;
                        var columnSpan = column.AsSpan(0, n);
                        var maskSpan = keepMask.AsSpan(0, n);
                        var globalRow = sStart + stripeRow;
                        var rowBase = stripeRow * width;
                        var masterRow = masterSink.GetRow(channelIdx, globalRow);
                        var rejectRow = rejectSink.GetRow(0, globalRow);

                        for (var col = 0; col < width; col++)
                        {
                            for (var f = 0; f < n; f++)
                            {
                                var v = stripeBuffer[f * rowFloats + rowBase + col];
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
        }

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

        var firstFrame = alignedFrames[0];
        var masterImage = masterSink.FinaliseAsImage(
            BitDepth.Float32,
            maxValue: firstFrame.MaxValue,
            minValue: 0f,
            pedestal: firstFrame.Pedestal,
            meta: firstFrame.Meta);
        var rejectMapImage = rejectSink.FinaliseAsImage(
            BitDepth.Float32,
            maxValue: 1f,
            minValue: 0f,
            pedestal: 0f,
            meta: firstFrame.Meta);

        return new IntegrationResult(masterImage, rejectMapImage, n, totalRejections, meanRate);
    }

    private struct RowState
    {
        public float[] Column;
        public float[] KeepMask;
        public long Rejections;
    }

    private static void ValidateInput(IReadOnlyList<StagedAlignedFrame> frames)
    {
        if (frames is null || frames.Count == 0)
        {
            throw new ArgumentException("StreamingIntegrator needs at least one frame.", nameof(frames));
        }
        var first = frames[0].Reader;
        for (var i = 1; i < frames.Count; i++)
        {
            var r = frames[i].Reader;
            if (r.Channels != first.Channels || r.Width != first.Width || r.Height != first.Height)
            {
                throw new ArgumentException(
                    $"Frame {i} shape mismatch: expected {first.Channels}x{first.Height}x{first.Width}, " +
                    $"got {r.Channels}x{r.Height}x{r.Width}.",
                    nameof(frames));
            }
        }
    }
}
