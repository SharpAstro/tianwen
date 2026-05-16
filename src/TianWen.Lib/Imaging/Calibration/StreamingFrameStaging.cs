using System;
using System.Buffers;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Disk-backed staging for warped frames during stacking. Header byte 12 is
/// a flag field that selects layout + pixel type:
/// <list type="bullet">
///   <item><b>0 (float32, full canvas)</b>: 16-byte header (channels / height /
///         width / flags), then channels * h * w float32 LE.</item>
///   <item><b>1 = <see cref="FlagHasFootprint"/> (float32 + footprint)</b>:
///         32-byte header (footprint origin/size in bytes 16..31), then
///         channels * footprintH * footprintW float32. Reader NaN-pads
///         outside footprint.</item>
///   <item><b>2 = <see cref="FlagHalfPrecision"/> (float16, full canvas)</b>:
///         16-byte header, then channels * h * w float16 (System.Half) LE.
///         Reader unpacks Half-&gt;float on stripe reads.</item>
///   <item><b>3 = both (float16 + footprint)</b>: 32-byte header, footprint
///         data in float16. (Combo deferred until both individual strategies
///         have shipped; reader code path already handles it.)</item>
/// </list>
/// </summary>
public static class StreamingFrameStaging
{
    /// <summary>Base header size in bytes (channels / height / width / flags).</summary>
    public const int HeaderBytesV1 = 16;

    /// <summary>Extended header size when <see cref="FlagHasFootprint"/> is set.
    /// Bytes 16..31 carry footprint origin (x, y) + size (w, h).</summary>
    public const int HeaderBytesV2 = 32;

    /// <summary>Legacy compat alias for <see cref="HeaderBytesV1"/>.</summary>
    public const int HeaderBytes = HeaderBytesV1;

    /// <summary>Legacy compat: no flags set, float32 full canvas.</summary>
    public const byte VersionV1 = 0;

    /// <summary>Legacy compat: <see cref="FlagHasFootprint"/> only,
    /// float32 + footprint.</summary>
    public const byte VersionV2WithFootprint = FlagHasFootprint;

    /// <summary>Flag bit indicating bytes 16..31 carry footprint metadata
    /// and pixel data is footprint-sized rather than full canvas.</summary>
    public const byte FlagHasFootprint = 0x01;

    /// <summary>Flag bit indicating pixel data is stored as
    /// <see cref="System.Half"/> (2 bytes/pixel) rather than float32.
    /// Reader unpacks to float on read.</summary>
    public const byte FlagHalfPrecision = 0x02;

    /// <summary>Writes <paramref name="image"/>'s pixels to <paramref name="path"/>
    /// in the v1 (full-canvas) staging format.</summary>
    public static void Write(Image image, string path)
    {
        var (channels, width, height) = image.Shape;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        Span<byte> header = stackalloc byte[HeaderBytesV1];
        BitConverter.TryWriteBytes(header[0..4], channels);
        BitConverter.TryWriteBytes(header[4..8], height);
        BitConverter.TryWriteBytes(header[8..12], width);
        // bytes 12..15 reserved -- zero from stackalloc (byte 12 = VersionV1)
        fs.Write(header);

        for (var ch = 0; ch < channels; ch++)
        {
            var bytes = MemoryMarshal.AsBytes(image.GetChannelSpan(ch));
            fs.Write(bytes);
        }
    }

