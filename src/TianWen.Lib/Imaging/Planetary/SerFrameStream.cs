using System;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Ser;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// An <see cref="IPlanetaryFrameStream"/> over a SER planetary-video file. Wraps a memory-mapped
/// <see cref="SerReader"/> (O(1) frame seek, lazy timestamp trailer) plus <see cref="SerImageBridge"/>
/// for the raw -> [0,1] decode. A Bayer source is split into four CFA sub-planes on load
/// (<see cref="PlanetaryFrameLayout.SplitCfa"/>, the default) so the integrator can stack each photosite
/// colour independently before a single final demosaic; pass <c>splitBayer: false</c> to keep the raw
/// mosaic (<see cref="PlanetaryFrameLayout.BayerMosaic"/>). Mono and RGB sources pass through unchanged.
/// </summary>
public sealed class SerFrameStream : IPlanetaryFrameStream
{
    private readonly SerReader _reader;
    private readonly bool _ownsReader;

    /// <summary>
    /// Wraps an already-opened <paramref name="reader"/>. When <paramref name="ownsReader"/> is true
    /// (the default) the reader is disposed with this stream.
    /// </summary>
    public SerFrameStream(SerReader reader, bool splitBayer = true, bool ownsReader = true)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _ownsReader = ownsReader;

        var (sensor, _, _) = reader.ColorId.ToSensorType();
        Layout = sensor switch
        {
            SensorType.Color => PlanetaryFrameLayout.Rgb,
            SensorType.RGGB => splitBayer ? PlanetaryFrameLayout.SplitCfa : PlanetaryFrameLayout.BayerMosaic,
            _ => PlanetaryFrameLayout.Mono,
        };

        var split = Layout == PlanetaryFrameLayout.SplitCfa;
        Width = split ? reader.Width / 2 : reader.Width;
        Height = split ? reader.Height / 2 : reader.Height;
    }

    /// <summary>Opens <paramref name="path"/> as a planetary frame stream (owns the reader).</summary>
    public static SerFrameStream Open(string path, bool splitBayer = true)
        => new SerFrameStream(SerReader.Open(path), splitBayer, ownsReader: true);

    /// <inheritdoc/>
    public int FrameCount => _reader.FrameCount;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PlanetaryFrameLayout Layout { get; }

    /// <inheritdoc/>
    public bool HasTimestamps => _reader.HasTimestamps;

    /// <inheritdoc/>
    public DateTimeOffset? TimestampOf(int index)
    {
        var ts = _reader.Timestamps;
        return !ts.IsDefaultOrEmpty && (uint)index < (uint)ts.Length ? ts[index] : null;
    }

    /// <inheritdoc/>
    public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Memory-mapped, CPU-bound decode -- completes synchronously. The caller runs us off the render
        // thread (the IPlanetaryFrameStream contract), so there is no gain in hopping to the thread pool
        // per frame; the batch pipeline + live windowed stacker control their own parallelism.
        var image = SerImageBridge.ToImage(_reader, index);
        if (Layout == PlanetaryFrameLayout.SplitCfa)
        {
            image = image.SplitBayerChannels();
        }

        return ValueTask.FromResult(image);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsReader)
        {
            _reader.Dispose();
        }
    }
}
