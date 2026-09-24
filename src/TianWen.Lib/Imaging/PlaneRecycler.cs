using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TianWen.Lib.Imaging;

/// <summary>
/// The free list a streaming camera driver recycles its frame planes through: a plane is TAKEN for a new
/// frame (a returned one of the same shape, else a new one) and handed back by the frame's own
/// <see cref="Image.Release"/>, through the <see cref="ChannelBuffer"/> it is wrapped in. What
/// <c>DALCameraDriver</c>, <c>AscomCameraDriver</c> and <c>AlpacaCameraDriver</c> each hand-roll for one
/// plane, for a frame of any number of them.
/// </summary>
/// <remarks>
/// A frame built from these planes is a RECYCLED camera frame (ownership convention 1): its consumer must
/// release it, and must not read it after, since the next frame is decoded into the same planes. A plane
/// of another shape (an ROI or zoom change) is dropped to the GC when found, never kept.
/// </remarks>
internal sealed class PlaneRecycler
{
    private readonly ConcurrentBag<float[,]> _free = [];
    private readonly Action<float[,]> _return;
    private readonly string _owner;

    /// <param name="owner">Who produces the frames, for the DEBUG leak tracker: every plane is attributed
    /// to it, rather than to this helper.</param>
    public PlaneRecycler(string owner)
    {
        _owner = owner;
        // Made once here, not per wrapped plane.
        _return = _free.Add;
    }

    private int _planesAllocated;

    /// <summary>How many planes this recycler has had to allocate, for tests pinning that a steady stream
    /// allocates none.</summary>
    internal int PlanesAllocated => Volatile.Read(ref _planesAllocated);

    /// <summary>A plane of <paramref name="height"/> x <paramref name="width"/>: a returned one when there is
    /// one, else new. Its contents are the previous frame's, so the caller writes every pixel.</summary>
    public float[,] Take(int height, int width)
    {
        while (_free.TryTake(out var plane))
        {
            if (plane.GetLength(0) == height && plane.GetLength(1) == width)
            {
                return plane;
            }
        }

        Interlocked.Increment(ref _planesAllocated);
        return new float[height, width];
    }

    /// <summary>A channel over <paramref name="plane"/> whose buffer returns it here on release.</summary>
    public Channel Wrap(float[,] plane, float minValue, float maxValue, byte index)
        => new Channel(plane, default, minValue, maxValue, index)
        {
            Buffer = new ChannelBuffer(plane, _return, _owner, 0),
        };
}