    /// <summary>
    /// Writes only the <paramref name="footprint"/> sub-region of <paramref name="image"/>
    /// to <paramref name="path"/> in the v2 staging format. Useful when the
    /// warped frame has NaN edges from the canvas-overhang regions where the
    /// source didn't cover -- staging only the non-NaN footprint trims disk
    /// usage 5-50% depending on frame motion.
    /// </summary>
    /// <param name="image">Canvas-sized warped frame.</param>
    /// <param name="path">Output file path.</param>
    /// <param name="footprint">Sub-rectangle of the canvas to actually persist.
    /// Must be entirely within <paramref name="image"/>'s shape.</param>
    /// <exception cref="ArgumentException">Footprint out of canvas bounds.</exception>
    public static void WriteWithFootprint(Image image, string path, Rectangle footprint)
    {
        var (channels, canvasWidth, canvasHeight) = image.Shape;
        if (footprint.X < 0 || footprint.Y < 0
            || footprint.Right > canvasWidth || footprint.Bottom > canvasHeight
            || footprint.Width <= 0 || footprint.Height <= 0)
        {
            throw new ArgumentException(
                $"Footprint {footprint} out of canvas bounds [0,0)-({canvasWidth},{canvasHeight}).",
                nameof(footprint));
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        Span<byte> header = stackalloc byte[HeaderBytesV2];
        BitConverter.TryWriteBytes(header[0..4], channels);
        BitConverter.TryWriteBytes(header[4..8], canvasHeight);
        BitConverter.TryWriteBytes(header[8..12], canvasWidth);
        header[12] = VersionV2WithFootprint;
        // bytes 13..15 reserved (zero from stackalloc)
        BitConverter.TryWriteBytes(header[16..20], footprint.X);
        BitConverter.TryWriteBytes(header[20..24], footprint.Y);
        BitConverter.TryWriteBytes(header[24..28], footprint.Width);
        BitConverter.TryWriteBytes(header[28..32], footprint.Height);
        fs.Write(header);

        // Pixel data: for each channel, write footprint.Height contiguous rows
        // of footprint.Width floats. Use a row buffer rather than per-pixel
        // writes to avoid the FileStream per-call overhead.
        var rowBuffer = ArrayPool<float>.Shared.Rent(footprint.Width);
        try
        {
            var rowSpan = rowBuffer.AsSpan(0, footprint.Width);
            for (var ch = 0; ch < channels; ch++)
            {
                var arr = image.GetChannelArray(ch);
                for (var y = 0; y < footprint.Height; y++)
                {
                    var srcRow = footprint.Y + y;
                    for (var x = 0; x < footprint.Width; x++)
                    {
                        rowSpan[x] = arr[srcRow, footprint.X + x];
                    }
                    fs.Write(MemoryMarshal.AsBytes(rowSpan));
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rowBuffer);
        }
    }

    /// <summary>
    /// Writes <paramref name="image"/>'s pixels to <paramref name="path"/> as
    /// <see cref="System.Half"/> (float16) -- half the bytes of float32 at a
    /// 10-bit mantissa cost. Suitable for staging in disk-constrained
    /// environments; quantisation noise sits below typical sensor read noise.
    /// Reader unpacks Half-&gt;float on stripe reads so the integrator is
    /// unchanged.
    /// </summary>
    public static void WriteHalf(Image image, string path)
    {
        var (channels, width, height) = image.Shape;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        Span<byte> header = stackalloc byte[HeaderBytesV1];
        BitConverter.TryWriteBytes(header[0..4], channels);
        BitConverter.TryWriteBytes(header[4..8], height);
        BitConverter.TryWriteBytes(header[8..12], width);
        header[12] = FlagHalfPrecision;
        // bytes 13..15 reserved (zero)
        fs.Write(header);

        // Encode each channel one row at a time. The row buffer is reused
        // across rows + channels; one allocation per call.
        var halfBuffer = ArrayPool<Half>.Shared.Rent(width);
        try
        {
            var halfSpan = halfBuffer.AsSpan(0, width);
            for (var ch = 0; ch < channels; ch++)
            {
                var arr = image.GetChannelArray(ch);
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        halfSpan[x] = (Half)arr[y, x];
                    }
                    fs.Write(MemoryMarshal.AsBytes(halfSpan));
                }
            }
        }
        finally
        {
            ArrayPool<Half>.Shared.Return(halfBuffer);
        }
    }
}

/// <summary>
/// Read-only random-access view into a <see cref="StreamingFrameStaging"/> file.
/// Supports both v1 (full-canvas) and v2 (footprint-cropped) layouts -- callers
/// see canvas-sized stripes either way; v2 stripes are NaN-padded outside the
/// footprint region. Thread-safe across concurrent <see cref="ReadStripe"/>
/// calls via an internal mutex (the underlying stream is single-cursor).
/// </summary>
public sealed class StreamingFrameReader : IDisposable
{
    private readonly FileStream _fs;
    private readonly object _gate = new();

    private readonly byte _flags;
    private readonly bool _hasFootprint;
    private readonly bool _isHalf;
    private readonly int _bytesPerSample;
    private readonly int _footprintX;
    private readonly int _footprintY;
    private readonly int _footprintWidth;
    private readonly int _footprintHeight;

    public string Path { get; }
    public int Channels { get; }
    /// <summary>Canvas height. For v1 files this equals the file height; for
    /// v2 files the on-disk channel is footprint-sized but stripes returned to
    /// callers are canvas-sized.</summary>
    public int Height { get; }
    /// <summary>Canvas width. See <see cref="Height"/>.</summary>
    public int Width { get; }

    /// <summary>
    /// Optional in-memory cache of the full canvas <see cref="Image"/> this
    /// reader was constructed against. Set via <see cref="SetCachedImage"/>
    /// when the strategy still has the warped Image alive after staging --
    /// <see cref="ReadStripe"/> will slice from it instead of reading the
    /// staged file. Held as a <see cref="WeakReference{T}"/> so the GC can
    /// reclaim under pressure without breaking correctness: a dead reference
    /// falls through to the disk path. The strategy is responsible for the
    /// strong-ref retention policy (typically a <see cref="FrameCache"/>
    /// keyed by frame index).
    /// </summary>
    private WeakReference<Image>? _cachedImage;

