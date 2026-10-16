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
        : this(width, height, layout, capacity, hasTimestamps, shelf: null)
    {
    }

    /// <summary>
    /// A live frame stream whose planes come from, and go back to, <paramref name="shelf"/> beyond its own free list (a
    /// planetary capture's ring, <see cref="RingPlaneShelf"/>): the shelf keeps this ring's shape from now on.
    /// </summary>
    internal LiveCameraFrameStream(int width, int height, PlanetaryFrameLayout layout, int capacity, bool hasTimestamps, RingPlaneShelf? shelf)
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
        _shelf = shelf;
        // The steady state's planes (see _freePlanes), for every channel a frame of this layout carries.
        _shelfBound = (capacity + 1) * (layout switch { PlanetaryFrameLayout.SplitCfa => 4, PlanetaryFrameLayout.Rgb => 3, _ => 1 });
        _shelf?.Keep(Shape, _shelfBound);
    }

    private readonly RingPlaneShelf? _shelf;
    private readonly int _shelfBound;
    private int _planesFromShelf;

    private (int Height, int Width, PlanetaryFrameLayout Layout) Shape => (Height, Width, Layout);

    /// <summary>Ring capacity: the maximum number of recent frames retained for loading.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Planes ever allocated, for the tests that hold the ring to recycling them.</summary>
    internal int PlanesAllocated => Volatile.Read(ref _planesAllocated);

    /// <summary>Planes taken from the shelf a disposed ring of this shape left, rather than allocated.</summary>
    internal int PlanesFromShelf => Volatile.Read(ref _planesFromShelf);

    /// <summary>Index of the most recently pushed frame, or <c>-1</c> before the first push.</summary>
    public int LatestIndex
    {
        get { lock (_gate) { return _count - 1; } }
    }

    private int _runStart;

    /// <summary>
    /// Index of the first frame of the stream's current RUN: 0, or the first frame pushed since its stream last started again
    /// after an interlude (<see cref="PlanetaryCapture.InterludeAsync"/>: a filter changed, a step of a sequence, a still). A
    /// live stack never folds a frame from before it with one after (<see cref="LiveStackLoop"/> starts its window again here),
    /// and the ring keeps its planes, so a restart allocates nothing. Read <see cref="LatestIndex"/> first: a run marked between
    /// the two reads then starts past the latest frame, which no stack takes.
    /// </summary>
    public int RunStart
    {
        get { lock (_gate) { return _runStart; } }
    }

    /// <summary>Marks the next frame pushed as the first of a new run (<see cref="RunStart"/>): the capture loop's, before that push.</summary>
    internal void StartRun()
    {
        lock (_gate)
        {
            _runStart = _count;
        }
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
    /// <paramref name="frame"/>. The frame must match the stream's plane dimensions -- or, for a
    /// <see cref="PlanetaryFrameLayout.SplitCfa"/> stream, be the single-channel Bayer mosaic those planes
    /// are half of, which the ring splits straight into its own planes (the same samples and labels
    /// <see cref="Image.SplitBayerChannels"/> then a push would give, without the four planes in between).
    /// </summary>
    public void Push(Image frame, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var isWholeMosaic = Layout == PlanetaryFrameLayout.SplitCfa && frame.IsCfaMosaic;
        var (planeWidth, planeHeight) = isWholeMosaic ? (frame.Width / 2, frame.Height / 2) : (frame.Width, frame.Height);
        if (planeWidth != Width || planeHeight != Height)
        {
            throw new ArgumentException(
                isWholeMosaic
                    ? $"Pushed mosaic is {frame.Width}x{frame.Height}, which splits into {planeWidth}x{planeHeight} planes, but the stream expects {Width}x{Height}."
                    : $"Pushed frame is {frame.Width}x{frame.Height} but the stream expects {Width}x{Height}.",
                nameof(frame));
        }

        // Deep-copy OUTSIDE the lock: the camera recycles its buffer for the next frame, so the ring must
        // own independent arrays. They come from the free list, so steady-state pushing allocates no plane.
        var copy = isWholeMosaic ? SplitIntoRingPlanes(frame) : CopyIntoRingPlanes(frame);

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
        if (Lease(index, cancellationToken) is { } image)
        {
            return ValueTask.FromResult(image);
        }

        int oldest, count;
        lock (_gate)
        {
            (oldest, count) = (Math.Max(0, _count - _ring.Length), _count);
        }
        throw new ArgumentOutOfRangeException(nameof(index),
            $"Frame {index} is not in the live ring (retained [{oldest}, {count - 1}], capacity {_ring.Length}).");
    }

    /// <inheritdoc/>
    public ValueTask<Image?> TryLoadAsync(int index, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Lease(index, cancellationToken));

    // The frame at `index` as a lease, or null once it has rolled out of the ring. Asked and leased in one hold of the
    // gate, so a frame cannot roll out between the two.
    private Image? Lease(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ImageLease lease;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsRetained(index) || _ring[index % _ring.Length] is not { } slot)
            {
                return null;
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
        return lease.Image;
    }

    // A frame index is loadable iff it has been pushed and has not yet rolled out of the ring.
    private bool IsRetained(int index) => index >= 0 && index < _count && index >= _count - _ring.Length;

    private float[,] RentPlane()
    {
        if (_freePlanes.TryDequeue(out var plane))
        {
            return plane;
        }
        if (_shelf is { } shelf && shelf.TryTake(Shape, out var shelved) && shelved is not null)
        {
            Interlocked.Increment(ref _planesFromShelf);
            return shelved;
        }

        Interlocked.Increment(ref _planesAllocated);
        return new float[Height, Width];
    }

    // A plane's last reference went: back to the pool, or, the stream gone and nothing to push again, to the shelf for the
    // next ring of this shape (and the collector, without one).
    private void ReturnPlane(float[,] plane)
    {
        if (!_disposed)
        {
            _freePlanes.Enqueue(plane);
        }
        else
        {
            _shelf?.Put(Shape, plane);
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
        // SplitIntoRingPlanes takes the same scale the same way.
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
        // A scale other than 1 divided by full scale, so the result is unit-referred by construction;
        // at scale 1 nothing moved, so whatever the source said still holds. Forwarded because
        // bitDepth above may keep an INTEGER container width, which on its own no longer implies ADU.
        return WrapRingPlanes(dst, bitDepth, src, scale, samplesAreUnitReferred: scale != 1f || src.SamplesAreUnitReferred);
    }

    /// <summary>
    /// Splits a single-channel Bayer mosaic into four half-size CFA planes <c>[R, G1, G2, B]</c> taken from
    /// the free list, normalising in the same pass: the fusion of <see cref="Image.SplitBayerChannels"/> and
    /// <see cref="CopyIntoRingPlanes"/>, and bit for bit what those two steps produced. The scale is the
    /// same because it reads only the frame's peak and metadata, which the split carried across unchanged;
    /// the labels are the split image's (a <c>Float32</c> container that never claimed unit reference).
    /// It spares the four planes the split allocated per frame, 74 to 246 MB/s at video rate.
    /// </summary>
    private Image SplitIntoRingPlanes(Image mosaic)
    {
        var scale = mosaic.HasUnitScalePeak ? 1f : 1f / mosaic.UnitScaleDivisor;
        var ox = mosaic.ImageMeta.BayerOffsetX & 1;
        var oy = mosaic.ImageMeta.BayerOffsetY & 1;
        var dst = new float[4][,];
        for (var c = 0; c < 4; c++)
        {
            dst[c] = RentPlane();
        }

        // SplitBayerChannels' own traversal: red sits where (x - offsetX) and (y - offsetY) are both even,
        // G1 shares red's row and G2 blue's. Every sample of every recycled plane is written.
        var src = mosaic.GetChannelArray(0);
        var width = mosaic.Width;
        float[,] r = dst[0], g1 = dst[1], g2 = dst[2], b = dst[3];
        for (var sy = 0; sy < Height; sy++)
        {
            var yR = (sy * 2) + oy;
            var yB = (sy * 2) + (1 - oy);
            var srcR = MemoryMarshal.CreateReadOnlySpan(ref src[yR, 0], width);
            var srcB = MemoryMarshal.CreateReadOnlySpan(ref src[yB, 0], width);
            var rRow = MemoryMarshal.CreateSpan(ref r[sy, 0], Width);
            var g1Row = MemoryMarshal.CreateSpan(ref g1[sy, 0], Width);
            var g2Row = MemoryMarshal.CreateSpan(ref g2[sy, 0], Width);
            var bRow = MemoryMarshal.CreateSpan(ref b[sy, 0], Width);
            for (var sx = 0; sx < Width; sx++)
            {
                var xR = (sx * 2) + ox;
                var xB = (sx * 2) + (1 - ox);
                // A multiply by 1 is exact, so an already-unit frame keeps its samples bit for bit.
                rRow[sx] = srcR[xR] * scale;
                g1Row[sx] = srcR[xB] * scale;
                g2Row[sx] = srcB[xR] * scale;
                bRow[sx] = srcB[xB] * scale;
            }
        }

        return WrapRingPlanes(dst, BitDepth.Float32, mosaic, scale, samplesAreUnitReferred: scale != 1f);
    }

    // The ring image over freshly written planes: each travels with a ChannelBuffer whose creator reference
    // the image takes over, and carries the image-wide min and max, as the raw-array constructor set them.
    private Image WrapRingPlanes(float[][,] planes, BitDepth bitDepth, Image source, float scale, bool samplesAreUnitReferred)
    {
        // After scaling, the data is fractional [0,1] floats regardless of the source bit depth. The
        // resulting max is the observed peak scaled down by the SAME factor applied to the pixels --
        // NOT necessarily 1.0 now that the divisor can be the fixed full-scale rather than the observed
        // peak itself (an unsaturated frame normalised by its sensor's full-scale stays below 1.0,
        // correctly reflecting how exposed it actually was).
        var maxValue = source.MaxValue * scale;
        var minValue = source.MinValue * scale;
        var channels = ImmutableArray.CreateBuilder<Channel>(planes.Length);
        for (var c = 0; c < planes.Length; c++)
        {
            channels.Add(new Channel(planes[c], default, minValue, maxValue, (byte)c)
            {
                Buffer = new ChannelBuffer(planes[c], _returnPlane),
            });
        }

        // Keep SensorFullScaleAdu in the same units as the (rescaled) pixels -- after a
        // divide-by-full-scale it reads 1.0. Single implementation: ImageMeta.Rescale.
        return new Image(channels.MoveToImmutable(), bitDepth, source.Pedestal * scale, source.ImageMeta.Rescale(scale),
            samplesAreUnitReferred);
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
            retained = [.. _ring];
            Array.Clear(_ring);
            Array.Clear(_timestamps);
        }

        // The ring's references, released outside the gate like an eviction. A frame a loader still holds
        // stays valid until that loader releases it, and only then does its plane go anywhere (ReturnPlane): nothing
        // returns to this ring's pool after disposal.
        _shelf?.Keep(Shape, _shelfBound);
        foreach (var image in retained)
        {
            image?.Release();
        }

        while (_freePlanes.TryDequeue(out var free))
        {
            _shelf?.Put(Shape, free);
        }
    }
}
