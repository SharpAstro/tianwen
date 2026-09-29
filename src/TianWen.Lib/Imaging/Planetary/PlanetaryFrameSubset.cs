using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Some frames of another stream, in the order given and numbered from zero: every other frame of a capture, say, so its
/// two halves are stacked by the same method from disjoint frames (docs/plans/planetary-restoration.md, T2 and R3). It does
/// not own the stream it draws from.
/// </summary>
public sealed class PlanetaryFrameSubset : IPlanetaryFrameStream
{
    private readonly IPlanetaryFrameStream _inner;
    private readonly ImmutableArray<int> _indices;

    /// <summary>Frames <paramref name="indices"/> of <paramref name="inner"/>, each within its range.</summary>
    public PlanetaryFrameSubset(IPlanetaryFrameStream inner, ImmutableArray<int> indices)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (indices.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A subset needs at least one frame.", nameof(indices));
        }
        foreach (var index in indices)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index, nameof(indices));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, inner.FrameCount, nameof(indices));
        }
        (_inner, _indices) = (inner, indices);
    }

    /// <summary>Every other frame of <paramref name="inner"/>, from frame <paramref name="parity"/> (0 or 1).</summary>
    public static PlanetaryFrameSubset Half(IPlanetaryFrameStream inner, int parity)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(parity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parity, 1);
        var builder = ImmutableArray.CreateBuilder<int>((inner.FrameCount + 1 - parity) / 2);
        for (var i = parity; i < inner.FrameCount; i += 2)
        {
            builder.Add(i);
        }
        return new PlanetaryFrameSubset(inner, builder.ToImmutable());
    }

    /// <inheritdoc/>
    public int FrameCount => _indices.Length;

    /// <inheritdoc/>
    public int Width => _inner.Width;

    /// <inheritdoc/>
    public int Height => _inner.Height;

    /// <inheritdoc/>
    public PlanetaryFrameLayout Layout => _inner.Layout;

    /// <inheritdoc/>
    public bool HasTimestamps => _inner.HasTimestamps;

    /// <inheritdoc/>
    public DateTimeOffset? TimestampOf(int index) => (uint)index < (uint)FrameCount ? _inner.TimestampOf(_indices[index]) : null;

    /// <inheritdoc/>
    public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        return _inner.LoadAsync(_indices[index], cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
