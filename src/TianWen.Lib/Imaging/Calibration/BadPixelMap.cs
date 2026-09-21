using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using CommunityToolkit.HighPerformance;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// The on-disk form of a bad pixel mask: the <see cref="BitMatrix"/> the detectors produce, as a
/// raster a FITS reader can open, and back again.
/// </summary>
/// <remarks>
/// <para><b>The format is AstroPixelProcessor's, deliberately.</b> This archive already holds 18
/// distinct APP maps named <c>BPM-&lt;camera&gt;-&lt;W&gt;x&lt;H&gt;.fits</c>, and they are
/// <c>BITPIX = 8</c> rasters at one byte per photosite carrying three levels:
/// <see cref="LinearLevel"/> (127) for a pixel that behaves, <see cref="HotLevel"/> (255) for a hot
/// one and <see cref="ColdLevel"/> (0) for a cold one. Reading one measured 2,121,856 pixels of
/// which 58,629 were hot (2.763%, kappa 3, over 200 darks), which is where the level convention and
/// the header cards below come from. Writing the same shape means their maps and ours are the same
/// kind of thing: ours can be opened by their tools, theirs can be read back here as a reference
/// mask to check ours against, and neither needs a converter.</para>
///
/// <para><b>We flag one state, not three.</b> A <see cref="BitMatrix"/> bit is "this pixel is not
/// to be trusted", and the detectors behind it converge a threshold rather than classify a defect,
/// so a written mask uses 127 and 255 only. A map READ back treats any level that is not
/// <see cref="LinearLevel"/> as flagged, which is what makes an APP map with cold pixels in it
/// legible here without inventing a second bit.</para>
///
/// <para><b>Storage.</b> A mask is one byte per pixel before compression and overwhelmingly one
/// repeated value, which is exactly the case gzip is good at: the APP map above is 2.12 MB raw and
/// 0.091 MB gzipped, 23x, and a sparser mask does better. So the sidecar is written
/// <c>.fits.gz</c> and there is no need for a coordinate list, which would be smaller still but
/// would be a format only we can read.</para>
/// </remarks>
public static class BadPixelMap
{
    /// <summary>The level APP writes for a pixel that behaves, and the only level this reader
    /// treats as unflagged.</summary>
    public const float LinearLevel = 127f;

    /// <summary>The level APP writes for a hot pixel, and the one this writer uses for every
    /// flagged pixel: our detectors converge a threshold rather than sort hot from cold.</summary>
    public const float HotLevel = 255f;

    /// <summary>The level APP writes for a cold pixel. Read as flagged, never written.</summary>
    public const float ColdLevel = 0f;

    /// <summary>Half a level, so a raster that came back through <c>BSCALE</c> or a float
    /// conversion still compares equal to the level it was written as.</summary>
    private const float LevelTolerance = 0.5f;

    /// <summary>Whether a raster sample means "do not trust this pixel".</summary>
    public static bool IsFlagged(float level) => MathF.Abs(level - LinearLevel) > LevelTolerance;

    /// <summary>
    /// The mask as a raster image, ready to write. One plane per channel of the mask, on the
    /// geometry the mask was built at, which is the SENSOR's and not the canvas's.
    /// </summary>
    /// <remarks>
    /// Filled a row at a time and then punched a word at a time: the fill is one memset per plane
    /// and the flagging visits only the words that hold a set bit, so a mask at a realistic defect
    /// density (a few thousand pixels of several million) touches a small fraction of the plane
    /// after the fill. That is what makes writing one on every integration cost nothing worth
    /// measuring.
    /// </remarks>
    public static Image ToImage(BitMatrix[] mask, in ImageMeta meta)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.Length == 0)
        {
            throw new ArgumentException("A bad pixel map needs at least one channel.", nameof(mask));
        }

        var rows = mask[0].Rows;
        var columns = mask[0].Columns;
        var data = Image.CreateChannelData(mask.Length, rows, columns);
        var anyFlagged = false;

        for (var c = 0; c < mask.Length; c++)
        {
            var m = mask[c];
            if (m.Rows != rows || m.Columns != columns)
            {
                throw new ArgumentException(
                    $"Channel {c} is {m.Columns}x{m.Rows}, channel 0 is {columns}x{rows}.", nameof(mask));
            }

            var plane = data[c];
            plane.AsSpan2D().Fill(LinearLevel);

            var wordsPerRow = m.WordsPerRow;
            for (var y = 0; y < rows; y++)
            {
                for (var w = 0; w < wordsPerRow; w++)
                {
                    var word = m.GetWord(y, w);
                    if (word == 0)
                    {
                        continue;
                    }

                    anyFlagged = true;
                    var baseColumn = w << 6;
                    while (word != 0)
                    {
                        var bit = BitOperations.TrailingZeroCount(word);
                        var x = baseColumn + bit;
                        if (x < columns)
                        {
                            plane[y, x] = HotLevel;
                        }

                        word &= word - 1;
                    }
                }
            }
        }

        return new Image(
            data: data,
            bitDepth: BitDepth.Int8,
            maxValue: anyFlagged ? HotLevel : LinearLevel,
            minValue: LinearLevel,
            pedestal: 0f,
            imageMeta: meta);
    }

    /// <summary>
    /// The inverse: a raster read off disk, as the mask it stands for. Works on a map this writer
    /// produced and on an APP one, since both use the same levels.
    /// </summary>
    public static BitMatrix[] FromImage(Image map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var masks = new BitMatrix[map.ChannelCount];
        for (var c = 0; c < map.ChannelCount; c++)
        {
            var mask = new BitMatrix(map.Height, map.Width);
            var plane = map.GetChannelSpan(c);
            for (var y = 0; y < map.Height; y++)
            {
                var offset = y * map.Width;
                for (var x = 0; x < map.Width; x++)
                {
                    if (IsFlagged(plane[offset + x]))
                    {
                        mask[y, x] = true;
                    }
                }
            }

            masks[c] = mask;
        }

        return masks;
    }

    /// <summary>Reads a bad pixel map from disk, ours or APP's, as the mask it stands for.</summary>
    public static bool TryRead(string path, [NotNullWhen(true)] out BitMatrix[]? mask)
    {
        if (!Image.TryReadFitsFile(path, out var image))
        {
            mask = null;
            return false;
        }

        mask = FromImage(image);
        return true;
    }
}
