using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Lib.Tests;

/// <summary>
/// A mono capture held in memory, for the planetary stackers and statistics: each load is a copy, so a consumer's
/// <see cref="Image.Release"/> never touches the frames the test made.
/// </summary>
internal sealed class InMemoryFrameStream(float[][,] frames, DateTimeOffset[]? timestamps = null) : IPlanetaryFrameStream
{
    private int _loadCount;

    /// <summary>How many frames have been loaded.</summary>
    public int LoadCount => _loadCount;

    public int FrameCount => frames.Length;

    public int Width => frames[0].GetLength(1);

    public int Height => frames[0].GetLength(0);

    public PlanetaryFrameLayout Layout => PlanetaryFrameLayout.Mono;

    public bool HasTimestamps => timestamps is not null;

    public DateTimeOffset? TimestampOf(int index)
        => timestamps is { } ts && (uint)index < (uint)ts.Length ? ts[index] : null;

    public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _loadCount);
        return ValueTask.FromResult(Image.FromChannel(frames[index].Copy(), 1f, 0f));
    }

    public void Dispose()
    {
    }
}
