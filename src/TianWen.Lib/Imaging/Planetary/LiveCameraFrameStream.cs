using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// An <see cref="IPlanetaryFrameStream"/> backed by a <b>live camera push-stream</b> instead of a file --
/// the "wire it so a camera plugs in" seam the planetary plan reserved. A capture loop pushes frames as
/// they arrive (<see cref="Push"/>); the same <see cref="RollingWindowStacker"/> / preview pipeline that
/// consumes a <see cref="SerFrameStream"/> consumes this one unchanged, blind to the live source.
/// <para>
/// <b>Bounded ring, copy-on-push.</b> Frames are kept in a fixed-capacity ring (the rolling window only
/// ever looks back <c>MaxWindowFrames</c>, so older frames are never needed). Each pushed frame is
/// <b>deep-copied</b> into a ring-owned, non-pooled <see cref="Image"/>: the camera recycles its own
/// buffer for the next frame, so the stream cannot hold the camera's array. Because the copy carries no
/// <c>ChannelBuffer</c>, the stacker's <see cref="Image.Release"/> on a loaded frame is a no-op, so
/// <see cref="LoadAsync"/> can safely hand back the <i>shared</i> ring reference (no per-load copy) --
/// the frame is immutable once pushed.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Push"/> runs on the capture loop; <see cref="LoadAsync"/> /
/// <see cref="FrameCount"/> / <see cref="TimestampOf"/> run on the stacker's background task. All ring
/// access is guarded by one <see cref="Lock"/>; the work outside the lock (the deep copy on push) touches
/// only caller-local arrays. <see cref="FrameCount"/> grows monotonically with the number of frames ever
/// pushed; only the last <see cref="Capacity"/> are retained.
/// </para>
/// </summary>
public sealed class LiveCameraFrameStream : IPlanetaryFrameStream
{
    private readonly Lock _gate = new();
    private readonly Image?[] _ring;
    private readonly DateTimeOffset?[] _timestamps;
    private int _count;       // total frames ever pushed; == FrameCount, grows monotonically
    private bool _disposed;

    /// <summary>
    /// Creates a live frame stream. <paramref name="width"/> / <paramref name="height"/> are the per-plane
    /// dimensions the pushed frames carry (halved already for a <see cref="PlanetaryFrameLayout.SplitCfa"/>
    /// source -- the caller applies any Bayer split before pushing, mirroring <see cref="SerFrameStream"/>).
    /// <paramref name="capacity"/> must be at least the rolling window's <c>MaxWindowFrames</c> (plus a
    /// little margin for eviction) so the stacker never asks for a frame that has rolled out of the ring.
    /// </summary>
    public LiveCameraFrameStream(
        int width, int height, PlanetaryFrameLayout layout, int capacity = 1024, bool hasTimestamps = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Width = width;
        Height = height;
        Layout = layout;
        HasTimestamps = hasTimestamps;
        _ring = new Image?[capacity];
        _timestamps = new DateTimeOffset?[capacity];
    }

    /// <summary>Ring capacity: the maximum number of recent frames retained for loading.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Index of the most recently pushed frame, or <c>-1</c> before the first push.</summary>
    public int LatestIndex
    {
        get { lock (_gate) { return _count - 1; } }
    }

    /// <inheritdoc/>
    public int FrameCount
    {
        get { lock (_gate) { return _count; } }
    }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PlanetaryFrameLayout Layout { get; }

    /// <inheritdoc/>
    public bool HasTimestamps { get; }

    /// <summary>
    /// Pushes a freshly-captured frame onto the stream (capture-loop thread). The frame is deep-copied into
    /// a ring-owned image, so the caller may immediately <see cref="Image.Release"/> / reuse
    /// <paramref name="frame"/>. The frame must match the stream's plane dimensions.
    /// </summary>
    public void Push(Image frame, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != Width || frame.Height != Height)
        {
            throw new ArgumentException(
                $"Pushed frame is {frame.Width}x{frame.Height} but the stream expects {Width}x{Height}.", nameof(frame));
        }

        // Deep-copy OUTSIDE the lock: the camera recycles its buffer for the next frame, so the ring must
        // own independent arrays. The copy carries no ChannelBuffer -> Release() on a loaded frame is a no-op.
        var copy = DeepCopy(frame);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var slot = _count % _ring.Length;
            _ring[slot] = copy;
            _timestamps[slot] = HasTimestamps ? timestamp : null;
            _count++;
        }
    }

    /// <inheritdoc/>
    public DateTimeOffset? TimestampOf(int index)
    {
        if (!HasTimestamps)
        {
            return null;
        }

        lock (_gate)
        {
            return IsRetained(index) ? _timestamps[index % _ring.Length] : null;
        }
    }

    /// <inheritdoc/>
    public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Image image;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsRetained(index) || _ring[index % _ring.Length] is not { } slot)
            {
                var oldest = Math.Max(0, _count - _ring.Length);
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Frame {index} is not in the live ring (retained [{oldest}, {_count - 1}], capacity {_ring.Length}).");
            }
            image = slot;
        }

        // Ring-owned, immutable, no ChannelBuffer: the stacker's Release() is a no-op, so sharing the
        // reference is safe and avoids a per-load copy. The slot won't be overwritten for Capacity more
        // pushes, far beyond the rolling window the stacker touches.
        return ValueTask.FromResult(image);
    }

    // A frame index is loadable iff it has been pushed and has not yet rolled out of the ring.
    private bool IsRetained(int index) => index >= 0 && index < _count && index >= _count - _ring.Length;

    private static Image DeepCopy(Image src)
    {
        var channels = src.ChannelCount;
        var dst = Image.CreateChannelData(channels, src.Height, src.Width);

        // The planetary stack pipeline operates in [0,1] (PlanetaryMaster.NormalizeInPlace declares the
        // master MaxValue = 1, and the SER bridge decodes raw frames straight to [0,1]). A live camera,
        // however, delivers ADU (MaxValue = sensor full-scale), so normalise the owned copy to [0,1] here.
        // Without this the coverage-normalised master keeps ADU values while declaring MaxValue = 1, and the
        // viewer clamps every pixel to white -> a flat, structureless frame. Already-[0,1] sources (SER,
        // MaxValue <= 1) pass through unscaled (scale == 1).
        var scale = src.MaxValue > 1f ? 1f / src.MaxValue : 1f;
        for (var c = 0; c < channels; c++)
        {
            var plane = src.GetChannelArray(c);
            var outPlane = dst[c];
            if (scale == 1f)
            {
                Array.Copy(plane, outPlane, plane.Length);
            }
            else
            {
                var srcSpan = MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
                var dstSpan = MemoryMarshal.CreateSpan(ref outPlane[0, 0], outPlane.Length);
                for (var i = 0; i < srcSpan.Length; i++)
                {
                    dstSpan[i] = srcSpan[i] * scale;
                }
            }
        }

        // After scaling, the data is fractional [0,1] floats regardless of the source bit depth.
        var bitDepth = scale == 1f ? src.BitDepth : BitDepth.Float32;
        var maxValue = scale == 1f ? src.MaxValue : 1f;
        return new Image(dst, bitDepth, maxValue, src.MinValue * scale, src.Pedestal * scale, src.ImageMeta);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Array.Clear(_ring);
            Array.Clear(_timestamps);
        }
    }
}
