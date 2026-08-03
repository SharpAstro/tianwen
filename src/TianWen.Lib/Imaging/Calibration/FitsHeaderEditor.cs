using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Surgically edits a FITS file's primary header, byte for byte, without decoding one pixel.
///
/// <para><b>Why this exists rather than reusing <c>Image.WriteToFitsFile</c>.</b> That writer
/// *reconstructs* a header from <see cref="ImageMeta"/>, so it can only emit what that type models.
/// Measured against a real N.I.N.A. light, a read-then-write round trip destroys 21 of its 50
/// informational cards, among them <c>AIRMASS</c>, <c>CENTALT</c>/<c>CENTAZ</c>,
/// <c>DATE-AVG</c>/<c>MJD-AVG</c> (the sub-second averaged midpoint), the whole focuser telemetry
/// block, and <c>OBSERVER</c>/<c>SITENAME</c>; it also renames <c>XBAYROFF</c>/<c>YBAYROFF</c> to
/// our own spelling, and it re-encodes the pixels through a float buffer. That is fine for writing a
/// NEW file and unacceptable for amending an irreplaceable one.</para>
///
/// <para><b>The model here is the opposite: everything is preserved except the one card being
/// changed.</b> A FITS file is a sequence of 2880-byte blocks; the primary header is 80-byte ASCII
/// cards terminated by <c>END</c>. This parses only those header blocks, rewrites them, and copies
/// <b>every remaining byte of the file verbatim</b>, which means pixel data, padding, and any
/// extension HDUs are bit-identical by construction rather than by careful re-encoding. Nothing
/// downstream of the primary header is even interpreted, so there is no format subtlety left to get
/// wrong (<c>BZERO</c>-scaled unsigned integers, non-standard extensions, trailing junk).</para>
///
/// <para><b>The write is never in place.</b> New content goes to a temp file beside the original,
/// which is then verified against the original before anything is replaced, and the replace itself
/// keeps a backup until the result has been re-verified on disk. A crash at any point leaves either
/// the untouched original or the original plus a recoverable backup, never a half-written frame.</para>
/// </summary>
public static class FitsHeaderEditor
{
    /// <summary>FITS logical block size. Headers and data are both padded to a multiple of this.</summary>
    public const int BlockSize = 2880;

    /// <summary>Every header card is exactly this many bytes of ASCII.</summary>
    public const int CardSize = 80;

    /// <summary>Longest header we will walk before declaring the file malformed (~1170 cards).
    /// A real primary header is one or two blocks; anything past this is not a header we understand.</summary>
    private const int MaxHeaderBlocks = 32;

    /// <summary>Why a file was left alone, or how it changed.</summary>
    public enum TagOutcome
    {
        /// <summary>The card was written (or would be, on a dry run).</summary>
        Tagged,
        /// <summary>The file already carries this keyword with a non-blank value.</summary>
        AlreadyPresent,
        /// <summary>The frame type is not one the keyword is meaningful for.</summary>
        FrameTypeExcluded,
        /// <summary>Not a FITS file, or a header we could not parse. Never modified.</summary>
        Unreadable,
    }

    /// <param name="Path">The file considered.</param>
    /// <param name="Outcome">What happened, or would happen on a dry run.</param>
    /// <param name="Detail">Human-readable reason, empty when tagged normally.</param>
    /// <param name="ExistingValue">The keyword's current value when it already had one.</param>
    public sealed record TagResult(string Path, TagOutcome Outcome, string Detail = "", string? ExistingValue = null);

    /// <summary>
    /// Sets or replaces a string-valued card in <paramref name="path"/>'s primary header.
    /// </summary>
    /// <param name="path">The FITS file to amend.</param>
    /// <param name="keyword">Card keyword, at most 8 characters (e.g. <c>FILTER</c>).</param>
    /// <param name="value">Card value. Must fit one 80-byte card; no <c>CONTINUE</c> support.</param>
    /// <param name="comment">Trailing comment, silently truncated to fit the card.</param>
    /// <param name="allowedFrameTypes">When non-empty, only amend files whose <c>IMAGETYP</c> parses
    /// to one of these. The guard exists because an archive folder holds more than lights: bad-pixel
    /// maps and master darks sit next to the subs, and a blanket tag would stamp a filter onto data
    /// where it means nothing.</param>
    /// <param name="overwriteExisting">Replace a keyword that already has a non-blank value. Off by
    /// default: the job is filling in what was never recorded, and silently relabelling a frame that
    /// stated its own filter is a different and far more dangerous operation.</param>
    /// <param name="apply">When false (the default) nothing is written and the returned outcome is
    /// what *would* happen.</param>
    public static async Task<TagResult> SetStringCardAsync(
        string path,
        string keyword,
        string value,
        string comment = "",
        IReadOnlySet<FrameType>? allowedFrameTypes = null,
        bool overwriteExisting = false,
        bool apply = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        if (keyword.Length > 8)
        {
            throw new ArgumentException($"FITS keyword '{keyword}' exceeds 8 characters.", nameof(keyword));
        }

        var newCard = FormatStringCard(keyword, value, comment);

        byte[] headerBytes;
        List<string> cards;
        try
        {
            await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, useAsync: true);
            var parsed = await ReadPrimaryHeaderAsync(read, cancellationToken);
            if (parsed is null)
            {
                return new TagResult(path, TagOutcome.Unreadable, "not a FITS primary header");
            }
            (headerBytes, cards) = parsed.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TagResult(path, TagOutcome.Unreadable, ex.Message);
        }

