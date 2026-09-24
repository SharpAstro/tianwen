using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Points a RAW frame at its CURATED twin, so one night stops costing two copies of itself.
///
/// <para><b>The direction is the whole design.</b> The curated frame is the one that survives and
/// the raw name becomes another name for it, never the other way round. A curated header carries
/// work the raw one does not: a filter identity established by measurement, a backfilled frame
/// type, a corrected focal length. Linking the other way would throw exactly that away, and it is
/// the only part of the archive that cannot be re-derived from the pixels.</para>
///
/// <para><b>Pixels decide, headers veto.</b> Two frames are the same frame when their DATA units
/// digest equal (<see cref="StackManifest.DigestData"/>), which is what makes a header difference
/// expected rather than alarming. The header is then a veto and not a key: the sweep refuses when
/// the RAW carries a card the curated one LACKS, because that card is about to stop existing.
/// Measured over 1,500 payload-identical pairs on 2026-09-22, that never happened: the curated
/// header added FILTER 403 times, IMAGETYP 76 and PIXSCALE 8, and carried a corrected value for
/// FRAMETYP, SITEELEV, FOCALLEN, TELESCOP and OBJECT. The veto is here so the sweep stays safe if
/// that ever stops being true, rather than because it is expected to fire.</para>
///
/// <para><b>What this is NOT.</b> A hard link is one set of bytes under several names, not a
/// backup. Running this over a curated copy converts a real second copy into a second name, which
/// is a deliberate trade of redundancy for space and belongs to whoever runs it. And nothing here
/// deletes a raw FOLDER: that is <see cref="ArchivePruneSweep"/>, deliberately a separate pass,
/// because dropping a name and choosing which bytes survive are different decisions.</para>
///
/// <para>Every write goes through <see cref="HardLinkProbe.RepointTo"/>, which links under a
/// staging name and renames it over the target, so the raw path is never absent even for an
/// instant.</para>
/// </summary>
public static class ArchiveLinkSweep
{
    /// <summary>What happened to one raw frame, or what would happen on a dry run.</summary>
    public enum LinkOutcome
    {
        /// <summary>The raw name now points at the curated frame, and its own bytes are released.</summary>
        Linked,
        /// <summary>Both names already point at one file. Nothing to do and nothing to reclaim.</summary>
        AlreadyOneFile,
        /// <summary>No curated frame holds these pixels, so this one is not yet curated.</summary>
        NoTwin,
        /// <summary>The data digests disagree at the moment of writing. A ledger said otherwise and
        /// the ledger is stale; the frames are not interchangeable.</summary>
        PayloadDiffers,
        /// <summary>The raw header carries a card the curated header does not, so linking would
        /// discard it. The fix is to curate that card, not to link anyway.</summary>
        HeaderWouldLose,
        /// <summary>The two are on different volumes, where a hard link cannot reach. Anything on
        /// another drive is a second physical copy whatever the counts say.</summary>
        DifferentVolume,
        /// <summary>One of the two could not be read, so nothing was decided about it.</summary>
        Unreadable,
        /// <summary>The write was attempted and failed. The raw frame is untouched.</summary>
        Failed,
    }

    /// <summary>How much header difference the sweep tolerates before it refuses.</summary>
    public enum HeaderPolicy
    {
        /// <summary>The default and the point of the sweep: the curated header wins wherever the two
        /// differ, and the only refusal is a card the raw has and the curated lacks.</summary>
        CuratedWins,

        /// <summary>Refuse any difference at all, so only byte-identical frames link. Useful for a
        /// first pass over an archive whose curation is not trusted yet.</summary>
        RequireIdentical,
    }

