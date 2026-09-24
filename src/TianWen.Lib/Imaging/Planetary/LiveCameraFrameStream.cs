using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
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
/// <b>deep-copied</b> into ring-owned planes: the camera recycles its own buffer for the next frame, so
/// the stream cannot hold the camera's array.
/// </para>
/// <para>
/// <b>The planes are recycled, and a loaded frame is a LEASE.</b> Every push used to copy into new planes
/// and drop the evicted slot's, 74 to 246 MB/s of garbage at video rate. Now each plane is a
/// <see cref="ChannelBuffer"/>: the ring holds one reference, <see cref="LoadAsync"/> hands out a leased
/// image holding another, and a plane returns to the stream's free list only when the last of them is
/// released. So a frame a stacker still holds keeps its pixels even after its slot is overwritten, and its
/// planes are reused only once it lets go. Every consumer releases what it loads in a <c>finally</c>
/// (the stackers and <see cref="FrameGrader"/>), which is exactly the obligation a lease asks for; a
/// loaded frame must not be read after its <see cref="Image.Release"/>, which now throws where it used
/// to be a no-op. A consumer that never releases costs recycling, never correctness: its planes simply
/// stay out of the pool and the ring allocates fresh ones.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Push"/> runs on the capture loop; <see cref="LoadAsync"/> /
/// <see cref="FrameCount"/> / <see cref="TimestampOf"/> run on the stacker's background task. All ring
/// access is guarded by one <see cref="Lock"/>; the work outside the lock (the deep copy on push, the
/// release of an evicted frame) touches only that frame's own planes, and the free list is a lock-free
/// queue because a plane comes back on whichever thread released it last. <see cref="FrameCount"/> grows
/// monotonically with the number of frames ever pushed; only the last <see cref="Capacity"/> are retained.
/// </para>
/// </summary>
public sealed class LiveCameraFrameStream : IPlanetaryFrameStream
{
    private readonly Lock _gate = new();
    private readonly Image?[] _ring;
    private readonly DateTimeOffset?[] _timestamps;
    private int _count;       // total frames ever pushed; == FrameCount, grows monotonically
    private volatile bool _disposed;

