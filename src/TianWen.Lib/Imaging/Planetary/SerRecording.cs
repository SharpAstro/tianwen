using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
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
/// </remarks>
public sealed class SerRecording
{
    /// <summary>Frames that may wait for the disk before one is dropped: about half a second at video rate.</summary>
    private const int QueueFrames = 64;

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

    private int _written;
    private int _dropped;
    private int _ended;
    private volatile string? _endReason;
    private volatile string? _failure;

    /// <summary>A recording to <paramref name="path"/> of every frame that arrives before <paramref name="endsAt"/>.</summary>
    public SerRecording(string path, DateTimeOffset startedAt, DateTimeOffset endsAt, ILogger logger)
    {
        Path = path;
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
        var depth = DepthOf(frame);
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
        if (_depth == 8)
        {
            Fill(frame, bytes.AsSpan(0, samples));
        }
        else
        {
            Fill(frame, MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan(0, samples * sizeof(ushort))));
        }
        if (!_queue.Writer.TryWrite((bytes, arrived)))
        {
            ArrayPool<byte>.Shared.Return(bytes);
            Interlocked.Increment(ref _dropped);
        }
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

    // 8 for a frame the camera read out in 8 bits (its samples 0 to 255), else 16: a colour frame already in [0, 1] and a
    // 16-bit readout both keep the 16 bits a recording always had.
    private static int DepthOf(Image frame) => frame.BitDepth is BitDepth.Int8 && !frame.SamplesAreUnitReferred ? 8 : 16;

    // The samples as the camera gave them, whole numbers 0 to 255, interleaved per pixel for RGB (SER's layout).
    private static void Fill(Image frame, Span<byte> samples)
    {
        var scale = frame.SamplesAreUnitReferred ? byte.MaxValue : 1f;
        var channels = frame.ChannelCount >= 3 ? 3 : 1;
        for (var c = 0; c < channels; c++)
        {
            var plane = frame.GetChannelSpan(c);
            for (var i = 0; i < plane.Length; i++)
            {
                samples[i * channels + c] = (byte)Math.Clamp(MathF.Round(plane[i] * scale), 0f, byte.MaxValue);
            }
        }
    }

    // The samples as the camera gave them, whole numbers 0 to 65535, interleaved per pixel for RGB (SER's layout).
    private static void Fill(Image frame, Span<ushort> samples)
    {
        var scale = frame.SamplesAreUnitReferred ? ushort.MaxValue : 1f;
        var channels = frame.ChannelCount >= 3 ? 3 : 1;
        for (var c = 0; c < channels; c++)
        {
            var plane = frame.GetChannelSpan(c);
            for (var i = 0; i < plane.Length; i++)
            {
                samples[i * channels + c] = (ushort)Math.Clamp(MathF.Round(plane[i] * scale), 0f, ushort.MaxValue);
            }
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
                        writer = new SerWriter(Path, _width, _height, _color, pixelDepthPerPlane: _depth, instrument: "TianWen");
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
        }
    }
}