    /// <param name="RawPath">The frame that would become a name for the curated one.</param>
    /// <param name="CuratedPath">The frame whose bytes and header survive; empty when there is none.</param>
    /// <param name="Outcome">What happened, or would happen on a dry run.</param>
    /// <param name="Detail">Why, in a form worth putting in front of someone. Empty when it linked.</param>
    /// <param name="BytesReleased">What the raw frame occupied, and so what linking gives back.
    /// Zero for every outcome but <see cref="LinkOutcome.Linked"/>.</param>
    public readonly record struct LinkResult(
        string RawPath, string CuratedPath, LinkOutcome Outcome, string Detail, long BytesReleased);

    /// <summary>Cards that say how the file is SHAPED rather than what it records. They differ for
    /// structural reasons or not at all, so a difference in one says nothing about curation and
    /// must not veto a link.</summary>
    private static readonly HashSet<string> StructuralCards = new(StringComparer.Ordinal)
    {
        "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3", "NAXIS4",
        "EXTEND", "BZERO", "BSCALE", "END", "COMMENT", "HISTORY", "",
    };

    /// <summary>
    /// Considers one pair. Reads both frames and decides; writes only when
    /// <paramref name="apply"/> is true, and returns the same verdict either way so a dry run
    /// reports exactly what a real run would do.
    /// </summary>
    /// <param name="rawPath">The frame to re-point.</param>
    /// <param name="curatedPath">The frame to point it at.</param>
    /// <param name="headerPolicy">How much header difference to tolerate.</param>
    /// <param name="apply">When false (the default) nothing is written.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<LinkResult> LinkAsync(
        string rawPath,
        string curatedPath,
        HeaderPolicy headerPolicy = HeaderPolicy.CuratedWins,
        bool apply = false,
        CancellationToken cancellationToken = default)
    {
        if (HardLinkProbe.TryGetIdentity(rawPath) is not { } rawId)
        {
            return new LinkResult(rawPath, curatedPath, LinkOutcome.Unreadable, $"{rawPath} cannot be read.", 0);
        }
        if (HardLinkProbe.TryGetIdentity(curatedPath) is not { } curatedId)
        {
            return new LinkResult(rawPath, curatedPath, LinkOutcome.Unreadable, $"{curatedPath} cannot be read.", 0);
        }
        if (rawId.IsSameFileAs(curatedId))
        {
            return new LinkResult(rawPath, curatedPath, LinkOutcome.AlreadyOneFile,
                $"both names already point at {curatedId}.", 0);
        }
        if (rawId.VolumeSerial != curatedId.VolumeSerial)
        {
            return new LinkResult(rawPath, curatedPath, LinkOutcome.DifferentVolume,
                "a hard link cannot cross a volume, so these are two physical copies.", 0);
        }

        // Never trust a ledger at the moment of writing. The digest store is refreshed by hand and
        // was 6 percent stale when this was written, and the cost of being wrong here is a frame
        // replaced by a different frame.
        //
        // And not the data-unit digest a ledger keys on either (StackManifest.DigestData): that hashes
        // the first image's data unit and stops, so it cannot see an extension HDU or trailing bytes,
        // which the link would silently discard. What is compared here is EVERYTHING after the primary
        // header, byte for byte, which is exactly the part of the raw file that stops existing.
        var rawBody = await BodyDigestAsync(rawPath, cancellationToken).ConfigureAwait(false);
        var curatedBody = await BodyDigestAsync(curatedPath, cancellationToken).ConfigureAwait(false);
        if (rawBody is null || curatedBody is null)
        {
            return new LinkResult(rawPath, curatedPath, LinkOutcome.Unreadable,
                "one of the two has no primary header this reader understands.", 0);
        }
        if (!string.Equals(rawBody, curatedBody, StringComparison.Ordinal))
        {
            return new LinkResult(rawPath, curatedPath, LinkOutcome.PayloadDiffers,
                $"everything after the primary header must be byte-identical, and it is not ({rawBody} against " +
                $"{curatedBody}): the pixels, the padding or an extension differ.", 0);
        }

        var veto = await HeaderVetoAsync(rawPath, curatedPath, headerPolicy, cancellationToken).ConfigureAwait(false);
        if (veto is { Length: > 0 })
        {
            return new LinkResult(rawPath, curatedPath, LinkOutcome.HeaderWouldLose, veto, 0);
        }

        var size = new FileInfo(rawPath).Length;
        // A raw frame with more than one name does not release its bytes when this one moves away,
        // so say what is actually reclaimed rather than counting the same extent once per name.
        var released = rawId.LinkCount == 1 ? size : 0;
        if (!apply)
        {
            return new LinkResult(rawPath, curatedPath, LinkOutcome.Linked, "", released);
        }

        try
        {
            HardLinkProbe.RepointTo(rawPath, curatedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LinkResult(rawPath, curatedPath, LinkOutcome.Failed, ex.Message, 0);
        }

        return new LinkResult(rawPath, curatedPath, LinkOutcome.Linked, "", released);
    }

    /// <summary>
    /// Indexes curated frames by the digest of their data unit, so a raw frame's twin is a lookup.
    /// The first path wins where several curated frames hold one payload, which is the same frame
    /// filed twice and not a reason to stop.
    /// </summary>
    public static Dictionary<string, string> IndexByPayload(
        IEnumerable<string> curatedFiles, CancellationToken cancellationToken = default)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in curatedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = StackManifest.DigestData(file);
            if (digest.Length > 0)
            {
                index.TryAdd(digest, file);
            }
        }
        return index;
    }

    /// <summary>
    /// The path the file system itself uses for a tree root: junctions and symbolic links resolved,
    /// short names expanded, falling back to the full path where the platform cannot say. Link names
    /// come back REAL, so a root compared against them (the overlap check, "already linked into the
    /// curated tree") must be real too, or a root given through the curated archive's junction farm
    /// is not recognised as the tree it is.
    /// </summary>
    public static string CanonicalRoot(string path)
        => HardLinkProbe.TryGetFinalPath(path) ?? Path.GetFullPath(path);

    /// <summary>
    /// True when this raw frame already has a name under <paramref name="curatedRoot"/>, so the
    /// sweep has nothing left to do with it.
    ///
    /// <para><b>This is what makes a re-run cheap.</b> A full sweep reads every frame twice to
    /// digest it, which over a terabyte is measured in hours, and a run that is interrupted has to
    /// be started again. Answering "already done" from the directory entries costs no read at all,
    /// so the second run skips everything the first one finished.</para>
    /// </summary>
    public static bool AlreadyLinkedInto(string rawPath, string curatedRoot)
    {
        var links = HardLinkProbe.EnumerateLinks(rawPath);
        if (links.IsDefaultOrEmpty || links.Length < 2)
        {
            return false;
        }

        var prefix = Path.GetFullPath(curatedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var link in links)
        {
            if (Path.GetFullPath(link).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Cards that decide what the data bytes MEAN. Identical bytes read under a different
    /// <c>BZERO</c>, <c>BSCALE</c>, <c>BLANK</c>, <c>BITPIX</c> or axis shape are different pixels,
    /// and no digest of the bytes can see it, so these must be EQUAL under every policy. They are
    /// structural, so the general veto below skips them, which is exactly why they are checked here.
    /// A header edit could change one (the generic <c>tag-card</c> does not refuse structural
    /// keywords), so this is not a theoretical case.
    /// </summary>
    private static bool DecidesPixelMeaning(string keyword)
        => keyword is "BITPIX" or "BZERO" or "BSCALE" or "BLANK"
           || keyword.StartsWith("NAXIS", StringComparison.Ordinal);

    /// <summary>A digest of every byte after the primary header, or null when the header cannot be
    /// read. The primary header is the one thing the sweep lets differ.</summary>
    private static async Task<string?> BodyDigestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            if (await FitsHeaderEditor.ReadPrimaryHeaderAsync(stream, cancellationToken).ConfigureAwait(false)
                is not { } header)
            {
                return null;
            }
            stream.Position = header.Length;
            return ContentDigest.OfStream(stream, stream.Length - header.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The reason to refuse, or an empty string to go ahead.</summary>
    private static async Task<string> HeaderVetoAsync(
        string rawPath, string curatedPath, HeaderPolicy policy, CancellationToken cancellationToken)
    {
        var rawAll = await ReadCardsAsync(rawPath, cancellationToken).ConfigureAwait(false);
        var curatedAll = await ReadCardsAsync(curatedPath, cancellationToken).ConfigureAwait(false);
        if (rawAll is null || curatedAll is null)
        {
            return "a primary header could not be read, so the two cannot be compared.";
        }

        var meaning = rawAll.Keys.Union(curatedAll.Keys)
            .Where(DecidesPixelMeaning)
            .Where(k => !rawAll.TryGetValue(k, out var a)
                     || !curatedAll.TryGetValue(k, out var b)
                     || !string.Equals(a, b, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        if (meaning.Length > 0)
        {
            return $"the two read the same bytes differently ({string.Join(", ", meaning.Select(k => $"{k} " +
                $"{rawAll.GetValueOrDefault(k, "absent")} against {curatedAll.GetValueOrDefault(k, "absent")}"))}), " +
                "so they are not the same pixels.";
        }

        var raw = rawAll.Where(kv => !StructuralCards.Contains(kv.Key)).ToDictionary(StringComparer.Ordinal);
        var curated = curatedAll.Where(kv => !StructuralCards.Contains(kv.Key)).ToDictionary(StringComparer.Ordinal);

        var lost = raw.Keys.Where(k => !curated.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        if (lost.Length > 0)
        {
            return $"the raw frame states {string.Join(", ", lost)} and the curated one does not, "
                 + "so linking would discard it.";
        }

        if (policy is HeaderPolicy.RequireIdentical)
        {
            // Every difference, in BOTH directions. Walking only the raw frame's keywords misses
            // the common case entirely, which is the curated header ADDING a card: that is a
            // difference, and this policy exists for a caller who wants none.
            var differing = raw.Keys.Union(curated.Keys)
                .Where(k => !raw.TryGetValue(k, out var a)
                         || !curated.TryGetValue(k, out var b)
                         || !string.Equals(a, b, StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal).ToArray();
            if (differing.Length > 0)
            {
                return $"the two disagree on {string.Join(", ", differing)} and identical headers were required.";
            }
        }

        return "";
    }

    /// <summary>Primary-header cards as keyword to value, only the ones with no value at all
    /// (<c>COMMENT</c>, <c>HISTORY</c>, <c>END</c>, blank) dropped. Null when the file has no header
    /// this reader understands.</summary>
    private static async Task<Dictionary<string, string>?> ReadCardsAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            if (await FitsHeaderEditor.ReadPrimaryHeaderAsync(stream, cancellationToken).ConfigureAwait(false)
                is not { } header)
            {
                return null;
            }

            var cards = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var card in header.Cards)
            {
                var keyword = card.Length >= 8 ? card[..8].Trim() : card.Trim();
                if (keyword is "COMMENT" or "HISTORY" or "END" or "")
                {
                    continue;
                }
                // The value is everything after "= ", up to an unquoted comment. A quoted value can
                // contain a slash, which is why this is not just IndexOf('/').
                cards[keyword] = ValueOf(card);
            }
            return cards;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The value field of one card, comment stripped, quotes kept so a string value and a
    /// numeric one that print the same are not treated as equal.</summary>
    private static string ValueOf(string card)
    {
        if (card.Length < 10 || card[8] != '=')
        {
            return "";
        }

        var span = card.AsSpan(9);
        var inQuotes = false;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == '\'')
            {
                inQuotes = !inQuotes;
            }
            else if (span[i] == '/' && !inQuotes)
            {
                return span[..i].Trim().ToString();
            }
        }
        return span.Trim().ToString();
    }
}
