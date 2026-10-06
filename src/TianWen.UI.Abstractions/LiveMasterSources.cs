using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Where a <see cref="LiveStackPreviewSource"/>'s masters come from: a rolling-window stack of a frame stream integrated
/// here (<see cref="StackedMasters"/>, a SER file's playback), a stack integrated elsewhere whose masters arrive whole
/// (<see cref="NodeMasters"/>, the node's planetary run, P6 of docs/plans/hardware-in-the-server.md, #936), or one master
/// opened as a file (<see cref="FixedMaster"/>, #1314). The source's publishing, its wavelet sharpen and its stretch are the
/// same for all three.
/// </summary>
public interface ILiveMasterSource : IDisposable
{
    /// <summary>How far the source has got: the stream's frames, or the masters received. The source follows the last.</summary>
    int FrameCount { get; }

    bool HasTimestamps { get; }

    DateTimeOffset? TimestampOf(int index);

    /// <summary>The master up to <paramref name="playhead"/>, a fresh image the caller owns.</summary>
    Task<Image> MasterAtAsync(int playhead, CancellationToken cancellationToken);

    /// <summary>The geometry a master will have, before the first one exists.</summary>
    (int Width, int Height, int Channels, SensorType Sensor) ExpectedGeometry { get; }
}

/// <summary>
/// The masters of a rolling-window stack of a frame stream, integrated here. Construct off the render thread: it pre-warms
/// the stream's timestamp trailer, so the transport bar's per-frame <see cref="TimestampOf"/> reads hit the warm cache
/// instead of a file-tail seek on the UI thread.
/// </summary>
public sealed class StackedMasters : ILiveMasterSource
{
    private readonly IPlanetaryFrameStream _stream;
    private readonly bool _ownsStream;
    private readonly RollingWindowStacker _stacker;

    /// <param name="ownsStream">Whether the stream is disposed with this: a file-backed SER stream scoped to a view is.</param>
    public StackedMasters(IPlanetaryFrameStream stream, RollingWindowOptions? options, bool ownsStream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _ownsStream = ownsStream;
        _stacker = new RollingWindowStacker(stream, options);
        _ = stream.HasTimestamps;
    }

    public int FrameCount => _stream.FrameCount;

    public bool HasTimestamps => _stream.HasTimestamps;

    public DateTimeOffset? TimestampOf(int index) => _stream.TimestampOf(index);

    public Task<Image> MasterAtAsync(int playhead, CancellationToken cancellationToken) => _stacker.StackToAsync(playhead, cancellationToken);

    public (int Width, int Height, int Channels, SensorType Sensor) ExpectedGeometry
    {
        get
        {
            // A split-CFA source demosaics back to the full mosaic resolution; mono and RGB stay at the stream's plane
            // size. Channels: mono 1, everything else 3 (RGB).
            var split = _stream.Layout == PlanetaryFrameLayout.SplitCfa;
            var channels = _stream.Layout == PlanetaryFrameLayout.Mono ? 1 : 3;
            return (split ? _stream.Width * 2 : _stream.Width, split ? _stream.Height * 2 : _stream.Height, channels,
                channels == 1 ? SensorType.Monochrome : SensorType.Color);
        }
    }

    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}

/// <summary>
/// The masters a node's planetary run stacks and streams (<c>planetary/master</c>), as they arrive. Each one pushed is the
/// next "frame" the source follows to; the one taken is the caller's, a copy of the one held, which a later push releases.
/// </summary>
public sealed class NodeMasters : ILiveMasterSource
{
    private Image? _latest;
    private int _count;

    /// <summary>Takes <paramref name="master"/> as the latest, and CONSUMES it: the one it replaces is released.</summary>
    public void Push(Image master)
    {
        Interlocked.Exchange(ref _latest, master)?.Release();
        Interlocked.Increment(ref _count);
    }

    public int FrameCount => Volatile.Read(ref _count);

    public bool HasTimestamps => false;

    public DateTimeOffset? TimestampOf(int index) => null;

    public Task<Image> MasterAtAsync(int playhead, CancellationToken cancellationToken)
    {
        // A copy, leased for the read: the reader thread releases the held one as the next master arrives.
        if (Volatile.Read(ref _latest) is { } held && held.TryLease(out var lease))
        {
            using (lease)
            {
                return Task.FromResult(lease.Image.Clone());
            }
        }
        return Task.FromException<Image>(new InvalidOperationException("No master from the node yet"));
    }

    public (int Width, int Height, int Channels, SensorType Sensor) ExpectedGeometry =>
        Volatile.Read(ref _latest) is { } held
            ? (held.Width, held.Height, held.ChannelCount, held.ChannelCount == 1 ? SensorType.Monochrome : SensorType.Color)
            : (1, 1, 1, SensorType.Monochrome);

    public void Dispose() => Interlocked.Exchange(ref _latest, null)?.Release();
}

/// <summary>
/// A planetary master opened as a file, as the one master a <see cref="LiveStackPreviewSource"/> shows (#1314): the sharpening
/// layer works over it exactly as over the stacked view's masters, so the dials, Derive, its stops and the colour look have one
/// path. Its one "frame" is stamped with the master's own instant (<see cref="PlanetaryBestStack.InstantOf"/>, the middle of its
/// capture's span), which a derivation takes as its epoch.
/// </summary>
public sealed class FixedMaster : ILiveMasterSource
{
    private Image? _master;
    private readonly DateTimeOffset? _instant;

    /// <summary>Takes <paramref name="master"/>, and CONSUMES it: it is released with this.</summary>
    public FixedMaster(Image master)
    {
        ArgumentNullException.ThrowIfNull(master);
        _master = master;
        _instant = PlanetaryBestStack.InstantOf(master, epoch: null);
    }

    public int FrameCount => 1;

    public bool HasTimestamps => _instant is not null;

    public DateTimeOffset? TimestampOf(int index)
    {
        return _instant;
    }

    public Task<Image> MasterAtAsync(int playhead, CancellationToken cancellationToken)
    {
        // A copy each time, as every master a source is handed is the caller's: the source keeps it to sharpen again, and releases it.
        return Volatile.Read(ref _master) is { } master
            ? Task.FromResult(master.Clone())
            : Task.FromException<Image>(new ObjectDisposedException(nameof(FixedMaster)));
    }

    public (int Width, int Height, int Channels, SensorType Sensor) ExpectedGeometry =>
        Volatile.Read(ref _master) is { } master
            ? (master.Width, master.Height, master.ChannelCount, master.ChannelCount == 1 ? SensorType.Monochrome : SensorType.Color)
            : (1, 1, 1, SensorType.Monochrome);

    public void Dispose()
    {
        Interlocked.Exchange(ref _master, null)?.Release();
    }
}
