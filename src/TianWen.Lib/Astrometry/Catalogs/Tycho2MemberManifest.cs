using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using SharpAstro.Lzip;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// The framing of a region-aligned multi-member Tycho-2 bake: how many members there are, which GSC
/// regions each one holds, and how to name its file. Written by <c>tools/bake-tycho2</c>, read by any
/// client that wants a subset of the catalog.
///
/// <para><b>Why this has to exist at all.</b> lzip members are only enumerable by walking BACKWARDS
/// from the end of the file -- <c>LzipDecoder.FindMembers</c> reads each trailer's <c>member_size</c>
/// and steps back -- so a client holding the head of the catalog cannot find a single member
/// boundary. The framing is therefore not derivable and must be shipped.</para>
///
/// <para><b>It is framing, NOT a spatial index.</b> The sky-to-region question is answered by the GSC
/// bounds table, which is already embedded in the assembly and needs no fetch
/// (<see cref="Tycho2RegionSelector"/>). This only maps a region number onto the member that holds
/// it, which is the one fact the compression framing hides.</para>
///
/// <para><b>Member 0 is the catalog header</b> (the <c>streamCount</c> + per-region offset table) and
/// holds no regions, so its boundary range is empty. A client fetches it once: without the offset
/// table a record's <c>tyc1</c> is unknowable, since a record carries only <c>tyc2</c>/<c>tyc3</c> and
/// takes its <c>tyc1</c> from which region it sits in.</para>
///
/// <para><b>It names each member's CONTENT, as the CRC32 of its uncompressed bytes</b> (version 2), the
/// value the member's own lzip trailer records. A client caches what it has decoded, and the catalog is
/// re-baked under it: on 2026-09-25 a re-bake removed 254 duplicate records, the browser's cache key did
/// not change, and 151 of 166 members came back from that cache at their old lengths, each refused and
/// none fetched again, so most of the sky stayed empty. A cache keyed by the CRC can never return an
/// earlier bake's member, and a re-bake costs a client exactly the members it changed.</para>
/// </summary>
public sealed class Tycho2MemberManifest
{
    /// <summary>"TY2M", so a stray file is identifiable by eye in a hex dump.</summary>
    private static ReadOnlySpan<byte> Magic => "TY2M"u8;

    private const int CurrentVersion = 2;

    /// <summary>Fixed-size prologue: magic, version, memberCount, regionCount, rawLength.</summary>
    private const int PrologueBytes = 4 + (4 * 4);

    /// <summary>Region index at which each member starts, length <c>MemberCount + 1</c>; member
    /// <c>i</c> holds regions <c>[this[i], this[i + 1])</c>. Stored as boundaries rather than as a
    /// first-region per member so the last member's extent is explicit instead of implied.</summary>
    private readonly int[] _regionBoundary;

    /// <summary>CRC32 of each member's uncompressed bytes, length <c>MemberCount</c>.</summary>
    private readonly uint[] _memberCrc;

    public int MemberCount => _regionBoundary.Length - 1;

    /// <summary>Total GSC regions in the catalog (9537), i.e. the last boundary.</summary>
    public int RegionCount { get; }

    /// <summary>Decompressed length of the whole catalog, for pre-allocation.</summary>
    public int RawLength { get; }

    private Tycho2MemberManifest(int[] regionBoundary, uint[] memberCrc, int regionCount, int rawLength)
    {
        _regionBoundary = regionBoundary;
        _memberCrc = memberCrc;
        RegionCount = regionCount;
        RawLength = rawLength;
    }

    /// <summary>First region held by member <paramref name="member"/> (== the end of the previous).</summary>
    public int RegionBoundary(int member) => _regionBoundary[member];

    /// <summary>
    /// CRC32 of member <paramref name="member"/>'s uncompressed bytes: its content identity, which a
    /// client keys whatever it keeps of the member by, and checks a download against
    /// (<see cref="LzipMemberCrc"/>).
    /// </summary>
    public uint MemberCrc(int member) => _memberCrc[member];