    // Planes no frame holds any more, every one Height x Width. Steady state needs Capacity + 1 per channel
    // (the incoming copy exists before the slot it replaces is released) plus whatever loaders still hold.
    private readonly ConcurrentQueue<float[,]> _freePlanes = new();
    private readonly Action<float[,]> _returnPlane;
    private int _planesAllocated;

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
        _returnPlane = ReturnPlane;
    }

    /// <summary>Ring capacity: the maximum number of recent frames retained for loading.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Planes ever allocated, for the tests that hold the ring to recycling them.</summary>
    internal int PlanesAllocated => Volatile.Read(ref _planesAllocated);

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
        // own independent arrays. They come from the free list, so steady-state pushing allocates no plane.
        var copy = CopyIntoRingPlanes(frame);

        Image? evicted = null;
        var disposed = false;
        lock (_gate)
        {
            if (_disposed)
            {
                disposed = true;
            }
            else
            {
                var slot = _count % _ring.Length;
                evicted = _ring[slot];
                _ring[slot] = copy;
                _timestamps[slot] = HasTimestamps ? timestamp : null;
                _count++;
            }
        }

        if (disposed)
        {
            copy.Release();
            throw new ObjectDisposedException(nameof(LiveCameraFrameStream));
        }

        // The ring's own reference to the frame it just overwrote. Its planes go back to the free list now,
        // or later, when the last loader still holding it releases it.
        evicted?.Release();
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

        ImageLease lease;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsRetained(index) || _ring[index % _ring.Length] is not { } slot)
            {
                var oldest = Math.Max(0, _count - _ring.Length);
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Frame {index} is not in the live ring (retained [{oldest}, {_count - 1}], capacity {_ring.Length}).");
            }

            // Under the gate the ring still holds its reference to this slot (eviction swaps the slot here
            // before releasing outside), so the lease cannot lose a race with the frame's last release.
            if (!slot.TryLease(out lease))
            {
                throw new InvalidOperationException($"Frame {index} is in the ring but could not be leased.");
            }
        }

        // A lease, not the ring's own image: the loader's Release() spends only the reference taken here,
        // and the planes stay out of the pool until it does, however many pushes overwrite the slot. No
        // pixel is copied.
        return ValueTask.FromResult(lease.Image);
    }

    // A frame index is loadable iff it has been pushed and has not yet rolled out of the ring.
    private bool IsRetained(int index) => index >= 0 && index < _count && index >= _count - _ring.Length;

    private float[,] RentPlane()
    {
        if (_freePlanes.TryDequeue(out var plane))
        {
            return plane;
        }

        Interlocked.Increment(ref _planesAllocated);
        return new float[Height, Width];
    }

    // A plane's last reference went: back to the pool, unless the stream is gone and nothing will push again.
    private void ReturnPlane(float[,] plane)
    {
        if (!_disposed)
        {
            _freePlanes.Enqueue(plane);
        }
    }

    private Image CopyIntoRingPlanes(Image src)
    {
        var channels = src.ChannelCount;
        var dst = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            dst[c] = RentPlane();
        }

        // The planetary stack pipeline operates in [0,1] (PlanetaryMaster.NormalizeInPlace declares the
        // master MaxValue = 1, and the SER bridge decodes raw frames straight to [0,1]). A live camera,
        // however, delivers ADU, so normalise the owned copy to [0,1] here. Without this the
        // coverage-normalised master keeps ADU values while declaring MaxValue = 1, and the viewer clamps
        // every pixel to white -> a flat, structureless frame.
        //
        // Divide by the canonical Image.UnitScaleDivisor (the sensor's FIXED full-scale ADU when known --
        // e.g. 16383 for a 14-bit sensor -- else the observed peak): src.MaxValue is the peak pixel
        // actually OBSERVED in this specific frame, which varies frame to frame with scene
        // brightness/seeing/hot pixels; using it as the divisor gives every accumulated frame its own
        // scale factor (a dim frame and a saturated frame both stretch their own peak to 1.0),
        // corrupting the photometric consistency the rolling accumulator assumes.
        //
        // The scale-at-all gate stays keyed on the frame's ACTUAL pixel range (MaxValue > 1), not the
        // metadata: an already-[0,1] source (SER, or a frame normalised upstream that still carries a
        // stale ADU-domain SensorFullScaleAdu) must pass through unscaled rather than be divided again.
        var scale = src.HasUnitScalePeak ? 1f : 1f / src.UnitScaleDivisor;
        for (var c = 0; c < channels; c++)
        {
            // Every sample of the recycled plane is written, so nothing of the frame it last held survives.
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

        // After scaling, the data is fractional [0,1] floats regardless of the source bit depth. The
        // resulting max is the observed peak scaled down by the SAME factor applied to the pixels --
        // NOT necessarily 1.0 now that the divisor can be the fixed full-scale rather than the observed
        // peak itself (an unsaturated frame normalised by its sensor's full-scale stays below 1.0,
        // correctly reflecting how exposed it actually was).
        var bitDepth = scale == 1f ? src.BitDepth : BitDepth.Float32;
        var maxValue = src.MaxValue * scale;
        var minValue = src.MinValue * scale;
        // Keep SensorFullScaleAdu in the same units as the (rescaled) pixels -- after a
        // divide-by-full-scale it reads 1.0. Single implementation: ImageMeta.Rescale.
        var meta = src.ImageMeta.Rescale(scale);

        // Each plane travels with its ChannelBuffer, whose creator reference the ring's image takes over;
        // the image-wide min and max on every channel, as the raw-array constructor used to set them.
        var channelsWithBuffers = ImmutableArray.CreateBuilder<Channel>(channels);
        for (var c = 0; c < channels; c++)
        {
            channelsWithBuffers.Add(new Channel(dst[c], default, minValue, maxValue, (byte)c)
            {
                Buffer = new ChannelBuffer(dst[c], _returnPlane),
            });
        }

        // A scale other than 1 divided by full scale, so the result is unit-referred by construction;
        // at scale 1 nothing moved, so whatever the source said still holds. Forwarded because
        // bitDepth above may keep an INTEGER container width, which on its own no longer implies ADU.
        return new Image(channelsWithBuffers.MoveToImmutable(), bitDepth, src.Pedestal * scale, meta,
            samplesAreUnitReferred: scale != 1f || src.SamplesAreUnitReferred);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Image?[] retained;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            retained = (Image?[])_ring.Clone();
            Array.Clear(_ring);
            Array.Clear(_timestamps);
        }

        // The ring's references, released outside the gate like an eviction. A frame a loader still holds
        // stays valid until that loader releases it; nothing returns to the pool after disposal.
        foreach (var image in retained)
        {
            image?.Release();
        }

        _freePlanes.Clear();
    }
}
