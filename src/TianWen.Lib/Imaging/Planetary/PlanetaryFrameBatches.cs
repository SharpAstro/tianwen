using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A stream's frames taken a batch at a time: each batch loaded in order on the caller's thread (a stream decodes through one
/// reader), its frames PREPARED side by side (what is each frame's own: its grade, its shift, its mesh, its sharpness map), then
/// FOLDED one by one in the order given, on the caller's thread. A stack's sum gives the same bits only in its own order, and it
/// keeps it; what ran on one core between two folds is what runs side by side.
/// </summary>
/// <remarks>
/// The batch stack ran on one core (235 s for 3,000 frames on 2026-10-02, 1.00 cores measured), and once its folds ran in bands of
/// rows (<see cref="ParallelFor.RunBands"/>) the work between them was what kept it from the rest. A batch is at most one frame a
/// core, and fewer where its frames and what is made of them would pass <see cref="BatchBytes"/>, so a batch of large colour frames
/// is not a gigabyte. Linear in the frames, as everything the stack does must be.
/// </remarks>
internal static class PlanetaryFrameBatches
{
    /// <summary>What a batch's frames may hold at most, twice each frame for what is made of it.</summary>
    internal const long BatchBytes = 256L * 1024 * 1024;

    /// <summary>The most frames a batch holds, which a caller sizes its per-slot state by (one aligner twin a slot).</summary>
    internal static int MaxSlots => Environment.ProcessorCount;

    /// <summary>How many frames a batch of frames this size holds.</summary>
    internal static int BatchSize(int width, int height, int channels)
    {
        var frameBytes = Math.Max(1L, (long)width * height * Math.Max(1, channels) * sizeof(float));
        return (int)Math.Clamp(BatchBytes / (2 * frameBytes), 1, MaxSlots);
    }

    /// <summary>
    /// Loads <paramref name="indices"/> in order, prepares each by <paramref name="prepare"/> (the frame, its index and the slot
    /// it is prepared in, below <see cref="MaxSlots"/>: a slot is one thread's at a time, for state a thread may not share), then
    /// folds each by <paramref name="fold"/> in the order of <paramref name="indices"/>. Every frame is released once folded.
    /// </summary>
    public static async Task RunAsync<T>(IPlanetaryFrameStream stream, ImmutableArray<int> indices, Func<Image, int, int, T> prepare,
        Action<Image, int, T> fold, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(fold);

        Image[]? frames = null;
        T[]? prepared = null;
        var start = 0;
        while (start < indices.Length)
        {
            var loaded = 0;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = await stream.LoadAsync(indices[start], cancellationToken).ConfigureAwait(false);
                // Sized off the first frame, the stream's frames being alike.
                frames ??= new Image[BatchSize(first.Width, first.Height, first.ChannelCount)];
                prepared ??= new T[frames.Length];
                frames[0] = first;
                loaded = 1;
                var count = Math.Min(frames.Length, indices.Length - start);
                for (; loaded < count; loaded++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    frames[loaded] = await stream.LoadAsync(indices[start + loaded], cancellationToken).ConfigureAwait(false);
                }

                var (batchFrames, batchPrepared, batchStart) = (frames, prepared, start);
                ParallelFor.Run(count, i => batchPrepared[i] = prepare(batchFrames[i], indices[batchStart + i], i));
                for (var i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fold(frames[i], indices[start + i], prepared[i]);
                }

                start += count;
            }
            finally
            {
                for (var i = 0; i < loaded; i++)
                {
                    frames?[i].Release();
                }
                if (frames is not null)
                {
                    Array.Clear(frames);
                }
                if (prepared is not null)
                {
                    Array.Clear(prepared);
                }
            }
        }
    }
}
