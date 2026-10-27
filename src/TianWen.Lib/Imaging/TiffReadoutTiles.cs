using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using SharpAstro.Tiff;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Single samples of a TIFF master, decoded from the FILE one strip or tile at a time through SharpAstro.Tiff's
/// on-demand segment read, so a viewer's cursor readout does not need the float planes resident (#1853).
/// </summary>
/// <remarks>
/// <para><b>The same number as <c>image[c, y, x]</c>, bit for bit.</b> A readout block (64 x 64) is built from the
/// strips or tiles that intersect it, each decoded by <see cref="TiffReader.ReadSegment(Stream, TiffPageLayout, int, Span{byte})"/>
/// (the same codec, predictor and byte-order swap as the whole-page read) and converted by
/// <see cref="Image.ConvertTiffSamples"/> and <see cref="Image.InvertSamples"/>, the very code
/// <see cref="Image.TryReadTiff"/> converts a page with, so there is no second copy of the conversion. Pinned by
/// <c>TiffReadoutTileTests</c>.</para>
/// <para><b>What it serves.</b> Page 0 of a classic TIFF with an RGB, MinIsBlack or MinIsWhite photometric and
/// 8-bit or 16-bit unsigned or 32-bit float samples, stripped or tiled, chunky or planar, whatever the codec: exactly
/// what the whole-page read converts sample by sample. <see cref="TryOpen"/> answers false for what it converts as a
/// whole plane (Separated / CMYK, rebuilt into RGB), for what it refuses (palette, YCbCr, 32-bit integer, BigTIFF, an
/// empty file) and for any file it cannot lay out, and the caller keeps reading the planes.</para>
/// <para><b>The real cost.</b> Open reads the directory and the segment layout only. A miss opens the file, decodes
/// the segments of one block (a whole strip is as wide as the image, so a strip file reads
/// <c>RowsPerStrip</c> x <c>Width</c> samples per block; a planar page reads every plane of a segment) and closes it
/// again, so a viewer showing the file does not stop a writer replacing it. It is synchronous, paid on the thread
/// that asks. The cache is the base's and holds about 1 MB whatever the file's size. A file that changed (length or
/// write time) is never read.</para>
/// </remarks>
public sealed class TiffReadoutTiles : ReadoutTileSourceBase
{
    private readonly TiffPageLayout _layout;
    private readonly int _bitsPerSample;
    private readonly bool _isFloat;
    private readonly bool _invert;
    private readonly float? _unitScale;

    private TiffReadoutTiles(
        string path, TiffPageLayout layout, int channelCount, bool invert, float? unitScale,
        long length, DateTime writeTimeUtc)
        : base(path, layout.Width, layout.Height, channelCount, length, writeTimeUtc)
    {
        _layout = layout;
        _bitsPerSample = layout.BitsPerSample;
        _isFloat = layout.SampleFormat == TiffSampleFormat.IeeeFloat;
        _invert = invert;
        _unitScale = unitScale;
    }

    /// <summary>
    /// Describes <paramref name="path"/> for segment reads, or answers false when the file is not one this serves (see
    /// the remarks on the type). Matched on the <c>.tif</c> / <c>.tiff</c> extension.
    /// </summary>
    /// <param name="path">The file the document was opened from.</param>
    /// <param name="unitScale">The factor the import multiplied every sample by, or null where it scaled nothing.</param>
    /// <param name="tiles">The source, or null.</param>
    public static bool TryOpen(string path, float? unitScale, [NotNullWhen(true)] out TiffReadoutTiles? tiles)
    {
        tiles = null;
        if (System.IO.Path.GetExtension(path).ToLowerInvariant() is not (".tif" or ".tiff"))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);

            // The importer's own bounds: nothing to map in an empty file, and past int.MaxValue a span cannot address it.
            if (!info.Exists || info.Length is 0 or > int.MaxValue)
            {
                return false;
            }

            if (!TryProbePage(path, info.Length, out var probe)
                || probe.Photometric is not (TiffPhotometric.Rgb or TiffPhotometric.MinIsBlack or TiffPhotometric.MinIsWhite)
                || !IsConvertible(probe.SampleFormat, probe.BitsPerSample))
            {
                return false;
            }

            var layout = TiffReader.ReadLayout(path, 0);
            if (layout.Width != probe.Width || layout.Height != probe.Height
                || layout.SamplesPerPixel != probe.SamplesPerPixel || layout.BitsPerSample != probe.BitsPerSample
                || layout.Width <= 0 || layout.Height <= 0)
            {
                return false;
            }

