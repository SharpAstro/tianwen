using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Disk-backed staging for warped frames during stacking. Each frame is written
/// as a tiny header + raw float32 LE pixel data so row-stripe reads are a single
/// <see cref="FileStream.Seek"/> + <see cref="FileStream.Read"/>:
/// <list type="bullet">
///   <item>bytes 0..3 : channels (int32 LE)</item>
///   <item>bytes 4..7 : height (int32 LE)</item>
///   <item>bytes 8..11: width (int32 LE)</item>
///   <item>bytes 12..15: reserved</item>
///   <item>bytes 16..: channels * height * width float32 LE, in (channel, row, col) order</item>
/// </list>
/// No FITS overhead. Lets the streaming integrator process arbitrary-N stacks
/// with O(stripe_height * width * channels * N) RAM rather than O(N * frame_size).
/// </summary>
public static class StreamingFrameStaging
{
    /// <summary>Header size in bytes. Fixed, version-independent.</summary>
    public const int HeaderBytes = 16;

    /// <summary>Writes <paramref name="image"/>'s pixels to <paramref name="path"/> in the
    /// streaming-staging binary format. Each channel is written contiguously in row-major
    /// order so a stripe of rows is a single seek + read.</summary>
    public static void Write(Image image, string path)
    {
        var (channels, width, height) = image.Shape;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        Span<byte> header = stackalloc byte[HeaderBytes];
        BitConverter.TryWriteBytes(header[0..4], channels);
        BitConverter.TryWriteBytes(header[4..8], height);
        BitConverter.TryWriteBytes(header[8..12], width);
        // bytes 12..15 reserved -- zero from stackalloc
        fs.Write(header);

        // GetChannelSpan returns a flat ReadOnlySpan<float> over the row-major
        // channel data. Cast to bytes for a single bulk write per channel.
        for (var ch = 0; ch < channels; ch++)
        {
            var bytes = MemoryMarshal.AsBytes(image.GetChannelSpan(ch));
            fs.Write(bytes);
        }
    }
}

/// <summary>
/// Read-only random-access view into a <see cref="StreamingFrameStaging"/> file.
/// Holds an open <see cref="FileStream"/>; dispose to release the handle.
/// Thread-safe across concurrent <see cref="ReadStripe"/> calls only if callers
/// serialise their own access -- the underlying stream is single-cursor.
/// </summary>
public sealed class StreamingFrameReader : IDisposable
{
    private readonly FileStream _fs;
    private readonly object _gate = new();

    public string Path { get; }
    public int Channels { get; }
    public int Height { get; }
    public int Width { get; }

    /// <summary>Bytes occupied by a single channel plane (h * w * sizeof(float)).</summary>
    public long ChannelBytes => (long)Height * Width * sizeof(float);

    public StreamingFrameReader(string path)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        Span<byte> header = stackalloc byte[StreamingFrameStaging.HeaderBytes];
        _fs.ReadExactly(header);
        Channels = BitConverter.ToInt32(header[0..4]);
        Height = BitConverter.ToInt32(header[4..8]);
        Width = BitConverter.ToInt32(header[8..12]);
        if (Channels <= 0 || Height <= 0 || Width <= 0)
        {
            throw new InvalidDataException(
                $"Bad streaming-staging header: c={Channels}, h={Height}, w={Width} in {path}.");
        }
    }

    /// <summary>Reads <paramref name="rowCount"/> consecutive rows starting at
    /// <paramref name="rowStart"/> from <paramref name="channel"/> into <paramref name="destination"/>.
    /// The destination span must hold at least <c>rowCount * Width</c> floats.</summary>
    public void ReadStripe(int channel, int rowStart, int rowCount, Span<float> destination)
    {
        if ((uint)channel >= (uint)Channels)
            throw new ArgumentOutOfRangeException(nameof(channel));
        if ((uint)rowStart >= (uint)Height || rowStart + rowCount > Height)
            throw new ArgumentOutOfRangeException(nameof(rowStart));
        if (destination.Length < rowCount * Width)
            throw new ArgumentException("Destination span too small for the requested stripe.", nameof(destination));

        var offset = StreamingFrameStaging.HeaderBytes
            + (long)channel * ChannelBytes
            + (long)rowStart * Width * sizeof(float);
        var byteCount = (long)rowCount * Width * sizeof(float);

        // Serialise seek+read pair so concurrent readers on the same file don't
        // interleave cursors. Multiple readers on different files run freely.
        lock (_gate)
        {
            _fs.Seek(offset, SeekOrigin.Begin);
            _fs.ReadExactly(MemoryMarshal.AsBytes(destination[..(rowCount * Width)]));
        }
    }

    public void Dispose() => _fs.Dispose();
}
