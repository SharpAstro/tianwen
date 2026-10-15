using System;
using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpAstro.Ser;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A planetary capture's recording to disk (P5 part 5d of docs/plans/hardware-in-the-server.md, #934): every frame the
/// capture takes until <see cref="EndsAt"/>, in the camera's own shape (a mono plane or a Bayer mosaic, in the depth it
/// streams: 8 bits a sample for an 8-bit stream, else 16), into a SER file, each with the time it arrived in the trailer. #814 designs the memory-mapped SER this may become, the
/// recording and the stack's frame ring in one.
/// </summary>
/// <remarks>
/// <para><b>The capture loop only converts and queues</b> (<see cref="TryAppend"/>): a writer task of the recording's own
/// does the disk, so a disk that falls behind costs RECORDED frames, counted in <see cref="FramesDropped"/>, never the
/// capture's rate or its live view.</para>
/// <para><b>A SER frame has one size and one depth</b>, so a frame of another (a window resized, or the stream's depth
/// switched, mid-recording) ends it, as its duration and a stop do. The file is whole once <see cref="Completion"/> has: its header, frame count included, is written as it
/// closes.</para>
/// <para><b>A stall is a gap in the trailer, never a splice.</b> Each frame carries the time its camera's driver read it,
/// so when a stream stalls and its driver brings it back (a restart, a reopen or a reset, <c>DALCameraDriver.Video.cs</c>)
/// the two frames either side are consecutive in the file and their timestamps the gap apart (about 6.5 s for a ToupTek
/// restart). A reader of the SER sees every frame that was taken, none invented, and a jump in time between two; a rate
/// read as frame count over the timestamp span counts the gap as time without frames, and a de-rotation reads the true
/// times. A recording of a stream notes each gap longer than the exposure plus 1 s (<see cref="Gaps"/>) and logs them as it closes; one
/// of a camera that takes each frame as an exposure of its own notes none, a slow download between two being no stall.</para>
/// </remarks>
public sealed class SerRecording
{
    /// <summary>Frames that may wait for the disk before one is dropped: about half a second at video rate.</summary>
    private const int QueueFrames = 64;

    /// <summary>How much longer than its exposure a step between two frames is before it is a gap: a stalled ToupTek says so 1 s past the exposure.</summary>
    private static readonly TimeSpan GapBeyondExposure = TimeSpan.FromSeconds(1);

    private readonly Channel<(byte[] Bytes, DateTimeOffset Arrived)> _queue =
        System.Threading.Channels.Channel.CreateBounded<(byte[], DateTimeOffset)>(new BoundedChannelOptions(QueueFrames) { SingleReader = true, SingleWriter = true });
    private readonly ILogger _logger;

    // The frames' shape, taken from the first one on the capture loop and read by the writer only after a frame it queued.
    private int _width;
    private int _height;
    private int _channels;
    private SerColorId _color;
    private int _depth;
    private bool _shaped;

    private readonly string _instrument;
    private readonly string _telescope;
    private readonly bool _streamed;

    private int _written;
    private int _dropped;
    private int _queued;
    private DateTimeOffset _lastArrived;
    private ImmutableArray<SerRecordingGap> _gaps = [];
    private int _ended;
    private volatile string? _endReason;
    private volatile string? _failure;

    /// <summary>
    /// A recording to <paramref name="path"/> of every frame that arrives before <paramref name="endsAt"/>, its header naming the camera
    /// (<paramref name="instrument"/>) and the telescope (<paramref name="telescope"/>, <see cref="PlanetaryCaptureName.TelescopeField"/>)
    /// it was taken with, so its best stack knows the optics (#1179). <paramref name="streamed"/> says the frames come from a camera's
    /// stream, the only frames whose spacing can show a stall (<see cref="Gaps"/>).
    /// </summary>
    public SerRecording(string path, DateTimeOffset startedAt, DateTimeOffset endsAt, ILogger logger, string? instrument = null, string? telescope = null,
        bool streamed = false)
    {
        Path = path;
        _instrument = string.IsNullOrEmpty(instrument) ? "TianWen" : instrument;
        _telescope = telescope ?? "";
        _streamed = streamed;
        StartedAt = startedAt;
        EndsAt = endsAt;
        _logger = logger;
        Completion = Task.Run(WriteAsync);
    }