            // Image carries mono or RGB: alpha and other extras are dropped, as TryReadTiff drops them.
            var channelCount = layout.SamplesPerPixel >= 3 ? 3 : 1;
            tiles = new TiffReadoutTiles(
                path, layout, channelCount, probe.Photometric == TiffPhotometric.MinIsWhite, unitScale,
                info.Length, info.LastWriteTimeUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or InvalidDataException or ArgumentException or FormatException)
        {
            // A file that cannot be laid out for this is a file the planes serve (BigTIFF among them).
            return false;
        }
    }

    // The sample layouts Image.ConvertTiffSamples converts; anything else throws there and the whole read fails.
    private static bool IsConvertible(TiffSampleFormat format, int bitsPerSample)
        => format == TiffSampleFormat.IeeeFloat ? bitsPerSample == 32 : bitsPerSample is 8 or 16;

    /// <inheritdoc/>
    public override bool TryReadImage([NotNullWhen(true)] out Image? image)
        => Image.TryReadTiff(Path, out image);

    protected override bool TryReadTile(int channel, int x0, int y0, int tileWidth, int tileHeight, [NotNullWhen(true)] out float[]? tile)
    {
        tile = null;
        byte[]? buffer = null;
        try
        {
            // Shared read, write and delete, so a viewer showing the file never stops a writer replacing it.
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var samples = new float[tileWidth * tileHeight];
            var x1 = x0 + tileWidth;
            var y1 = y0 + tileHeight;
            var samplesPerPixel = _layout.SamplesPerPixel;
            var bytesPerPixel = samplesPerPixel * (_bitsPerSample / 8);
            var covered = 0;

            // Segments are walked from the block's own corner by the rectangle each one covers, so a strip (the whole
            // width) and a tile (a part of it) are one loop, and an edge tile's padding is never part of a rectangle.
            for (var y = y0; y < y1;)
            {
                var rowEnd = y1;
                for (var x = x0; x < x1;)
                {
                    var index = _layout.SegmentAt(x, y);
                    var rect = _layout.SegmentRectangle(index);
                    var size = rect.Width * rect.Height * bytesPerPixel;
                    if (rect.Width <= 0 || rect.Height <= 0 || size < 0)
                    {
                        return false;
                    }

                    // A strip is as wide as the image and a frame's strips are megabytes: rented, never a LOH array per miss.
                    if (buffer is null || buffer.Length < size)
                    {
                        if (buffer is not null)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }

                        buffer = ArrayPool<byte>.Shared.Rent(size);
                    }

                    var read = TiffReader.ReadSegment(stream, _layout, index, buffer.AsSpan(0, size));
                    if (read.X != rect.X || read.Y != rect.Y || read.Width != rect.Width || read.Height != rect.Height)
                    {
                        return false;
                    }

                    var xa = Math.Max(rect.X, x0);
                    var xb = Math.Min(rect.X + rect.Width, x1);
                    var ya = Math.Max(rect.Y, y0);
                    var yb = Math.Min(rect.Y + rect.Height, y1);
                    for (var row = ya; row < yb; row++)
                    {
                        var source = buffer.AsSpan((((row - rect.Y) * rect.Width) + (xa - rect.X)) * bytesPerPixel,
                            (xb - xa) * bytesPerPixel);
                        var destination = samples.AsSpan(((row - y0) * tileWidth) + (xa - x0), xb - xa);
                        Image.ConvertTiffSamples(source, samplesPerPixel, channel, _bitsPerSample, _isFloat, destination);
                        if (_invert)
                        {
                            Image.InvertSamples(destination);
                        }

                        if (_unitScale is { } unit)
                        {
                            for (var i = 0; i < destination.Length; i++)
                            {
                                destination[i] *= unit;
                            }
                        }
                    }

                    covered += (xb - xa) * (yb - ya);
                    rowEnd = Math.Min(rowEnd, rect.Y + rect.Height);
                    x = rect.X + rect.Width;
                }

                y = rowEnd;
            }

            // Every sample of the block must have come from a stored segment, or the block is not served.
            if (covered != samples.Length)
            {
                return false;
            }

            tile = samples;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or InvalidDataException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>What the layout does not carry: page 0's photometric, taken without decoding a pixel.</summary>
    private readonly record struct PageProbe(
        int Width, int Height, int SamplesPerPixel, int BitsPerSample, TiffPhotometric Photometric, TiffSampleFormat SampleFormat);

    /// <summary>
    /// Page 0's description through <see cref="TiffReader.ReadInto{TSink}"/> with a sink that declines every page, so
    /// the directories are parsed and no strip is touched (the file is mapped, never read).
    /// </summary>
    private static unsafe bool TryProbePage(string path, long length, out PageProbe probe)
    {
        probe = default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var mapping = MemoryMappedFile.CreateFromFile(stream, mapName: null, capacity: 0,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        using var view = mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);

        byte* origin = null;
        try
        {
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref origin);
            var bytes = new ReadOnlySpan<byte>(origin + view.PointerOffset, (int)length);
            var sink = new ProbeSink();
            TiffReader.ReadInto(bytes, ref sink);
            probe = sink.Probe;
            return sink.Seen;
        }
        finally
        {
            if (origin is not null)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    private struct ProbeSink : ITiffStripSink
    {
        public bool Seen;
        public PageProbe Probe;

        public bool BeginPage(int pageIndex, TiffPage page)
        {
            if (pageIndex == 0)
            {
                Seen = true;
                Probe = new PageProbe(page.Width, page.Height, page.SamplesPerPixel, page.BitsPerSample,
                    page.Photometric, page.SampleFormat);
            }

            return false;
        }

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples)
        {
        }
    }
}
