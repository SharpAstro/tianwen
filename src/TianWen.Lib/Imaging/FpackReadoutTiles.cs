using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using nom.tam.fits;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Single samples of a tile-compressed (<c>.fz</c>, fpack) image, decoded from the FILE one stored tile at a time, so a
/// viewer's cursor readout does not need the float planes resident (#1856).
/// </summary>
/// <remarks>
/// <para><b>The same number as <c>image[c, y, x]</c>, bit for bit.</b> A readout block (64 x 64) is built from the stored
/// tiles that intersect it, each decoded by FITS.Lib's own <c>CompressedImageData.ReadTile</c> (the same codec, quantization
/// and dither as the whole-image read), and then converted exactly as <c>Image.TryReadFitsFile(Fits, ...)</c> converts a
/// plane: <c>(float)BSCALE * stored + (float)BZERO</c>, or the float as it is where there is no scaling, and finally
/// multiplied by <c>unitScale</c> as the import's scaling to [0, 1] multiplies a plane. Pinned by
/// <c>FpackReadoutTileTests</c>.</para>
/// <para><b>What it serves.</b> A file whose first image HDU is a tile-compressed image of BITPIX 8, 16, 32 or -32 with two
/// or three axes, the layout the importer's HDU path reads. <see cref="TryOpen"/> answers false for anything else and the
/// caller keeps reading the planes. fpack's default tile is one row, so a block reads 64 stored tiles (per channel); a
/// source of larger tiles decodes fewer, each whole.</para>
/// <para><b>Bounded, not asynchronous.</b> Open reads headers and the tile geometry only. A miss opens the file, decodes the
/// tiles of one block and closes it again, so a viewer showing the file does not stop a writer replacing it. The cache is the
/// base's and so holds about 1 MB whatever the file's size. A file that changed (length or write time) is never read.</para>
/// </remarks>
public sealed class FpackReadoutTiles : ReadoutTileSourceBase
{
    private readonly int _bitPix;
    private readonly float _zero;
    private readonly float _scale;
    private readonly float? _unitScale;
    private readonly int _axisCount;

    private FpackReadoutTiles(
        string path, int bitPix, int width, int height, int channelCount, int axisCount,
        float bzero, float bscale, float? unitScale, long length, DateTime writeTimeUtc)
        : base(path, width, height, channelCount, length, writeTimeUtc)
    {
        _bitPix = bitPix;
        _axisCount = axisCount;
        _zero = bzero;
        _scale = bscale;
        _unitScale = unitScale;
    }

    /// <summary>
    /// Describes <paramref name="path"/> for tile reads, or answers false when the file is not one this serves (see the
    /// remarks on the type). Matched on the <c>.fz</c> extension alone.
    /// </summary>
    /// <param name="path">The file the document was opened from.</param>
    /// <param name="unitScale">The factor the import multiplied every sample by, or null where it scaled nothing.</param>
    /// <param name="tiles">The source, or null.</param>
    public static bool TryOpen(string path, float? unitScale, [NotNullWhen(true)] out FpackReadoutTiles? tiles)
    {
        tiles = null;
        if (!path.EndsWith(".fz", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            using var fits = Image.OpenFits(path);
            if (fits.ReadFirstImageHdu() is not CompressedImageHDU hdu
                || hdu.Axes is not { Length: 2 or 3 } axes
                || hdu.BitPix is not (8 or 16 or 32 or -32)
                || hdu.CompressedData is not { } data
                || data.TileCount <= 0)
            {
                return false;
            }

            var (channelCount, height, width) = axes.Length == 2 ? (1, axes[0], axes[1]) : (axes[0], axes[1], axes[2]);
            tiles = new FpackReadoutTiles(
                path, hdu.BitPix, width, height, channelCount, axes.Length,
                (float)hdu.BZero, (float)hdu.BScale, unitScale, info.Length, info.LastWriteTimeUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FitsException)
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
            using var fits = Image.OpenFits(Path);
            if (fits.ReadFirstImageHdu() is not CompressedImageHDU { CompressedData: { } data })
            {
                return false;
            }

            var samples = new float[tileWidth * tileHeight];
            var done = _bitPix switch
            {
                8 => Fill<byte>(data, channel, x0, y0, tileWidth, tileHeight, samples),
                16 => Fill<short>(data, channel, x0, y0, tileWidth, tileHeight, samples),
                32 => Fill<int>(data, channel, x0, y0, tileWidth, tileHeight, samples),
                _ => Fill<float>(data, channel, x0, y0, tileWidth, tileHeight, samples),
            };

            tile = done ? samples : null;
            return done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FitsException)
        {
            return false;
        }
    }

    private bool Fill<T>(CompressedImageData data, int channel, int x0, int y0, int tileWidth, int tileHeight, float[] samples)
        where T : unmanaged, System.Numerics.INumberBase<T>
    {
        T[]? buffer = null;
        var x1 = x0 + tileWidth;
        var y1 = y0 + tileHeight;
        var covered = 0;
        for (var n = 0; n < data.TileCount; n++)
        {
            var rect = data.GetTileRectangle(n);
            var (tx, ty, tw, th) = (rect.Start[0], rect.Start[1], rect.Size[0], rect.Size[1]);
            var (tz, td) = _axisCount == 3 ? (rect.Start[2], rect.Size[2]) : (0, 1);
            if (channel < tz || channel >= tz + td || tx >= x1 || tx + tw <= x0 || ty >= y1 || ty + th <= y0)
            {
                continue;
            }

            if (buffer is null || buffer.Length < rect.PixelCount)
            {
                buffer = new T[rect.PixelCount];
            }

            if (data.ReadTile(n, buffer) < rect.PixelCount)
            {
                return false;
            }

            var planeOffset = (channel - tz) * tw * th;
            var xa = Math.Max(tx, x0);
            var xb = Math.Min(tx + tw, x1);
            for (var y = Math.Max(ty, y0); y < Math.Min(ty + th, y1); y++)
            {
                var source = planeOffset + ((y - ty) * tw);
                var destination = ((y - y0) * tileWidth) - x0;
                for (var x = xa; x < xb; x++)
                {
                    samples[destination + x] = Convert(buffer[source + (x - tx)]);
                }
            }

            covered += (xb - xa) * (Math.Min(ty + th, y1) - Math.Max(ty, y0));
        }

        // Every sample of the block must have come from a stored tile, or the block is not served.
        return covered == tileWidth * tileHeight;
    }

    /// <summary>
    /// <c>Image.TryReadFitsFile(Fits, ...)</c>'s <c>ConvertChannel</c> (<c>bscale * float(stored) + bzero</c>), or a float
    /// with no scaling as it is, then the import's scaling to [0, 1].
    /// </summary>
    private float Convert<T>(T stored) where T : unmanaged, System.Numerics.INumberBase<T>
    {
        var physical = typeof(T) == typeof(float) && _scale == 1f && _zero == 0f
            ? float.CreateTruncating(stored)
            : (_scale * float.CreateTruncating(stored)) + _zero;
        return _unitScale is { } unit ? physical * unit : physical;
    }
}
