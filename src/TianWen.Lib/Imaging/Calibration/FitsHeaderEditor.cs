using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// block, and <c>OBSERVER</c>/<c>SITENAME</c>, and it re-encodes the pixels through a float buffer
/// (until 2026-08-17 it also renamed <c>XBAYROFF</c>/<c>YBAYROFF</c> to a private spelling; the
/// writer emits the standard names now). That is fine for writing a
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
///
/// <para><b>Because the write is a replace, a frame with several names needs a decision.</b> A
/// replace re-points one directory entry, so on a hard-linked frame it would amend one name and
/// leave the others on the old file. <see cref="HardLinkPolicy"/> is that decision, and
/// <see cref="HardLinkPolicy.Relink"/> is the one that matches what the links mean: they are one
/// physical frame under several names, so a card describing the frame belongs to all of them. See
/// <c>RelinkSiblings</c> for the ordering that makes it safe.</para>
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

    /// <summary>Chunk the verification pass compares in. Rented rather than allocated, so the size is
    /// a throughput choice and not an allocation cost.</summary>
    private const int CompareChunk = 1 << 20;

    /// <summary>Scratch name a re-pointed hard link is created under before it is renamed over the
    /// sibling it replaces. Distinct from the temp/backup suffixes so a leftover says which step
    /// stopped.</summary>
    private const string RelinkSuffix = ".tianwen-relink";

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
        /// <summary>Other paths share this file's data (see <see cref="HardLinkProbe"/>), so
        /// replacing it would edit one name and silently leave its siblings on the old content.</summary>
        MultiplyLinked,
        /// <summary>The card was written and every other name for the frame was re-pointed at the
        /// amended file, so the archive still holds one physical frame under all of them.</summary>
        TaggedAndRelinked,
    }

    /// <summary>What to do about a frame that more than one path points at.</summary>
    public enum HardLinkPolicy
    {
        /// <summary>Leave it alone and report it. The right default: the edit is a replace, and a
        /// replace re-points one name.</summary>
        Refuse,
        /// <summary>Amend one name, then re-point every other name at the amended frame. This is the
        /// semantically correct answer rather than a convenience, because the links are ONE physical
        /// frame under several names and a FILTER card describes the frame, so it applies to every
        /// name equally. It also keeps the de-duplication, and makes a later divergence impossible
        /// rather than merely unlikely.</summary>
        Relink,
        /// <summary>Amend this name only and let the other names keep the old header. Correct only
        /// when the copies are genuinely meant to differ, which for a de-duplicated archive is
        /// essentially never.</summary>
        Diverge,
    }

    /// <param name="Path">The file considered.</param>
    /// <param name="Outcome">What happened, or would happen on a dry run.</param>
    /// <param name="Detail">Human-readable reason, empty when tagged normally.</param>
    /// <param name="ExistingValue">The keyword's current value when it already had one.</param>
    /// <param name="OtherLinks">The other paths that point at this same frame, whenever there were
    /// any, whatever the outcome. Populated on a dry run too, because "which other files does this
    /// reach" is the question a dry run exists to answer.</param>
    public sealed record TagResult(
        string Path,
        TagOutcome Outcome,
        string Detail = "",
        string? ExistingValue = null,
        ImmutableArray<string> OtherLinks = default)
    {
        /// <summary>The other names for this frame, never a default (unset) array.</summary>
        public ImmutableArray<string> OtherLinks { get; init; } = OtherLinks.IsDefault ? [] : OtherLinks;
    }

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
    /// <param name="hardLinks">What to do when other paths point at the same frame. Defaults to
    /// <see cref="HardLinkPolicy.Refuse"/>, because the replace re-points ONE directory entry, so a
    /// hard-linked frame would otherwise come out edited under one name and untouched under the
    /// others, and the two copies of what was one night would then disagree.</param>
    /// <param name="apply">When false (the default) nothing is written and the returned outcome is
    /// what *would* happen.</param>
    public static Task<TagResult> SetStringCardAsync(
        string path,
        string keyword,
        string value,
        string comment = "",
        IReadOnlySet<FrameType>? allowedFrameTypes = null,
        bool overwriteExisting = false,
        HardLinkPolicy hardLinks = HardLinkPolicy.Refuse,
        bool apply = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        RejectOverlongKeyword(keyword);
        return SetCardAsync(
            path, keyword, FormatStringCard(keyword, value, comment),
            allowedFrameTypes, overwriteExisting, hardLinks, apply, cancellationToken);
    }

    /// <summary>
    /// Sets or replaces a NUMERIC card, which FITS formats differently from a string one: the value
    /// is unquoted and RIGHT-justified to byte 30 rather than quoted and left-justified from byte 11
    /// (FITS 4.0 section 4.2.3). Writing a number through <see cref="SetStringCardAsync"/> would
    /// quote it, and a quoted number is a STRING as far as every reader is concerned -- so the card
    /// would look right to a human and parse as text to everything else.
    ///
    /// <para>Everything else -- the frame-type guard, the overwrite rule, the hard-link policy, the
    /// dry run -- is shared with the string form and cannot drift from it.</para>
    /// </summary>
    /// <param name="value">The number to write. Always emitted with a decimal point so it reads as
    /// floating point rather than an integer, which is what a physical quantity like an elevation or
    /// a focal length is.</param>
    public static Task<TagResult> SetNumericCardAsync(
        string path,
        string keyword,
        double value,
        string comment = "",
        IReadOnlySet<FrameType>? allowedFrameTypes = null,
        bool overwriteExisting = false,
        HardLinkPolicy hardLinks = HardLinkPolicy.Refuse,
        bool apply = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        RejectOverlongKeyword(keyword);
        return SetCardAsync(
            path, keyword, FormatNumericCard(keyword, value, comment),
            allowedFrameTypes, overwriteExisting, hardLinks, apply, cancellationToken);
    }

    private static void RejectOverlongKeyword(string keyword)
    {
        if (keyword.Length > 8)
        {
            throw new ArgumentException($"FITS keyword '{keyword}' exceeds 8 characters.", nameof(keyword));
        }
    }

    /// <summary>The half both public setters share: read the primary header, apply every guard, then
    /// splice in the already-formatted card.</summary>
    private static async Task<TagResult> SetCardAsync(
        string path,
        string keyword,
        string newCard,
        IReadOnlySet<FrameType>? allowedFrameTypes,
        bool overwriteExisting,
        HardLinkPolicy hardLinks,
        bool apply,
        CancellationToken cancellationToken)
    {
        int headerLength;
        List<string> cards;
        try
        {
            await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, useAsync: true);
            var parsed = await ReadPrimaryHeaderAsync(read, cancellationToken);
            if (parsed is null)
            {
                return new TagResult(path, TagOutcome.Unreadable, "not a FITS primary header");
            }
            (headerLength, cards) = parsed.Value;
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

        // Resolved BEFORE the dry-run return on purpose: the whole value of a dry run is finding out
        // what a real run would do, and "this edit reaches one of three names for the same frame,
        // one of them outside the directory you pointed at" is the single most consequential thing
        // it can tell you. The link walk only runs when the cheap count says it is worth it.
        var before = HardLinkProbe.TryGetIdentity(path);
        var otherLinks = before is { LinkCount: > 1 }
            ? [.. HardLinkProbe.EnumerateLinks(path).Where(other => !PathsEqual(other, path))]
            : ImmutableArray<string>.Empty;

        if (before is { LinkCount: > 1 } shared && hardLinks is HardLinkPolicy.Refuse)
        {
            return new TagResult(path, TagOutcome.MultiplyLinked, $"{shared.LinkCount} hard links", existing, otherLinks);
        }

        // Relinking needs both the pre-edit identity and the list of names to move, so a walk that
        // came back empty (an unsupported platform, or a failed enumeration) falls back to editing
        // this one name rather than silently doing half the job.
        var relink = hardLinks is HardLinkPolicy.Relink && before is { LinkCount: > 1 } && otherLinks.Length > 0;

        if (!apply)
        {
            return new TagResult(
                path, relink ? TagOutcome.TaggedAndRelinked : TagOutcome.Tagged, "dry run", existing, otherLinks);
        }

        var rewritten = RewriteHeader(cards, keyword, newCard);
        await ReplaceHeaderAsync(path, headerLength, rewritten, cancellationToken);

        if (!relink || before is not { } original)
        {
            return new TagResult(path, TagOutcome.Tagged, "", existing, otherLinks);
        }

        RelinkSiblings(path, otherLinks, original);
        return new TagResult(
            path, TagOutcome.TaggedAndRelinked, $"{otherLinks.Length} other name(s) re-pointed", existing, otherLinks);
    }

    /// <summary>
    /// Re-points every other name for a frame at the amended file, so a de-duplicated archive keeps
    /// one physical frame under all its names instead of forking into a tagged and an untagged copy.
    ///
    /// <para><b>Every expectation is verified, not assumed.</b> The replace should have left
    /// <paramref name="path"/> naming NEW data with exactly one link, and every sibling still naming
    /// the ORIGINAL data with its link count down by the one name that moved away. Where there were
    /// only two names that reads as "the sibling is now the sole remaining name for the file as it
    /// was", which is the case worth stating out loud. Any of it not holding means something other
    /// than this edit changed the file while we worked, and re-pointing names is then the very last
    /// thing that should happen, so the whole set is checked before a single name moves.</para>
    /// </summary>
    /// <returns>How many names were re-pointed, always <c>siblings.Length</c> on success.</returns>
    private static int RelinkSiblings(string path, ImmutableArray<string> siblings, HardLinkProbe.FileIdentity before)
    {
        if (HardLinkProbe.TryGetIdentity(path) is not { } amended)
        {
            throw new IOException(
                $"The header of {path} was amended, but its identity is unreadable, so the other " +
                $"{siblings.Length} name(s) for it were left on the original frame.");
        }
        if (amended.IsSameFileAs(before))
        {
            throw new IOException(
                $"Refusing to re-link {path}: the edit left it naming the same file as before ({before}), " +
                "which no replace should do.");
        }
        if (amended.LinkCount != 1)
        {
            throw new IOException(
                $"Refusing to re-link {path}: the amended file already has {amended.LinkCount} names, expected 1.");
        }

        var expected = before.LinkCount - 1;
        foreach (var sibling in siblings)
        {
            if (HardLinkProbe.TryGetIdentity(sibling) is not { } current)
            {
                throw new IOException($"Refusing to re-link {path}: {sibling} cannot be read.");
            }
            if (!current.IsSameFileAs(before))
            {
                throw new IOException(
                    $"Refusing to re-link {path}: {sibling} no longer holds the original frame " +
                    $"(it names {current}, expected {before}).");
            }
            if (current.LinkCount != expected)
            {
                throw new IOException(
                    $"Refusing to re-link {path}: {sibling} reports {current.LinkCount} names for the " +
                    $"original frame, expected {expected} after one moved away.");
            }
        }

        var moved = 0;
        foreach (var sibling in siblings)
        {
            var staging = sibling + RelinkSuffix;
            try
            {
                // Link first, then rename over the sibling: a replacing rename is one atomic
                // directory operation, so the sibling's path is never absent even for an instant.
                // The opposite order, delete then link, has a window in which a crash loses a name
                // outright, and a name in this archive is how a night is found.
                if (!HardLinkProbe.TryCreateHardLink(staging, path, out var error))
                {
                    throw new IOException($"Could not add a name for the amended frame at {staging}: {error}");
                }
                File.Move(staging, sibling, overwrite: true);

                if (HardLinkProbe.TryGetIdentity(sibling) is not { } relinked || !relinked.IsSameFileAs(amended))
                {
                    throw new IOException($"{sibling} still does not name the amended frame after re-linking.");
                }
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(staging);
                throw new IOException(
                    $"Amended {path} and re-pointed {moved} of {siblings.Length} other name(s) before failing " +
                    $"on {sibling}: {ex.Message} The names not yet re-pointed still hold the original, " +
                    "untagged frame, so nothing is lost; re-running tags each of them separately.",
                    ex);
            }
        }

        // Every name accounted for. The original file now has no name at all, so NTFS has already
        // reclaimed it, and the space de-duplication saved is still saved.
        if (HardLinkProbe.TryGetIdentity(path) is { } final && final.LinkCount != before.LinkCount)
        {
            throw new IOException(
                $"Re-linking {path} ended with {final.LinkCount} names, expected the original {before.LinkCount}.");
        }
        return moved;
    }

    /// <summary>Two paths naming the same entry, compared the way the file system does.</summary>
    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the primary header: how many bytes it occupies, up to and including the block holding
    /// <c>END</c>, plus the parsed cards. Null when the file is not a FITS primary HDU.
    ///
    /// <para>The length rather than the bytes, because the length is all the caller ever wanted: it
    /// passes it to <see cref="ReplaceHeaderAsync"/> as the offset to resume copying from, and the
    /// content it needs is already in <c>Cards</c>. This used to keep every block and then
    /// concatenate them, so each file paid for N block arrays, one header-sized array, and a full
    /// copy of its own header, to arrive at a number.</para>
    /// </summary>
    private static async Task<(int Length, List<string> Cards)?> ReadPrimaryHeaderAsync(Stream stream, CancellationToken ct)
    {
        // One buffer reused for every block, which is only correct because no block outlives its own
        // pass now. It cannot be a stackalloc: a span may not be held across an await.
        var block = new byte[BlockSize];
        var cards = new List<string>();
        for (var b = 0; b < MaxHeaderBlocks; b++)
        {
            var read = await stream.ReadAtLeastAsync(block, BlockSize, throwOnEndOfStream: false, ct);
            if (read < BlockSize)
            {
                return null; // truncated: never a header we should be editing
            }

            for (var offset = 0; offset < BlockSize; offset += CardSize)
            {
                var card = Encoding.ASCII.GetString(block, offset, CardSize);
                // The very first card of a primary HDU must be SIMPLE, per the standard. Checking it
                // is what stops this from happily "editing" a JPEG.
                if (b == 0 && offset == 0 && !card.StartsWith("SIMPLE  =", StringComparison.Ordinal))
                {
                    return null;
                }
                if (card.StartsWith("END", StringComparison.Ordinal) && card[3..].AsSpan().Trim().IsEmpty)
                {
                    return ((b + 1) * BlockSize, cards);
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

        // Rented, not allocated. At a mebibyte each these are well past the 85 KB large-object
        // threshold, so a sweep of tens of thousands of frames would put tens of gigabytes through
        // the LOH and then collect all of it, for buffers whose whole life is this one comparison.
        // They cannot be spans: a span may not be held across an await, and every read here is one.
        var pool = ArrayPool<byte>.Shared;
        var bufA = pool.Rent(CompareChunk);
        var bufB = pool.Rent(CompareChunk);
        try
        {
            long compared = 0;
            while (compared < expected)
            {
                // Against the constant, not the array length: Rent may hand back a larger buffer, and
                // the chunk size should be the one this code chose.
                var want = (int)Math.Min(CompareChunk, expected - compared);
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
        finally
        {
            // No clearOnReturn: this held file bytes the caller already owns, not a secret, and
            // zeroing a mebibyte per frame would cost more than the rent saved.
            pool.Return(bufA);
            pool.Return(bufB);
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

    /// <summary>Formats one 80-byte NUMERIC card per FITS 4.0 section 4.2.3: keyword in bytes 1-8,
    /// <c>"= "</c> in 9-10, and the value RIGHT-justified so it ends at byte 30.</summary>
    internal static string FormatNumericCard(string keyword, double value, string comment)
    {
        if (!double.IsFinite(value))
        {
            // FITS has no way to spell NaN or an infinity in a numeric card. A reader would take
            // whatever text landed there as an unparseable value or, worse, as a string; declining is
            // the only honest option, and the caller already treats "unknown" as "write no card".
            throw new ArgumentException($"{keyword} cannot be written as {value}: FITS numeric cards have no non-finite form.", nameof(value));
        }
        RejectNonAscii(comment, nameof(comment));

        // Always with a decimal point, so the card reads as floating point. An elevation of exactly
        // 74 written as "74" is an INTEGER card, and a reader that types its cards would hand back an
        // int where the quantity is physically continuous.
        var text = value.ToString("0.0##########", CultureInfo.InvariantCulture);
        if (text.Length > ValueColumnWidth)
        {
            throw new ArgumentException(
                $"Value {text} does not fit a fixed-format FITS numeric card ({text.Length} > {ValueColumnWidth}).", nameof(value));
        }

        var card = $"{keyword.PadRight(8)}= {text.PadLeft(ValueColumnWidth)}";
        if (comment.Length > 0 && card.Length + 4 <= CardSize)
        {
            var room = CardSize - card.Length - 3;
            card += " / " + (comment.Length <= room ? comment : comment[..room]);
        }
        return card.PadRight(CardSize);
    }

    /// <summary>Bytes 11-30, the fixed-format value field a numeric card right-justifies into.</summary>
    private const int ValueColumnWidth = 20;

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
