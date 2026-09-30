using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// One photosite colour of a Bayer capture as a mono stream: channel <see cref="Channel"/> of a split-CFA source
/// (<see cref="PlanetaryFrameLayout.SplitCfa"/>, <c>[R, G1, G2, B]</c> at half the sensor's resolution), every frame's plane
/// on its own. It is how a statistic built for a mono capture reads a colour one, one colour at a time
/// (docs/plans/planetary-restoration.md, R5a): its pixel <c>(i, j)</c> is the sensor's photosite
/// <c>(2i + Phase.X, 2j + Phase.Y)</c> (<see cref="PhaseOf"/>).
/// </summary>
public sealed class CfaPlaneStream : IPlanetaryFrameStream
{
    /// <summary>The red photosites' channel of a split-CFA frame.</summary>
    public const int Red = 0;

    /// <summary>The green photosites on the red rows.</summary>
    public const int Green1 = 1;

    /// <summary>The green photosites on the blue rows.</summary>
    public const int Green2 = 2;

    /// <summary>The blue photosites' channel.</summary>
    public const int Blue = 3;

    private readonly IPlanetaryFrameStream _source;
    private readonly bool _ownsSource;

    /// <summary>
    /// Channel <paramref name="channel"/> of <paramref name="source"/>, which must be split-CFA. The source is disposed with this
    /// stream only when <paramref name="ownsSource"/>.
    /// </summary>
    public CfaPlaneStream(IPlanetaryFrameStream source, int channel, bool ownsSource = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Layout != PlanetaryFrameLayout.SplitCfa)
        {
            throw new ArgumentException($"A colour plane is read from a split-CFA stream; this one is {source.Layout}.", nameof(source));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, Blue);
        (_source, Channel, _ownsSource) = (source, channel, ownsSource);
    }

    /// <summary>Which of <c>[R, G1, G2, B]</c> this stream is.</summary>
    public int Channel { get; }

    /// <inheritdoc/>
    public int FrameCount => _source.FrameCount;

    /// <inheritdoc/>
    public int Width => _source.Width;

    /// <inheritdoc/>
    public int Height => _source.Height;

    /// <inheritdoc/>
    public PlanetaryFrameLayout Layout => PlanetaryFrameLayout.Mono;

    /// <inheritdoc/>
    public bool HasTimestamps => _source.HasTimestamps;

    /// <inheritdoc/>
    public DateTimeOffset? TimestampOf(int index) => _source.TimestampOf(index);

    /// <inheritdoc/>
    public async ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
    {
        var frame = await _source.LoadAsync(index, cancellationToken).ConfigureAwait(false);
        try
        {
            var src = frame.GetChannelSpan(Channel);
            var plane = new float[frame.Height, frame.Width];
            src.CopyTo(MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length));
            return new Image([plane], BitDepth.Float32, frame.MaxValue, frame.MinValue, frame.Pedestal,
                frame.ImageMeta with { SensorType = SensorType.Monochrome, BayerOffsetX = 0, BayerOffsetY = 0 });
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>
    /// Where channel <paramref name="channel"/>'s photosites sit in each 2 x 2 cell of the sensor, for a pattern whose red is at
    /// (<paramref name="bayerOffsetX"/>, <paramref name="bayerOffsetY"/>): <see cref="Image.SplitBayerChannels"/>'s convention,
    /// red at the offset, G1 beside it on its row, G2 below it, blue diagonal.
    /// </summary>
    public static (int X, int Y) PhaseOf(int channel, int bayerOffsetX, int bayerOffsetY)
    {
        var (ox, oy) = (bayerOffsetX & 1, bayerOffsetY & 1);
        return channel switch
        {
            Red => (ox, oy),
            Green1 => (1 - ox, oy),
            Green2 => (ox, 1 - oy),
            Blue => (1 - ox, 1 - oy),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "a CFA channel is 0 to 3"),
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsSource)
        {
            _source.Dispose();
        }
    }
}