    /// <summary>The SER file it writes.</summary>
    public string Path { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>When it ends by itself: a frame arriving from then on is not recorded.</summary>
    public DateTimeOffset EndsAt { get; }

    /// <summary>True until it has ended, however: its duration, a stop, a frame of another size, the capture ending.</summary>
    public bool IsRecording => Volatile.Read(ref _ended) == 0;

    /// <summary>Frames on disk.</summary>
    public int FramesWritten => Volatile.Read(ref _written);

    /// <summary>Frames the disk could not keep up with, never recorded.</summary>
    public int FramesDropped => Volatile.Read(ref _dropped);

    /// <summary>The gaps between consecutive frames longer than the exposure plus 1 s: a stream that stalled and came back.</summary>
    public ImmutableArray<SerRecordingGap> Gaps => _gaps;

    /// <summary>Why it ended, in words; null while it records.</summary>
    public string? EndReason => _endReason;

    /// <summary>Why the file could not be written, in words; null while it writes well.</summary>
    public string? FailureReason => _failure;

    /// <summary>Completes once every queued frame is on disk and the file is closed, its header written.</summary>
    public Task Completion { get; }

    /// <summary>
    /// Queues <paramref name="frame"/>, BORROWED (it is read here, never kept), to be written with the time it
    /// <paramref name="arrived"/>. False once the recording has ended, which this call does when the frame arrived at or
    /// after <see cref="EndsAt"/> or is of another size than the first. Called on the capture loop, one frame at a time.
    /// </summary>
    public bool TryAppend(Image frame, DateTimeOffset arrived)
    {
        if (!IsRecording)
        {
            return false;
        }
        if (arrived >= EndsAt)
        {
            End("its duration is over");
            return false;
        }

        var color = SerImageBridge.SerColorOf(frame.ImageMeta, frame.ChannelCount);
        var channels = color.PlaneCount;
        var depth = SerImageBridge.SerDepthOf(frame);
        if (!_shaped)
        {
            (_width, _height, _channels, _color, _depth, _shaped) = (frame.Width, frame.Height, channels, color, depth, true);
        }
        else if ((frame.Width, frame.Height, channels, color) != (_width, _height, _channels, _color))
        {
            End($"the frames changed from {_width}x{_height} to {frame.Width}x{frame.Height}");
            return false;
        }
        else if (depth != _depth)
        {
            End($"the frames changed from {_depth} to {depth} bits");
            return false;
        }

        var samples = _width * _height * _channels;
        var bytes = ArrayPool<byte>.Shared.Rent(samples * (_depth / 8));
        SerImageBridge.FillSerFrame(frame, _depth, bytes.AsSpan(0, samples * (_depth / 8)));
        if (!_queue.Writer.TryWrite((bytes, arrived)))
        {
            ArrayPool<byte>.Shared.Return(bytes);
            Interlocked.Increment(ref _dropped);
            return true;
        }

        // Frames are queued in file order, so the count queued is the index the frame lands at. Only a stream's spacing can
        // show a stall.
        if (_queued++ > 0 && _streamed && arrived - _lastArrived is var step && step > frame.ImageMeta.ExposureDuration + GapBeyondExposure)
        {
            _gaps = _gaps.Add(new SerRecordingGap(_queued - 2, step));
        }
        _lastArrived = arrived;
        return true;
    }

    /// <summary>Ends the recording: the frames queued so far are still written, then the file closes.</summary>
    public void End(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            _endReason = reason;
            _queue.Writer.TryComplete();
        }
    }

    private async Task WriteAsync()
    {
        SerWriter? writer = null;
        try
        {
            await foreach (var (bytes, arrived) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (writer is null)
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? ".");
                        writer = new SerWriter(Path, _width, _height, _color, pixelDepthPerPlane: _depth, instrument: _instrument, telescope: _telescope);
                    }
                    writer.AppendFrame(bytes.AsSpan(0, (int)writer.FrameSizeBytes), arrived);
                    Interlocked.Increment(ref _written);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The planetary recording to {Path} failed", Path);
            _failure = StatusText.FromException(ex);
            End("it could not be written");
            // What was queued behind the failure goes back to the pool unwritten.
            while (_queue.Reader.TryRead(out var left))
            {
                ArrayPool<byte>.Shared.Return(left.Bytes);
            }
        }
        finally
        {
            // The header, frame count and trailer included, is written as the writer closes.
            writer?.Dispose();
            if (_gaps is { Length: > 0 } gaps)
            {
                _logger.LogWarning("The planetary recording to {Path} has {Count} gaps, where its stream stalled and came back: {Gaps}", Path, gaps.Length,
                    string.Join("; ", gaps.Select(gap => $"{gap.Length.TotalSeconds:F2} s after frame {gap.AfterFrame}")));
            }
        }
    }
}

/// <summary>A gap in a <see cref="SerRecording"/>: <paramref name="Length"/> between frame <paramref name="AfterFrame"/> (0-based in the file) and the next.</summary>
public readonly record struct SerRecordingGap(int AfterFrame, TimeSpan Length);
