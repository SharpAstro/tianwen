using System;
using System.IO;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// A FITS header as the bytes of a FILE: whole blocks of <see cref="FitsHeaderEditor.BlockSize"/> bytes, cards of
/// <see cref="FitsHeaderEditor.CardSize"/>, ended by the block that holds the <c>END</c> card.
/// </summary>
/// <remarks>
/// Where a header ends is read off those bytes and never off a parsed header (#1529). FITS.Lib keeps one card of
/// each repeated <c>KEY = value</c>, and its <c>Header.Size</c> is the size of the cards it KEPT: an Astro Pixel
/// Processor integration writes 18 cards twice, 149 cards in five blocks, and parses to 127 in four. A header
/// cache sized by the parsed header held four blocks of such a file, without its END, and the parser read those
/// bytes for ever.
/// </remarks>
internal static class FitsHeaderBlocks
{
    /// <summary>
    /// The length in bytes of the header at the start of <paramref name="bytes"/>, through the block that holds
    /// its END card; -1 when no END card is in them. The END card is told as FITS.Lib's reader tells it, by a
    /// card's first eight bytes, so a header measured here is one that reader ends at the same card.
    /// </summary>
    internal static int LengthThroughEnd(ReadOnlySpan<byte> bytes)
    {
        for (var card = 0; card + FitsHeaderEditor.CardSize <= bytes.Length; card += FitsHeaderEditor.CardSize)
        {
            if (bytes.Slice(card, 8).SequenceEqual("END     "u8))
            {
                return (card / FitsHeaderEditor.BlockSize + 1) * FitsHeaderEditor.BlockSize;
            }
        }
        return -1;
    }

    /// <summary>Whether <paramref name="bytes"/> hold a header's END card, and so a whole header.</summary>
    internal static bool HoldsEnd(ReadOnlySpan<byte> bytes) => LengthThroughEnd(bytes) >= 0;

    /// <summary>
    /// Reads the header at the start of <paramref name="file"/> through the block that holds its END card.
    /// <paramref name="atLeast"/> is how much is read before the first look, a size the caller already has (the
    /// parsed header's, which is the file's own for all but a header with repeated cards), so the usual header
    /// costs one read; a longer one is followed a block at a time. Null when the file ends first, or when no END
    /// card is found within <see cref="FitsHeaderEditor.MaxHeaderBlocks"/> blocks.
    /// </summary>
    internal static byte[]? ReadThroughEnd(Stream file, int atLeast)
    {
        var block = FitsHeaderEditor.BlockSize;
        var limit = FitsHeaderEditor.MaxHeaderBlocks * block;
        var header = new byte[Math.Clamp((atLeast + block - 1) / block * block, block, limit)];
        var looked = 0;
        while (true)
        {
            if (file.ReadAtLeast(header.AsSpan(looked), header.Length - looked, throwOnEndOfStream: false) < header.Length - looked)
            {
                return null;
            }
            if (LengthThroughEnd(header.AsSpan(looked)) is var found and >= 0)
            {
                var length = looked + found;
                return length == header.Length ? header : header[..length];
            }
            if (header.Length >= limit)
            {
                return null;
            }
            looked = header.Length;
            Array.Resize(ref header, header.Length + block);
        }
    }
}
