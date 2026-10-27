using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using nom.tam.fits;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Single samples of a plain FITS image, read from the FILE through a small cache of tiles around where
/// they were asked for, so a viewer's cursor readout does not need the float planes resident (#368).
/// </summary>
/// <remarks>
/// <para><b>The same number as <c>image[c, y, x]</c>, bit for bit.</b> A sample is converted as
/// FITS.Lib's <c>FitsReader.ReadPlane</c> converts it (<c>(float)BSCALE * stored + (float)BZERO</c>,
/// multiplied and then added, and a float image with no scaling copied as it is), and then multiplied by
/// <c>unitScale</c> exactly as <c>Image.ScaleFloatValuesToUnitInPlace</c> multiplies a plane
/// (<c>1 / UnitScaleDivisor</c>, the factor a document reads off the image BEFORE that scaling spends
/// it), or not at all where the file was on [0, 1] already. Pinned over every BITPIX and scaling by
/// <c>ReadoutTileTests</c>.</para>
/// <para><b>What it serves.</b> An uncompressed, ungzipped FITS whose first image HDU is BITPIX 8, 16, 32
/// or -32 with two or three axes, the layout <c>Image.TryReadThroughFitsReader</c> reads. <see cref="TryOpen"/>
/// answers false for anything else (gzip, TIFF, CR2, BITPIX 64; a <c>.fz</c> is <see cref="FpackReadoutTiles"/>'s), and the caller keeps reading
/// the planes. It is never a way to read a file that the importer would not.</para>
/// <para><b>Bounded, not asynchronous.</b> A miss reads one 64 x 64 tile: at most 64 positional reads of
/// at most 256 bytes, one file handle opened and closed around them, and 16 KB for the tile. The cache
/// holds <see cref="MaxTiles"/> tiles and is emptied when full, so its memory is bounded at about 1 MB
/// whatever the file's size, and a hit allocates nothing. The file is opened per miss, never held, so a
/// viewer showing it does not stop a writer replacing it (a live stack's master is rewritten in
/// place).</para>
/// <para><b>A file that changed is not read.</b> Its length and write time are compared with the ones taken
/// at open on every miss; once they differ every read answers false, and the caller falls back to the
/// planes it already holds rather than quote a different file's pixels.</para>
/// </remarks>
public sealed class FitsReadoutTiles : ReadoutTileSourceBase
{
    private readonly long _dataOffset;
    private readonly long _planeBytes;
    private readonly int _bitPix;
    private readonly int _bytesPerSample;
    private readonly float _zero;
    private readonly float _scale;
    private readonly float? _unitScale;

    private FitsReadoutTiles(
        string path, int bitPix, int width, int height, int channelCount, long dataOffset,
        double bzero, double bscale, float? unitScale, long length, DateTime writeTimeUtc)
        : base(path, width, height, channelCount, length, writeTimeUtc)
    {
        _bitPix = bitPix;
        _bytesPerSample = Math.Abs(bitPix) / 8;
        _dataOffset = dataOffset;
        _planeBytes = (long)width * height * _bytesPerSample;
        _zero = (float)bzero;
        _scale = (float)bscale;
        _unitScale = unitScale;
    }

    /// <summary>
    /// Describes <paramref name="path"/> for tile reads, or answers false when the file is not one this serves
    /// (see the remarks on the type).
    /// </summary>
    /// <param name="path">The file the document was opened from.</param>
    /// <param name="unitScale">
    /// The factor the document multiplied every sample by on import, <c>1 / Image.UnitScaleDivisor</c>, or null
    /// where it scaled nothing (the frame was unit-referred already).
    /// </param>
    /// <param name="tiles">The source, or null.</param>
    public static bool TryOpen(string path, float? unitScale, [NotNullWhen(true)] out FitsReadoutTiles? tiles)
    {
        tiles = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || !FitsReader.TryOpen(path, out var reader))
            {
                return false;
            }

            using (reader)
            {
                // The layout Image.TryReadThroughFitsReader takes, and no other: what the importer would hand to
                // the HDU path is left to the planes.
                if (reader.BitPix is not (8 or 16 or 32 or -32) || reader.Hdu.Axes is not { Length: 2 or 3 })
                {
                    return false;
                }

                tiles = new FitsReadoutTiles(
                    path, reader.BitPix, reader.Width, reader.Height, reader.Planes, reader.DataOffset,
                    reader.BZero, reader.BScale, unitScale, info.Length, info.LastWriteTimeUtc);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A file that cannot be opened for this is a file the planes serve.
            return false;
        }
    }

    /// <inheritdoc/>
    public override bool TryReadImage([NotNullWhen(true)] out Image? image)
        => Image.TryReadFitsFile(Path, out image);

    protected override bool TryReadTile(int channel, int x0, int y0, int tileWidth, int tileHeight, [NotNullWhen(true)] out float[]? tile)
    {
        tile = null;
        try
        {
            using var handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var samples = new float[tileWidth * tileHeight];
            Span<byte> row = stackalloc byte[TileSize * sizeof(int)];
            row = row[..(tileWidth * _bytesPerSample)];
            var planeStart = _dataOffset + (channel * _planeBytes);
            for (var r = 0; r < tileHeight; r++)
            {
                var offset = planeStart + ((((long)(y0 + r) * Width) + x0) * _bytesPerSample);
                var filled = 0;
                while (filled < row.Length)
                {
                    var read = RandomAccess.Read(handle, row[filled..], offset + filled);
                    if (read == 0)
                    {
                        return false;
                    }

                    filled += read;
                }

                Convert(row, samples.AsSpan(r * tileWidth, tileWidth));
            }

            tile = samples;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>FitsReader.ReadPlane</c>'s conversion (<c>BigEndianSamples.ToSingle</c>, single precision, scalar tail
    /// and vector body computing the same expression), then the import's scaling to [0, 1].
    /// </summary>
    private void Convert(ReadOnlySpan<byte> source, Span<float> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            var sample = source.Slice(i * _bytesPerSample, _bytesPerSample);
            var physical = _bitPix switch
            {
                8 => (_scale * sample[0]) + _zero,
                16 => (_scale * BinaryPrimitives.ReadInt16BigEndian(sample)) + _zero,
                32 => (_scale * BinaryPrimitives.ReadInt32BigEndian(sample)) + _zero,
                // No scaling: the bits as they are, which keeps a payload NaN and a negative zero that the
                // arithmetic below would change.
                _ when _scale == 1f && _zero == 0f => BinaryPrimitives.ReadSingleBigEndian(sample),
                _ => (_scale * BinaryPrimitives.ReadSingleBigEndian(sample)) + _zero,
            };
            destination[i] = _unitScale is { } unit ? physical * unit : physical;
        }
    }
}
