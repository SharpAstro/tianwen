using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A capture's CURRENT frame rate (#1619): its frames over the last second, or over its last five frames where those span longer,
/// which a slow exposure's do. The run's average (<see cref="PlanetaryCapture.MeasuredFps"/>) blends every exposure the run
/// has had: a capture started at 10 ms and set to 5 ms read a blend of the two for the rest of the night.
/// </summary>
/// <remarks>
/// Written by one thread, the capture loop, a frame at a time (<see cref="Note"/>), in O(1) amortised; its answer is
/// published as one value (<see cref="PlanetaryCapture.CurrentFps"/>), never read from the ring by another thread.
/// </remarks>
internal sealed class FrameRateWindow
{
    /// <summary>The frames a window counts at least, where they span more than a second.</summary>
    internal const int MinFrames = 5;

    /// <summary>Frames held, past any frame rate the window reads: a window of a second at more than this many is cut to it.</summary>
    private const int Capacity = 4096;

    private static readonly long WindowTicks = TimeSpan.TicksPerSecond;

    private readonly long[] _ticks = new long[Capacity];
    private long _head;   // frames noted
    private long _tail;   // the oldest frame still in the window

    /// <summary>Notes a frame that arrived at <paramref name="ticks"/> (a monotonic clock's, in TimeSpan ticks) and answers the rate now.</summary>
    public double Note(long ticks)
    {
        _ticks[_head % Capacity] = ticks;
        _head++;
        // The window starts at the oldest frame inside the last second, but never holds fewer than MinFrames.
        while (_head - _tail > MinFrames && ticks - _ticks[_tail % Capacity] > WindowTicks)
        {
            _tail++;
        }
        if (_head - _tail > Capacity)
        {
            _tail = _head - Capacity;
        }

        var frames = _head - _tail;
        var span = ticks - _ticks[_tail % Capacity];
        return frames >= 2 && span > 0 ? (frames - 1) * (double)TimeSpan.TicksPerSecond / span : 0.0;
    }

    /// <summary>Forgets every frame, for a new capture.</summary>
    public void Reset() => (_head, _tail) = (0, 0);
}