    /// <summary>
    /// The CRC32 of the uncompressed data an lzip member carries, read from the member's own trailer
    /// (CRC32, then data size and member size, all little-endian, in its last 20 bytes). What the bake
    /// writes into the manifest and what a client compares a downloaded member with, so the two read it
    /// the same way.
    /// </summary>
    public static uint LzipMemberCrc(ReadOnlySpan<byte> lzipMember)
    {
        const int TrailerBytes = 20;
        if (lzipMember.Length < TrailerBytes)
        {
            throw new ArgumentException($"{lzipMember.Length} bytes is shorter than an lzip trailer.", nameof(lzipMember));
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(lzipMember[^TrailerBytes..]);
    }

    /// <summary>
    /// The file name a member is published under. Zero-padded so a directory listing sorts in member
    /// order, which is also catalog order.
    /// </summary>
    public static string MemberFileName(int member)
        => string.Create(CultureInfo.InvariantCulture, $"m{member:0000}.lz");

    public static Tycho2MemberManifest Create(
        IReadOnlyList<int> regionBoundary, IReadOnlyList<uint> memberCrc, int regionCount, int rawLength)
    {
        if (memberCrc.Count != regionBoundary.Count - 1)
        {
            throw new ArgumentException(
                $"{memberCrc.Count} member CRCs for {regionBoundary.Count - 1} members.", nameof(memberCrc));
        }

        return new([.. regionBoundary], [.. memberCrc], regionCount, rawLength);
    }

    /// <summary>
    /// Appends the members holding <paramref name="regions"/>, ascending and de-duplicated. Member 0
    /// (the header) is NOT included -- a client needs it unconditionally, so making it fall out of a
    /// region query would only make it possible to forget.
    /// </summary>
    /// <param name="regions">Ascending region indices, as produced by
    /// <see cref="Tycho2RegionSelector.SelectVisible"/>.</param>
    public void MembersForRegions(IReadOnlyList<int> regions, List<int> into)
    {
        var previous = -1;
        foreach (var region in regions)
        {
            var member = MemberForRegion(region);
            if (member != previous)
            {
                into.Add(member);
                previous = member;
            }
        }
    }

    /// <summary>
    /// The member holding a region, by binary search over the boundaries. Returns -1 for a region
    /// outside the catalog rather than clamping, so a caller cannot silently fetch the wrong sky.
    /// </summary>
    public int MemberForRegion(int region)
    {
        if (region < 0 || region >= RegionCount)
        {
            return -1;
        }

        // Upper bound: the last member whose first region is <= this one.
        var lo = 0;
        var hi = MemberCount - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) >> 1);
            if (_regionBoundary[mid] <= region) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public byte[] Write()
    {
        var bytes = new byte[PrologueBytes + (_regionBoundary.Length * 4) + (_memberCrc.Length * 4)];
        var span = bytes.AsSpan();

        Magic.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], CurrentVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], MemberCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], RegionCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], RawLength);

        for (var i = 0; i < _regionBoundary.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[(PrologueBytes + (i * 4))..], _regionBoundary[i]);
        }

        var crcStart = PrologueBytes + (_regionBoundary.Length * 4);
        for (var i = 0; i < _memberCrc.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[(crcStart + (i * 4))..], _memberCrc[i]);
        }

        return bytes;
    }

    /// <summary>
    /// Parses a manifest, throwing on anything it does not recognise. Deliberately strict: this is
    /// fetched over the network beside assets that are cached for a year, so a stale or truncated
    /// manifest must fail loudly rather than produce a plausible subset of the sky.
    /// </summary>
    public static Tycho2MemberManifest Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < PrologueBytes || !data[..4].SequenceEqual(Magic))
        {
            throw new InvalidOperationException("Not a Tycho-2 member manifest.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (version != CurrentVersion)
        {
            throw new InvalidOperationException($"Unsupported Tycho-2 manifest version {version}.");
        }

        var memberCount = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        var regionCount = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        var rawLength = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);

        var expected = PrologueBytes + ((memberCount + 1) * 4) + (memberCount * 4);
        if (memberCount <= 0 || data.Length < expected)
        {
            throw new InvalidOperationException(
                $"Truncated Tycho-2 manifest: {memberCount} members needs {expected} bytes, got {data.Length}.");
        }

        var boundary = new int[memberCount + 1];
        for (var i = 0; i < boundary.Length; i++)
        {
            boundary[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(PrologueBytes + (i * 4))..]);
        }

        var crcStart = PrologueBytes + (boundary.Length * 4);
        var crc = new uint[memberCount];
        for (var i = 0; i < crc.Length; i++)
        {
            crc[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(crcStart + (i * 4))..]);
        }

        return new Tycho2MemberManifest(boundary, crc, regionCount, rawLength);
    }

    /// <summary>
    /// Cuts a decompressed catalog into region-aligned lzip members (<see cref="Pack"/>) and the manifest
    /// that frames them and names each one's content. <c>tools/bake-tycho2</c> writes the result out; it
    /// lives here so a test bakes exactly the way the published assets are baked.
    /// </summary>
    /// <param name="raw">The whole decompressed catalog: <c>streamCount</c>, the region offset table, the
    /// records.</param>
    /// <param name="targetBytes">Approximate uncompressed bytes per member.</param>
    public static (byte[][] Members, Tycho2MemberManifest Manifest) Bake(byte[] raw, int targetBytes)
    {
        var regionCount = BinaryPrimitives.ReadInt32LittleEndian(raw);
        var (byteBoundary, regionBoundary) = Pack(raw.AsSpan(0, 4 + (regionCount * 4)), regionCount, raw.Length, targetBytes);
        var memberCount = byteBoundary.Length - 1;

        var members = new byte[memberCount][];
        Parallel.For(0, memberCount, i =>
        {
            members[i] = LzipEncoder.Compress(raw.AsSpan(byteBoundary[i], byteBoundary[i + 1] - byteBoundary[i]));
        });

        var crc = new uint[memberCount];
        for (var i = 0; i < memberCount; i++)
        {
            crc[i] = LzipMemberCrc(members[i]);
        }

        return (members, Create(regionBoundary, crc, regionCount, raw.Length));
    }

    /// <summary>
    /// Greedy region-aligned packing: member 0 is the header, then accumulate whole regions until a
    /// member reaches <paramref name="targetBytes"/> and cut.
    ///
    /// <para><b>Never cuts inside a region</b>, which is the property that makes members addressable
    /// at all -- a member that split one would put half a region behind a boundary no client can ask
    /// for half of. So the target is a floor the packer rounds up to the next region edge, NOT the
    /// fixed stride <c>LzipOptions.MemberSize</c> would give.</para>
    /// </summary>
    /// <param name="header">The catalog's leading bytes: <c>streamCount</c> then one int32 start
    /// offset per region.</param>
    /// <param name="regionCount">Region count, i.e. <c>streamCount</c>.</param>
    /// <param name="rawLength">Length of the whole decompressed catalog.</param>
    /// <param name="targetBytes">Approximate uncompressed bytes per member.</param>
    /// <returns>Raw byte boundaries, length <c>memberCount + 1</c>, starting at 0 and ending at
    /// <paramref name="rawLength"/>; paired with the region boundaries for the manifest.</returns>
    public static (int[] ByteBoundary, int[] RegionBoundary) Pack(
        ReadOnlySpan<byte> header, int regionCount, int rawLength, int targetBytes)
    {
        var bytes = new List<int> { 0 };
        var regions = new List<int> { 0 };

        // Member 0 is the header: it ends where the first region begins and holds no regions.
        var headerEnd = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        bytes.Add(headerEnd);
        regions.Add(0);

        var start = headerEnd;
        for (var region = 0; region < regionCount; region++)
        {
            var end = region + 1 < regionCount
                ? BinaryPrimitives.ReadInt32LittleEndian(header[((region + 2) * 4)..])
                : rawLength;

            if (end - start >= targetBytes)
            {
                bytes.Add(end);
                regions.Add(region + 1);
                start = end;
            }
        }

        // Whatever is left over is the final member; if the last cut landed exactly on the end there
        // is nothing to add, which is why this is conditional rather than unconditional.
        if (start < rawLength)
        {
            bytes.Add(rawLength);
            regions.Add(regionCount);
        }

        return ([.. bytes], [.. regions]);
    }
}