    /// <summary>Bytes one canvas-sized channel plane would occupy. Kept for
    /// backward compatibility with callers that compute output buffer sizes
    /// from this; the actual on-disk channel for v2 is smaller.</summary>
    public long ChannelBytes => (long)Height * Width * sizeof(float);

    public StreamingFrameReader(string path)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        Span<byte> header = stackalloc byte[StreamingFrameStaging.HeaderBytesV1];
        _fs.ReadExactly(header);
        Channels = BitConverter.ToInt32(header[0..4]);
        Height = BitConverter.ToInt32(header[4..8]);
        Width = BitConverter.ToInt32(header[8..12]);
        _flags = header[12];
        _hasFootprint = (_flags & StreamingFrameStaging.FlagHasFootprint) != 0;
        _isHalf = (_flags & StreamingFrameStaging.FlagHalfPrecision) != 0;
        _bytesPerSample = _isHalf ? sizeof(short) : sizeof(float);

        if (Channels <= 0 || Height <= 0 || Width <= 0)
        {
            throw new InvalidDataException(
                $"Bad streaming-staging header: c={Channels}, h={Height}, w={Width} in {path}.");
        }

        // Reject unknown flag bits so future format extensions don't get
        // silently misinterpreted by an old reader.
        const byte KnownFlags = StreamingFrameStaging.FlagHasFootprint | StreamingFrameStaging.FlagHalfPrecision;
        if ((_flags & ~KnownFlags) != 0)
        {
            throw new InvalidDataException($"Unknown staging flags 0x{_flags:X2} in {path}.");
        }