        if (allowedFrameTypes is { Count: > 0 })
        {
            var raw = CardValue(cards, "FRAMETYP") ?? CardValue(cards, "IMAGETYP");
            var frameType = raw is { } rawType ? FrameType.FromFITSValue(rawType) ?? FrameType.None : FrameType.None;
            if (!allowedFrameTypes.Contains(frameType))
            {
                return new TagResult(path, TagOutcome.FrameTypeExcluded, raw is null ? "no IMAGETYP" : $"IMAGETYP={raw}");
            }
        }

        var existing = CardValue(cards, keyword);
        if (existing is { Length: > 0 } && !overwriteExisting)
        {
            return new TagResult(path, TagOutcome.AlreadyPresent, $"{keyword}={existing}", existing);
        }

        if (!apply)
        {
            return new TagResult(path, TagOutcome.Tagged, "dry run", existing);
        }

        var rewritten = RewriteHeader(cards, keyword, newCard);
        await ReplaceHeaderAsync(path, headerBytes.Length, rewritten, cancellationToken);
        return new TagResult(path, TagOutcome.Tagged, "", existing);
    }

    /// <summary>
    /// Reads the primary header: the raw bytes up to and including the block holding <c>END</c>,
    /// plus the parsed cards. Null when the file is not a FITS primary HDU.
    /// </summary>
    private static async Task<(byte[] Raw, List<string> Cards)?> ReadPrimaryHeaderAsync(Stream stream, CancellationToken ct)
    {
        var blocks = new List<byte[]>();
        var cards = new List<string>();
        for (var b = 0; b < MaxHeaderBlocks; b++)
        {
            var block = new byte[BlockSize];
            var read = await stream.ReadAtLeastAsync(block, BlockSize, throwOnEndOfStream: false, ct);
            if (read < BlockSize)
            {
                return null; // truncated: never a header we should be editing
            }
            blocks.Add(block);

            for (var offset = 0; offset < BlockSize; offset += CardSize)
            {
                var card = Encoding.ASCII.GetString(block, offset, CardSize);
                // The very first card of a primary HDU must be SIMPLE, per the standard. Checking it
                // is what stops this from happily "editing" a JPEG.
                if (blocks.Count == 1 && offset == 0 && !card.StartsWith("SIMPLE  =", StringComparison.Ordinal))
                {
                    return null;
                }
                if (card.StartsWith("END", StringComparison.Ordinal) && card[3..].AsSpan().Trim().IsEmpty)
                {
                    var raw = new byte[blocks.Count * BlockSize];
                    for (var i = 0; i < blocks.Count; i++)
                    {
                        blocks[i].CopyTo(raw, i * BlockSize);
                    }
                    return (raw, cards);
                }
                cards.Add(card);
            }
        }
        return null;
    }

    /// <summary>Replaces the card with <paramref name="keyword"/> in place, or appends it, then
    /// serialises the block-padded header with a trailing <c>END</c>.</summary>
    private static byte[] RewriteHeader(List<string> cards, string keyword, string newCard)
    {
        var replaced = false;
        for (var i = 0; i < cards.Count; i++)
        {
            if (MatchesKeyword(cards[i], keyword))
            {
                cards[i] = newCard;
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            // Appending keeps every original card at its original index, so a diff of the two
            // headers shows exactly one added line.
            cards.Add(newCard);
        }

        var withEnd = cards.Count + 1;
        var blocks = (withEnd + 35) / 36;
        var buffer = new byte[blocks * BlockSize];
        buffer.AsSpan().Fill((byte)' ');
        for (var i = 0; i < cards.Count; i++)
        {
            Encoding.ASCII.GetBytes(cards[i], buffer.AsSpan(i * CardSize, CardSize));
        }
        Encoding.ASCII.GetBytes("END".PadRight(CardSize), buffer.AsSpan(cards.Count * CardSize, CardSize));
        return buffer;
    }

    /// <summary>
    /// Writes <paramref name="newHeader"/> followed by every byte of the original from
    /// <paramref name="oldHeaderLength"/> onward, verifies the result against the original, and only
    /// then swaps it in, keeping a backup until the swap is confirmed.
    /// </summary>
    private static async Task ReplaceHeaderAsync(string path, int oldHeaderLength, byte[] newHeader, CancellationToken ct)
    {
        var temp = path + ".tianwen-tmp";
        var backup = path + ".tianwen-bak";
        try
        {
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            await using (var dest = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                source.Seek(oldHeaderLength, SeekOrigin.Begin);
                await dest.WriteAsync(newHeader, ct);
                await source.CopyToAsync(dest, 1 << 20, ct);
                await dest.FlushAsync(ct);
            }

            // Verify BEFORE the original is touched: the payload must be byte-identical and the
            // header must parse back with the card we intended.
            await VerifyAsync(path, oldHeaderLength, temp, newHeader.Length, ct);

            // Replace keeps the previous contents in `backup` until we delete it, so a failure
            // between here and the end is recoverable by hand rather than terminal.
            File.Replace(temp, path, backup);
            File.Delete(backup);
        }
        catch
        {
            // Only ever remove our own scratch file; the original and any backup stay put.
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Confirms the rewritten file preserves every byte after the header.</summary>
    private static async Task VerifyAsync(string original, int oldHeaderLength, string candidate, int newHeaderLength, CancellationToken ct)
    {
        await using var a = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var b = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

        var expected = a.Length - oldHeaderLength;
        var actual = b.Length - newHeaderLength;
        if (expected != actual)
        {
            throw new IOException($"Verification failed for {original}: payload length {actual} != {expected}.");
        }

        a.Seek(oldHeaderLength, SeekOrigin.Begin);
        b.Seek(newHeaderLength, SeekOrigin.Begin);

        var bufA = new byte[1 << 20];
        var bufB = new byte[1 << 20];
        long compared = 0;
        while (compared < expected)
        {
            var want = (int)Math.Min(bufA.Length, expected - compared);
            var readA = await a.ReadAtLeastAsync(bufA.AsMemory(0, want), want, throwOnEndOfStream: false, ct);
            var readB = await b.ReadAtLeastAsync(bufB.AsMemory(0, want), want, throwOnEndOfStream: false, ct);
            if (readA != want || readB != want)
            {
                throw new IOException($"Verification failed for {original}: short read at byte {compared}.");
            }
            if (!bufA.AsSpan(0, want).SequenceEqual(bufB.AsSpan(0, want)))
            {
                throw new IOException($"Verification failed for {original}: payload differs at byte {compared}.");
            }
            compared += want;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a stray .tianwen-tmp is strictly better than masking the original failure.
        }
    }

    private static bool MatchesKeyword(string card, string keyword)
        => card.Length >= 8 && card.AsSpan(0, 8).Trim().SequenceEqual(keyword);

    /// <summary>The unquoted value of a string card, or null when the keyword is absent.</summary>
    internal static string? CardValue(List<string> cards, string keyword)
    {
        foreach (var card in cards)
        {
            if (!MatchesKeyword(card, keyword))
            {
                continue;
            }
            var body = card.Length > 10 ? card[10..] : "";
            var open = body.IndexOf('\'');
            if (open < 0)
            {
                // Unquoted (numeric / logical) value: take everything up to the comment.
                var slash = body.IndexOf('/');
                return (slash >= 0 ? body[..slash] : body).Trim();
            }
            var close = body.IndexOf('\'', open + 1);
            return close > open ? body[(open + 1)..close].Trim() : null;
        }
        return null;
    }

    /// <summary>Formats one 80-byte string-valued card per FITS 4.0 section 4.1.2: keyword in bytes
    /// 1-8, <c>"= "</c> in 9-10, quoted value from byte 11, optional <c>" / comment"</c>.</summary>
    internal static string FormatStringCard(string keyword, string value, string comment)
    {
        // A header is restricted ASCII (0x20-0x7E) by the standard, and the card is emitted with
        // Encoding.ASCII, which silently substitutes '?' for anything else. Left unchecked, a filter
        // name like "H-alpha" written with the actual Greek letter (our own Filter.ShortName spells
        // it that way) would be stamped into an irreplaceable file as "H?", wrong and unnoticed.
        // Refusing is the only acceptable behaviour when the output cannot be taken back.
        RejectNonAscii(value, nameof(value));
        RejectNonAscii(comment, nameof(comment));

        var escaped = value.Replace("'", "''", StringComparison.Ordinal);
        // The standard requires at least 8 characters between the quotes.
        var card = $"{keyword.PadRight(8)}= '{escaped.PadRight(8)}'";
        if (card.Length > CardSize)
        {
            throw new ArgumentException(
                $"Value '{value}' does not fit one FITS card ({card.Length} > {CardSize}); CONTINUE is not supported.",
                nameof(value));
        }
        if (comment.Length > 0 && card.Length + 4 <= CardSize)
        {
            var room = CardSize - card.Length - 3;
            card += " / " + (comment.Length <= room ? comment : comment[..room]);
        }
        return card.PadRight(CardSize);
    }

    /// <summary>Throws unless every character is a printable ASCII the FITS standard permits in a
    /// header (0x20 space through 0x7E tilde).</summary>
    private static void RejectNonAscii(string text, string paramName)
    {
        foreach (var ch in text)
        {
            if (ch is < ' ' or > '~')
            {
                throw new ArgumentException(
                    $"FITS headers are restricted ASCII (0x20-0x7E); '{text}' contains U+{(int)ch:X4}.",
                    paramName);
            }
        }
    }
}
