using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Ser;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Several captures of one night as one stream, in time order and numbered from zero (docs/plans/planetary-restoration.md, R6
/// part 2): a capture program saves a run as a file a minute, and a de-rotation only pays across files, since one file turns the
/// planet too little to matter (Jupiter 0.7 degrees in the 67 s of 2024-12-15's). Every part has one frame size and layout, and a
/// timed part never overlaps another. It owns its parts.
/// </summary>
public sealed class PlanetaryFrameSequence : IPlanetaryFrameStream
{
    private readonly IPlanetaryFrameStream[] _parts;
    private readonly int[] _starts;

    /// <summary>
    /// <paramref name="parts"/> joined, ordered by their first frame's time when every part is timed and as given otherwise.
    /// </summary>
    public PlanetaryFrameSequence(IEnumerable<IPlanetaryFrameStream> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var given = parts.ToArray();
        if (given.Length == 0)
        {
            throw new ArgumentException("A sequence needs at least one capture.", nameof(parts));
        }
        var first = given[0];
        foreach (var part in given)
        {
            if (part.FrameCount <= 0)
            {
                throw new ArgumentException("A live stream, or an empty one, cannot join a sequence.", nameof(parts));
            }
            if (part.Width != first.Width || part.Height != first.Height || part.Layout != first.Layout)
            {
                throw new ArgumentException(
                    $"Every capture of a sequence has one frame size and layout: {part.Width} x {part.Height} {part.Layout} against {first.Width} x {first.Height} {first.Layout}.",
                    nameof(parts));
            }
        }

        HasTimestamps = given.All(part => part.HasTimestamps && part.TimestampOf(0) is not null && part.TimestampOf(part.FrameCount - 1) is not null);
        _parts = HasTimestamps ? [.. given.OrderBy(part => part.TimestampOf(0))] : given;
        for (var i = 1; HasTimestamps && i < _parts.Length; i++)
        {
            if (_parts[i].TimestampOf(0) <= _parts[i - 1].TimestampOf(_parts[i - 1].FrameCount - 1))
            {
                throw new ArgumentException($"Capture {i} of the sequence starts before the one before it ends: the captures overlap in time.", nameof(parts));
            }
        }

        _starts = new int[_parts.Length];
        var count = 0L;
        for (var i = 0; i < _parts.Length; i++)
        {
            _starts[i] = (int)count;
            count += _parts[i].FrameCount;
        }
        if (count > int.MaxValue)
        {
            throw new ArgumentException($"A sequence holds at most {int.MaxValue} frames; these hold {count}.", nameof(parts));
        }
        FrameCount = (int)count;
    }

    /// <summary>
    /// The SER captures at <paramref name="paths"/> as one sequence, a Bayer mosaic split into its photosite planes as
    /// <see cref="SerFrameStream"/> splits it. Every capture has the one colour arrangement, since each is read by it.
    /// </summary>
    public static PlanetaryFrameSequence OpenSer(IEnumerable<string> paths, bool splitBayer = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var readers = new List<SerReader>();
        try
        {
            foreach (var path in paths)
            {
                readers.Add(SerReader.Open(path));
            }
            if (readers.Count > 0 && readers.FirstOrDefault(reader => reader.ColorId != readers[0].ColorId) is { } other)
            {
                throw new ArgumentException($"Every capture of a sequence has one colour arrangement: {other.ColorId} against {readers[0].ColorId}.", nameof(paths));
            }
            return new PlanetaryFrameSequence(readers.Select(reader => new SerFrameStream(reader, splitBayer, ownsReader: true)));
        }
        catch
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
            throw;
        }
    }

    /// <summary>How many captures the sequence joins.</summary>
    public int PartCount => _parts.Length;

    /// <summary>The capture frame <paramref name="index"/> comes from, in time order.</summary>
    public int PartOf(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        var part = Array.BinarySearch(_starts, index);
        return part >= 0 ? part : ~part - 1;
    }

    /// <inheritdoc/>
    public int FrameCount { get; }

    /// <inheritdoc/>
    public int Width => _parts[0].Width;

    /// <inheritdoc/>
    public int Height => _parts[0].Height;

    /// <inheritdoc/>
    public PlanetaryFrameLayout Layout => _parts[0].Layout;

    /// <inheritdoc/>
    public bool HasTimestamps { get; }

    /// <inheritdoc/>
    public DateTimeOffset? TimestampOf(int index)
    {
        if ((uint)index >= (uint)FrameCount)
        {
            return null;
        }
        var part = PartOf(index);
        return _parts[part].TimestampOf(index - _starts[part]);
    }

    /// <inheritdoc/>
    public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        var part = PartOf(index);
        return _parts[part].LoadAsync(index - _starts[part], cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var part in _parts)
        {
            part.Dispose();
        }
    }
}
