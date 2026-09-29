using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A run of consecutive frames of another stream, numbered from zero: the first minutes of a capture, say, so a synthetic
/// capture of the same length can be compared with them. It does not own the stream it windows.
/// </summary>
public sealed class PlanetaryFrameWindow : IPlanetaryFrameStream
{
    private readonly IPlanetaryFrameStream _inner;
    private readonly int _first;

    /// <summary>Frames <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> - 1 of <paramref name="inner"/>.</summary>
    public PlanetaryFrameWindow(IPlanetaryFrameStream inner, int first, int count)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(first + count, inner.FrameCount);
        (_inner, _first, FrameCount) = (inner, first, count);
    }

    /// <inheritdoc/>
    public int FrameCount { get; }

    /// <inheritdoc/>
    public int Width => _inner.Width;

    /// <inheritdoc/>
    public int Height => _inner.Height;

    /// <inheritdoc/>
    public PlanetaryFrameLayout Layout => _inner.Layout;

    /// <inheritdoc/>
    public bool HasTimestamps => _inner.HasTimestamps;

    /// <inheritdoc/>
    public DateTimeOffset? TimestampOf(int index) => (uint)index < (uint)FrameCount ? _inner.TimestampOf(_first + index) : null;

    /// <inheritdoc/>
    public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        return _inner.LoadAsync(_first + index, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