        if (_hasFootprint)
        {
            Span<byte> footprintBytes = stackalloc byte[16];
            _fs.ReadExactly(footprintBytes);
            _footprintX = BitConverter.ToInt32(footprintBytes[0..4]);
            _footprintY = BitConverter.ToInt32(footprintBytes[4..8]);
            _footprintWidth = BitConverter.ToInt32(footprintBytes[8..12]);
            _footprintHeight = BitConverter.ToInt32(footprintBytes[12..16]);

            if (_footprintX < 0 || _footprintY < 0
                || _footprintWidth <= 0 || _footprintHeight <= 0
                || _footprintX + _footprintWidth > Width
                || _footprintY + _footprintHeight > Height)
            {
                throw new InvalidDataException(
                    $"Bad footprint: origin=({_footprintX},{_footprintY}) " +
                    $"size=({_footprintWidth}x{_footprintHeight}) on canvas {Width}x{Height} in {path}.");
            }
        }
        else
        {
            // No footprint: file pixel data spans the full canvas.
            _footprintX = 0;
            _footprintY = 0;
            _footprintWidth = Width;
            _footprintHeight = Height;
        }
    }

    private int HeaderSize => _hasFootprint ? StreamingFrameStaging.HeaderBytesV2 : StreamingFrameStaging.HeaderBytesV1;

    /// <summary>
    /// Register the staged frame's in-memory <see cref="Image"/> so subsequent
    /// <see cref="ReadStripe"/> calls can slice from RAM instead of seeking
    /// the staged file. The reader holds a weak reference; the strategy must
    /// retain the strong reference (typically via a <see cref="FrameCache"/>)
    /// or the GC may reclaim the image before integration finishes, in which
    /// case the reader transparently falls back to the disk path.
    /// </summary>
    public void SetCachedImage(Image image)
    {
        _cachedImage = new WeakReference<Image>(image);
    }

    /// <summary>Reads <paramref name="rowCount"/> consecutive canvas rows
    /// starting at <paramref name="rowStart"/> from <paramref name="channel"/>
    /// into <paramref name="destination"/>. For v2 frames, rows outside the
    /// footprint are filled with NaN so the integrator's NaN-skipping handles
    /// them uniformly. Destination span must hold at least
    /// <c>rowCount * Width</c> floats.</summary>
    public void ReadStripe(int channel, int rowStart, int rowCount, Span<float> destination)
    {
        if ((uint)channel >= (uint)Channels)
            throw new ArgumentOutOfRangeException(nameof(channel));
        if ((uint)rowStart >= (uint)Height || rowStart + rowCount > Height)
            throw new ArgumentOutOfRangeException(nameof(rowStart));
        if (destination.Length < rowCount * Width)
            throw new ArgumentException("Destination span too small for the requested stripe.", nameof(destination));

        // Cache fast path: when the strategy retained the warped Image and it
        // hasn't been GC'd, slice directly from the in-memory channel data.
        // Skips the entire disk seek + byte-decode + scatter pipeline below.
        // Warped images carry NaN outside the footprint by construction (the
        // warp's out-of-source-bounds clamp) so the slice has the same
        // NaN-padded shape the staged-file path produces.
        if (_cachedImage is { } weak && weak.TryGetTarget(out var cached)
            && cached.ChannelCount == Channels
            && cached.Height == Height && cached.Width == Width)
        {
            var channelData = cached.GetChannelArray(channel);
            for (var r = 0; r < rowCount; r++)
            {
                for (var c = 0; c < Width; c++)
                {
                    destination[r * Width + c] = channelData[rowStart + r, c];
                }
            }
            return;
        }

        lock (_gate)
        {
            // Unified path: works for all four combos (full canvas / footprint
            // × float32 / float16). Read the on-disk slice (footprint rows that
            // overlap the requested stripe) into a scratch buffer, optionally
            // unpacking Half->float on the way, then scatter into the
            // canvas-sized destination with NaN padding outside the footprint.
            var stripeFloats = rowCount * Width;
            destination[..stripeFloats].Fill(float.NaN);

            var fpEndY = _footprintY + _footprintHeight;
            var overlapStart = Math.Max(rowStart, _footprintY);
            var overlapEnd = Math.Min(rowStart + rowCount, fpEndY);
            if (overlapEnd <= overlapStart) return;

            var overlapRows = overlapEnd - overlapStart;
            var fileRowStart = overlapStart - _footprintY;
            var fileChannelBytes = (long)_footprintHeight * _footprintWidth * _bytesPerSample;
            var byteOffset = HeaderSize
                + (long)channel * fileChannelBytes
                + (long)fileRowStart * _footprintWidth * _bytesPerSample;

            var sliceFloats = overlapRows * _footprintWidth;

            if (_isHalf)
            {
                // Read Half samples, unpack to float, scatter into destination.
                var halfBuf = ArrayPool<Half>.Shared.Rent(sliceFloats);
                var floatBuf = ArrayPool<float>.Shared.Rent(sliceFloats);
                try
                {
                    var halfSpan = halfBuf.AsSpan(0, sliceFloats);
                    var floatSpan = floatBuf.AsSpan(0, sliceFloats);
                    _fs.Seek(byteOffset, SeekOrigin.Begin);
                    _fs.ReadExactly(MemoryMarshal.AsBytes(halfSpan));
                    UnpackHalfToFloat(halfSpan, floatSpan);
                    ScatterRows(floatSpan, destination, overlapStart, overlapRows, rowStart);
                }
                finally
                {
                    ArrayPool<Half>.Shared.Return(halfBuf);
                    ArrayPool<float>.Shared.Return(floatBuf);
                }
            }
            else if (_hasFootprint)
            {
                // float32 + footprint: read into scratch then scatter.
                var floatBuf = ArrayPool<float>.Shared.Rent(sliceFloats);
                try
                {
                    var floatSpan = floatBuf.AsSpan(0, sliceFloats);
                    _fs.Seek(byteOffset, SeekOrigin.Begin);
                    _fs.ReadExactly(MemoryMarshal.AsBytes(floatSpan));
                    ScatterRows(floatSpan, destination, overlapStart, overlapRows, rowStart);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(floatBuf);
                }
            }
            else
            {
                // float32 full-canvas: read straight into destination (footprint
                // == canvas means scatter would be a no-op copy).
                _fs.Seek(byteOffset, SeekOrigin.Begin);
                _fs.ReadExactly(MemoryMarshal.AsBytes(destination[..stripeFloats]));
            }
        }
    }

    private void ScatterRows(ReadOnlySpan<float> source, Span<float> destination, int overlapStart, int overlapRows, int rowStart)
    {
        for (var r = 0; r < overlapRows; r++)
        {
            var destRow = overlapStart - rowStart + r;
            var destOffset = destRow * Width + _footprintX;
            source.Slice(r * _footprintWidth, _footprintWidth)
                .CopyTo(destination.Slice(destOffset, _footprintWidth));
        }
    }

    private static void UnpackHalfToFloat(ReadOnlySpan<Half> src, Span<float> dst)
    {
        // Scalar loop. JIT lowers to vcvtph2ps on x64; comparable to a
        // hand-vectorised conversion. Could swap to a TensorPrimitives helper
        // when one ships for Half->float in batch.
        for (var i = 0; i < src.Length; i++)
        {
            dst[i] = (float)src[i];
        }
    }

    public void Dispose() => _fs.Dispose();
}
