using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The planes a disposed <see cref="LiveCameraFrameStream"/> leaves for the next ring of the same shape (TIA2's
/// capture-priority leg). A planetary capture's ring holds about a thousand frames as float planes (1,024 x 1.2 MB at
/// 640 x 480), each a large object: every capture filled a new ring with fresh ones over its first five seconds, 1.2 GB,
/// and left the last ring's to the collector, which through the node under a busy machine held the capture's reader up in
/// gen 2 pauses. A ring takes from its own free list first and from here second, and gives its free planes back here as it
/// is disposed, as it does a plane a loader still held at the disposal once that loader lets it go.
/// </summary>
/// <remarks>
/// <para><b>One shape at a time.</b> A plane is only ever handed to a ring of its own height, width and layout: a ring of
/// another shape (a new window, a colour layout) gets fresh planes and the shelf drops the old shape's, so it never holds two.
/// A plane's every sample is written as a frame is pushed into it, so the frame it held before leaves nothing behind; the
/// depth a stream reads out in changes no plane's shape, every plane holding floats.</para>
/// <para><b>Bounded, and given up as the collector would have.</b> It keeps at most one ring's capacity of planes, and drops
/// them all at the first gen 2 collection 30 s after it was last used: what the collector took as garbage at its next gen 2
/// before, it now gives up at that time unless a capture has taken it first.</para>
/// <para>Lock-free: a plane comes back on whichever thread released it last, and a ring takes one per frame while it fills.</para>
/// </remarks>
internal sealed class RingPlaneShelf
{
    /// <summary>The process's own, which a planetary capture's ring uses.</summary>
    public static RingPlaneShelf Shared { get; } = new RingPlaneShelf(trimAtGen2: true);

    /// <summary>How long after its last use the shelf gives its planes up at a gen 2 collection.</summary>
    internal static readonly TimeSpan IdleBeforeTrim = TimeSpan.FromSeconds(30);

    private sealed record Shelf((int Height, int Width, PlanetaryFrameLayout Layout) Shape, int Bound)
    {
        public ConcurrentQueue<float[,]> Planes { get; } = new();

        public int Count;
    }

    private Shelf? _shelf;
    private long _lastUsed = Environment.TickCount64;

    internal RingPlaneShelf(bool trimAtGen2 = false)
    {
        if (trimAtGen2)
        {
            _ = new Gen2Watch(this);
        }
    }

    /// <summary>Planes held now, of any shape.</summary>
    internal int Count => Volatile.Read(ref _shelf) is { } shelf ? Volatile.Read(ref shelf.Count) : 0;

    /// <summary>
    /// A ring of <paramref name="shape"/> holding up to <paramref name="bound"/> planes is made: the shelf keeps that shape
    /// from now on, and drops another's.
    /// </summary>
    public void Keep((int Height, int Width, PlanetaryFrameLayout Layout) shape, int bound)
    {
        Touch();
        if (Volatile.Read(ref _shelf) is not { } shelf || shelf.Shape != shape)
        {
            Interlocked.Exchange(ref _shelf, new Shelf(shape, bound));
        }
    }

    /// <summary>A plane of <paramref name="shape"/> to fill, when the shelf holds one.</summary>
    public bool TryTake((int Height, int Width, PlanetaryFrameLayout Layout) shape, out float[,]? plane)
    {
        if (Volatile.Read(ref _shelf) is { } shelf && shelf.Shape == shape && shelf.Planes.TryDequeue(out plane))
        {
            Interlocked.Decrement(ref shelf.Count);
            Touch();
            return true;
        }
        plane = null;
        return false;
    }

    /// <summary>Takes <paramref name="plane"/> back for a later ring of its shape; one of another shape, or past the bound, goes to the collector.</summary>
    public void Put((int Height, int Width, PlanetaryFrameLayout Layout) shape, float[,] plane)
    {
        Touch();
        if (Volatile.Read(ref _shelf) is { } shelf && shelf.Shape == shape && plane.GetLength(0) == shape.Height && plane.GetLength(1) == shape.Width)
        {
            if (Interlocked.Increment(ref shelf.Count) <= shelf.Bound)
            {
                shelf.Planes.Enqueue(plane);
                return;
            }
            Interlocked.Decrement(ref shelf.Count);
        }
    }

    /// <summary>Drops every plane when the shelf has gone unused for <see cref="IdleBeforeTrim"/> by <paramref name="now"/> (ms, as <see cref="Environment.TickCount64"/>).</summary>
    internal void TrimIfIdle(long now)
    {
        if (now - Volatile.Read(ref _lastUsed) >= (long)IdleBeforeTrim.TotalMilliseconds)
        {
            Interlocked.Exchange(ref _shelf, null);
        }
    }

    private void Touch() => Volatile.Write(ref _lastUsed, Environment.TickCount64);

    // Finalized at every gen 2 collection once it lives in gen 2 (the runtime's own Gen2GcCallback does the same): it
    // registers itself again each time, until the process shuts down.
    private sealed class Gen2Watch(RingPlaneShelf shelf)
    {
        ~Gen2Watch()
        {
            shelf.TrimIfIdle(Environment.TickCount64);
            if (!Environment.HasShutdownStarted)
            {
                GC.ReRegisterForFinalize(this);
            }
        }
    }
}
