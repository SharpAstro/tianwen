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
            if (IsEnd(bytes.Slice(card, FitsHeaderEditor.CardSize)))
            {
                return (card / FitsHeaderEditor.BlockSize + 1) * FitsHeaderEditor.BlockSize;
            }
        }
        return -1;
    }

    /// <summary>Whether <paramref name="card"/> is the END card, as FITS.Lib's reader tells it: by its first eight bytes.</summary>
    internal static bool IsEnd(ReadOnlySpan<byte> card) => card[..8].SequenceEqual("END     "u8);

    /// <summary>Whether <paramref name="card"/>'s keyword, its first eight bytes less the padding, is <paramref name="keyword"/>.</summary>
    internal static bool KeywordIs(ReadOnlySpan<byte> card, ReadOnlySpan<char> keyword)
    {
        var key = card[..8].Trim((byte)' ');
        if (key.Length != keyword.Length)
        {
            return false;
        }
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] != keyword[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>How a walk of a header's cards ended (<see cref="WalkCards"/>).</summary>
    internal enum Walk
    {
        /// <summary>The END card was reached. The stream stands at the end of its block: where the data, or the next
        /// header, begins.</summary>
        End,

        /// <summary>The visitor had what it came for.</summary>
        Stopped,

        /// <summary>The stream ended inside the header.</summary>
        Truncated,

        /// <summary>No END card within the blocks the caller allowed.</summary>
        TooLong,
    }

    /// <summary>Sees one card of a header, 80 bytes; true ends the walk.</summary>
    internal delegate bool CardVisitor<TState>(ReadOnlySpan<byte> card, ref TState state);

    /// <summary>
    /// Walks the cards of the header that begins where <paramref name="stream"/> stands, a block at a time, handing each
    /// to <paramref name="visit"/> until the END card, which is not handed over, or until the visitor ends it.
    /// </summary>
    /// <remarks>
    /// The one loop three readers each wrote for themselves: reading whole blocks, cutting them into cards, stopping at
    /// END. They read a header WITHOUT opening the file as FITS, each for a reason of its own (one card out of the
    /// first header, per frame per run; the size of each HDU's data on the way to digesting it; a capture's cards as
    /// they are written, to be copied bit for bit), and they differ only in what they do with a card and in how long a
    /// header they follow, which is <paramref name="maxBlocks"/>.
    /// </remarks>
    internal static Walk WalkCards<TState>(Stream stream, ref TState state, CardVisitor<TState> visit, int maxBlocks = FitsHeaderEditor.MaxHeaderBlocks)
    {
        Span<byte> block = stackalloc byte[FitsHeaderEditor.BlockSize];
        for (var read = 0; read < maxBlocks; read++)
        {
            if (stream.ReadAtLeast(block, block.Length, throwOnEndOfStream: false) < block.Length)
            {
                return Walk.Truncated;
            }
            for (var at = 0; at < block.Length; at += FitsHeaderEditor.CardSize)
            {
                var card = block.Slice(at, FitsHeaderEditor.CardSize);
                if (IsEnd(card))
                {
                    return Walk.End;
                }
                if (visit(card, ref state))
                {
                    return Walk.Stopped;
                }
            }
        }
        return Walk.TooLong;
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
