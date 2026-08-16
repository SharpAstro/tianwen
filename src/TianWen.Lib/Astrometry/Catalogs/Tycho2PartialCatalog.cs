using System;
using System.Buffers.Binary;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Assembles a Tycho-2 catalog buffer from individually-fetched members, so a client can hold the
/// sky it has looked at and nothing else. The buffer is always full length and always has the real
/// offset table, so it is a valid input to
/// <see cref="ICelestialObjectDB.TryLoadTycho2BulkFromDecoded"/> from the moment the header lands.
///
/// <para><b>Absent regions are filled with <c>0xFF</c>, which is not an arbitrary choice: it is the
/// catalog's own "no data" sentinel and it makes every existing consumer skip them with no code
/// change at all.</b> A record's VT byte of <c>0xFF</c> already means "no photometry", which
/// <see cref="ICelestialObjectDB.CopyTycho2Stars"/> turns into a <c>NaN</c> magnitude and the star
/// flatten already filters on. The same fill makes a record's RA/Dec read back as <c>NaN</c> (all
/// bits set is a float NaN), so <c>Tycho2RaDecIndex</c>'s coordinate filters reject them too, and
/// its <c>(tyc2, tyc3)</c> binary search simply fails to find anything. Zero-fill would have been
/// the disaster: VT 0 decodes to magnitude -2.0, so every unfetched region would paint a sky full of
/// impossibly bright stars at RA 0h, Dec 0.</para>
///
/// <para><b>Mutated in place, deliberately.</b> The DB holds a reference to
/// <see cref="Buffer"/>, so a member accepted after the catalog was wired is visible immediately --
/// no re-wire, no second array, and the star count (derived from the offset table, which never
/// changes) stays correct throughout.</para>
/// </summary>
public sealed class Tycho2PartialCatalog
{
    /// <summary>The catalog's own no-data byte; see the class remarks for why the fill is this and
    /// not zero.</summary>
    private const byte Absent = 0xFF;

    private readonly Tycho2MemberManifest _manifest;
    private readonly bool[] _present;

    /// <summary>Raw byte offset each member starts at, resolved from the offset table once the
    /// header arrives. Null until then, which is what <see cref="HeaderLoaded"/> reports.</summary>
    private int[]? _memberStart;

    /// <summary>The assembled catalog. Safe to hand to the DB as soon as
    /// <see cref="HeaderLoaded"/> is true; members accepted later appear without re-wiring.</summary>
    public byte[] Buffer { get; }

    /// <summary>Whether member 0 (the offset table) has been accepted. Nothing else can be placed
    /// before it, because a member's byte offset is only knowable from the offset table.</summary>
    public bool HeaderLoaded => _memberStart is not null;

    public int MembersPresent { get; private set; }

    /// <summary>
    /// Records held, i.e. an upper bound on how many stars a flatten of this buffer can emit (some
    /// records carry no VT and are dropped). It is what a caller must size a vertex buffer by:
    /// <c>ICelestialObjectDB.Tycho2StarCount</c> still reports the WHOLE catalog, because the offset
    /// table does, so sizing by that allocates ~51 MB to hold a couple of megabytes of stars --
    /// every time a member lands.
    /// </summary>
    public int PresentRecordCount { get; private set; }

    public int MemberCount => _manifest.MemberCount;

    public Tycho2PartialCatalog(Tycho2MemberManifest manifest)
    {
        _manifest = manifest;
        _present = new bool[manifest.MemberCount];
        Buffer = GC.AllocateUninitializedArray<byte>(manifest.RawLength);
        Buffer.AsSpan().Fill(Absent);
    }

    public bool IsPresent(int member) => (uint)member < (uint)_present.Length && _present[member];

    /// <summary>
    /// Places a decoded member into the buffer. Member 0 must arrive first; it carries the offset
    /// table every other member's position is derived from.
    /// </summary>
    /// <param name="member">Member index, matching the manifest.</param>
    /// <param name="decoded">The member's decompressed bytes.</param>
    /// <returns>False when the member cannot be placed (out of range, header not yet loaded, or a
    /// length that disagrees with the offset table) -- a caller should treat that as a failed fetch
    /// rather than a fatal error, since the sky simply stays as it was.</returns>
    public bool Accept(int member, ReadOnlySpan<byte> decoded)
    {
        if ((uint)member >= (uint)_present.Length)
        {
            return false;
        }

        if (member == 0)
        {
            // The header defines where everything else goes, so it is placed by its own length and
            // the member starts are resolved from it immediately.
            if (decoded.Length > Buffer.Length)
            {
                return false;
            }

            decoded.CopyTo(Buffer);
            _memberStart = ResolveMemberStarts(decoded, decoded.Length);
            if (_memberStart is null)
            {
                return false;
            }
        }
        else
        {
            if (_memberStart is not { } starts)
            {
                return false;
            }

            var start = starts[member];
            var end = member + 1 < starts.Length ? starts[member + 1] : Buffer.Length;
            if (decoded.Length != end - start)
            {
                // A length mismatch means the manifest and the member files disagree -- a stale
                // cached asset beside a fresh manifest, most likely. Refuse rather than write a
                // shifted copy, which would place real stars at other stars' coordinates.
                return false;
            }

            decoded.CopyTo(Buffer.AsSpan(start));
        }

        if (!_present[member])
        {
            _present[member] = true;
            MembersPresent++;
            // Member 0 is the offset table, not records, so it contributes no stars.
            if (member > 0)
            {
                PresentRecordCount += decoded.Length / Tycho2RegionSelector.BytesPerStar;
            }
        }
        return true;
    }

    /// <summary>
    /// Byte offset of each member, from the region boundaries in the manifest and the region offsets
    /// in the header. Returns null if the header does not describe the catalog the manifest does,
    /// which is the one cross-check available before any records are trusted.
    /// </summary>
    private int[]? ResolveMemberStarts(ReadOnlySpan<byte> header, int headerLength)
    {
        if (header.Length < 4)
        {
            return null;
        }

        var regionCount = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (regionCount != _manifest.RegionCount || header.Length < 4 + (regionCount * 4))
        {
            return null;
        }

        var starts = new int[_manifest.MemberCount];
        starts[0] = 0;
        for (var member = 1; member < starts.Length; member++)
        {
            var firstRegion = _manifest.RegionBoundary(member);
            starts[member] = firstRegion < regionCount
                ? BinaryPrimitives.ReadInt32LittleEndian(header[((firstRegion + 1) * 4)..])
                : Buffer.Length;
        }

        // Member 1 begins exactly where the header ends; if it does not, the manifest was baked
        // against a different catalog than the header just delivered.
        if (starts.Length > 1 && starts[1] != headerLength)
        {
            return null;
        }

        return starts;
    }
}
