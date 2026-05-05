using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Snapshot of the state mutated by <see cref="CelestialObjectDB"/>'s SIMBAD merge phase
/// (the for-loop in <c>InitDBCoreAsync</c> that calls <c>MergeSimbadRecords</c> for each
/// of the 14 SIMBAD catalogs). Phase 2B sibling of <see cref="HdHipCrossSnapshot"/>.
///
/// <para>Captured at build time by <c>tools/precompute-simbad-merge</c> after a full live
/// init, then embedded as <c>simbad_merge.bin.gz</c>. At runtime, <see cref="CelestialObjectDB"/>
/// hash-verifies the snapshot against the embedded SIMBAD + NGC catalog inputs; on hit it
/// applies the snapshot and skips both the parse step (~80 ms across 14 prefetched files)
/// and the merge step (~100-150 ms of serial dict mutation). On hash miss the runtime falls
/// back to the live path with a phase-timing entry. See <c>PLAN-catalog-binary-format.md</c> § 2B.</para>
///
/// <para>Determinism note: stored entries are the FINAL post-SIMBAD-merge values for keys
/// that the SIMBAD phase added or modified. The pre-SIMBAD state (predefined objects + NGC
/// merge) is not in the snapshot — it runs live and is fast (~104 ms combined). The apply
/// path dict-overwrites the captured (key, value) pairs onto that pre-existing live state.</para>
///
/// <para>Three dicts are snapshotted independently: <see cref="Objects"/> for
/// <c>_objectsByIndex</c>, <see cref="Edges"/> for <c>_crossIndexLookuptable</c>, and
/// <see cref="NameMappings"/> for <c>_objectsByCommonName</c>. The third is required because
/// SIMBAD adds (commonName -> catIdx) entries via <c>AddCommonNameIndex(catToAddIdx, bestMatchNames)</c>
/// where <c>bestMatchNames</c> belongs to a DIFFERENT object than catToAddIdx — those mappings
/// are not derivable from the per-object CommonNames captured in <see cref="Objects"/> alone.</para>
///
/// <para>The <see cref="EdgeSnapshot"/> record is shared with <see cref="HdHipCrossSnapshot"/>:
/// both phases produce <c>(key, v1, ext[])</c> entries for <see cref="CelestialObjectDB"/>'s
/// <c>_crossIndexLookuptable</c>, so the on-disk shape is identical.</para>
/// </summary>
internal sealed record SimbadMergeSnapshot(
    ImmutableArray<SimbadObjectSnapshot> Objects,
    ImmutableArray<EdgeSnapshot> Edges,
    ImmutableArray<NameMappingSnapshot> NameMappings)
{
    public const uint SchemaVersion = 1;

    /// <summary>
    /// Bumped whenever SIMBAD merge logic (<c>MergeSimbadRecords</c> / <c>UpdateObjectCommonNames</c>
    /// / <c>PopulateSimbadStarEntries</c>) changes in a way that would alter the snapshot output
    /// for the same input bytes. Mixed into the input hash so old snapshots invalidate even if
    /// the embedded resources are byte-identical.
    /// </summary>
    public const uint AlgorithmVersion = 2;
}

/// <summary>
/// Post-SIMBAD-merge value for one entry in <c>_objectsByIndex</c>. Captures the full
/// <see cref="CelestialObject"/> shape including the common-name set so the apply path
/// can dict-overwrite without touching the live merge code.
/// </summary>
internal readonly record struct SimbadObjectSnapshot(
    CatalogIndex Index,
    ObjectType ObjType,
    Constellation Constellation,
    double Ra,
    double Dec,
    Half VMag,
    Half SurfaceBrightness,
    Half BvColor,
    ImmutableArray<string> CommonNames);

/// <summary>
/// Post-SIMBAD-merge value for one entry in <c>_objectsByCommonName</c>: a common name and
/// the (i1, ext[]) tuple of catalog indices that resolve from it. Same shape as
/// <see cref="EdgeSnapshot"/> but keyed by string. Captures cross-references SIMBAD adds via
/// <c>AddCommonNameIndex(catToAddIdx, bestMatchNames)</c> where catToAddIdx isn't in the
/// bestMatch's own CommonNames set.
/// </summary>
internal readonly record struct NameMappingSnapshot(
    string Name,
    CatalogIndex V1,
    ImmutableArray<CatalogIndex> Ext);

internal static class SimbadMergeSnapshotIo
{
    // "TWSIMRG1" — fixed 8-byte magic. Bumping the schema increments the trailing digit.
    private static ReadOnlySpan<byte> Magic => "TWSIMRG1"u8;

    private const int MagicLength = 8;
    private const int HashLength = 32;
    private const int HeaderLength = MagicLength + 4 /*schema*/ + HashLength + 4 /*objCount*/ + 4 /*edgeCount*/ + 4 /*nameMappingCount*/;

    /// <summary>
    /// Writes the snapshot in a self-describing gzip-compressed format. Caller-provided
    /// <paramref name="inputHash"/> must be the SHA-256 over all SIMBAD/NGC inputs that affect
    /// the snapshot output (see <see cref="SimbadMergeInputHasher"/>).
    /// </summary>
    public static void Write(Stream output, ReadOnlySpan<byte> inputHash, SimbadMergeSnapshot snapshot)
    {
        if (inputHash.Length != HashLength)
        {
            throw new ArgumentException($"Input hash must be {HashLength} bytes (SHA-256), got {inputHash.Length}.", nameof(inputHash));
        }

        using var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header[..MagicLength]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[MagicLength..], SimbadMergeSnapshot.SchemaVersion);
        inputHash.CopyTo(header[(MagicLength + 4)..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(MagicLength + 4 + HashLength)..], (uint)snapshot.Objects.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(MagicLength + 4 + HashLength + 4)..], (uint)snapshot.Edges.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(MagicLength + 4 + HashLength + 4 + 4)..], (uint)snapshot.NameMappings.Length);
        gz.Write(header);

        // Fixed-prefix object record: index/objType/constellation u64x3, ra/dec f64x2,
        // vMag/surfaceBrightness/bvColor Halfx3 + commonNames count u16 = 48 bytes.
        // Followed by length-prefixed UTF-8 names.
        const int ObjFixedLen = 8 + 8 + 8 + 8 + 8 + 2 + 2 + 2 + 2;
        Span<byte> objHeader = stackalloc byte[ObjFixedLen];
        Span<byte> nameLenBuf = stackalloc byte[2];
        foreach (var obj in snapshot.Objects)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(objHeader, (ulong)obj.Index);
            BinaryPrimitives.WriteUInt64LittleEndian(objHeader[8..], (ulong)obj.ObjType);
            BinaryPrimitives.WriteUInt64LittleEndian(objHeader[16..], (ulong)obj.Constellation);
            BinaryPrimitives.WriteDoubleLittleEndian(objHeader[24..], obj.Ra);
            BinaryPrimitives.WriteDoubleLittleEndian(objHeader[32..], obj.Dec);
            BinaryPrimitives.WriteHalfLittleEndian(objHeader[40..], obj.VMag);
            BinaryPrimitives.WriteHalfLittleEndian(objHeader[42..], obj.SurfaceBrightness);
            BinaryPrimitives.WriteHalfLittleEndian(objHeader[44..], obj.BvColor);
            // u16 commonNames count, written here so we can stream names without a second pass.
            BinaryPrimitives.WriteUInt16LittleEndian(objHeader[46..], (ushort)obj.CommonNames.Length);
            gz.Write(objHeader);

            foreach (var name in obj.CommonNames)
            {
                var nameBytes = Encoding.UTF8.GetBytes(name);
                if (nameBytes.Length > ushort.MaxValue)
                {
                    throw new InvalidDataException($"Common name '{name}' exceeds u16 length cap.");
                }
                BinaryPrimitives.WriteUInt16LittleEndian(nameLenBuf, (ushort)nameBytes.Length);
                gz.Write(nameLenBuf);
                gz.Write(nameBytes);
            }
        }

        // Edge entries share shape with HdHipCrossSnapshot: [key u64][v1 u64][extCount u32][ext u64 * extCount].
        Span<byte> edgeHeader = stackalloc byte[20];
        Span<byte> smallExt = stackalloc byte[8 * 16];
        foreach (var edge in snapshot.Edges)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(edgeHeader, (ulong)edge.Key);
            BinaryPrimitives.WriteUInt64LittleEndian(edgeHeader[8..], (ulong)edge.V1);
            BinaryPrimitives.WriteUInt32LittleEndian(edgeHeader[16..], (uint)edge.Ext.Length);
            gz.Write(edgeHeader);

            if (edge.Ext.Length == 0)
            {
                continue;
            }

            Span<byte> payload = edge.Ext.Length <= 16
                ? smallExt[..(8 * edge.Ext.Length)]
                : new byte[8 * edge.Ext.Length];
            for (int i = 0; i < edge.Ext.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(payload[(i * 8)..], (ulong)edge.Ext[i]);
            }
            gz.Write(payload);
        }

        // Name-mapping entries: [nameLen u16][name UTF-8 bytes][v1 u64][extCount u32][ext u64 * extCount].
        // Same v1/ext shape as EdgeSnapshot, just keyed on a length-prefixed string instead of u64.
        Span<byte> nmHeader = stackalloc byte[2];
        Span<byte> nmTail = stackalloc byte[12];
        foreach (var nm in snapshot.NameMappings)
        {
            var nameBytes = Encoding.UTF8.GetBytes(nm.Name);
            if (nameBytes.Length > ushort.MaxValue)
            {
                throw new InvalidDataException($"Common name '{nm.Name}' exceeds u16 length cap.");
            }
            BinaryPrimitives.WriteUInt16LittleEndian(nmHeader, (ushort)nameBytes.Length);
            gz.Write(nmHeader);
            gz.Write(nameBytes);

            BinaryPrimitives.WriteUInt64LittleEndian(nmTail, (ulong)nm.V1);
            BinaryPrimitives.WriteUInt32LittleEndian(nmTail[8..], (uint)nm.Ext.Length);
            gz.Write(nmTail);

            if (nm.Ext.Length == 0)
            {
                continue;
            }
            Span<byte> payload = nm.Ext.Length <= 16
                ? smallExt[..(8 * nm.Ext.Length)]
                : new byte[8 * nm.Ext.Length];
            for (int i = 0; i < nm.Ext.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(payload[(i * 8)..], (ulong)nm.Ext[i]);
            }
            gz.Write(payload);
        }
    }

    /// <summary>
    /// Reads a snapshot stream and returns the decoded payload alongside the embedded
    /// <paramref name="storedInputHash"/>. The caller is responsible for hash-verifying.
    /// Throws on malformed input (wrong magic, schema mismatch, truncated payload).
    /// </summary>
    public static SimbadMergeSnapshot Read(Stream gzipped, out byte[] storedInputHash)
    {
        using var gz = new GZipStream(gzipped, CompressionMode.Decompress, leaveOpen: true);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        if (!ms.TryGetBuffer(out var seg))
        {
            seg = new ArraySegment<byte>(ms.ToArray());
        }
        return ReadFromSpan(seg.AsSpan(), out storedInputHash);
    }

    public static SimbadMergeSnapshot ReadFromSpan(ReadOnlySpan<byte> raw, out byte[] storedInputHash)
    {
        if (raw.Length < HeaderLength)
        {
            throw new InvalidDataException($"Snapshot too small ({raw.Length} bytes; minimum {HeaderLength}).");
        }
        if (!raw[..MagicLength].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Snapshot magic mismatch.");
        }
        var schema = BinaryPrimitives.ReadUInt32LittleEndian(raw[MagicLength..]);
        if (schema != SimbadMergeSnapshot.SchemaVersion)
        {
            throw new InvalidDataException($"Snapshot schema {schema} != expected {SimbadMergeSnapshot.SchemaVersion}.");
        }
        storedInputHash = raw.Slice(MagicLength + 4, HashLength).ToArray();
        var objCount = BinaryPrimitives.ReadUInt32LittleEndian(raw[(MagicLength + 4 + HashLength)..]);
        var edgeCount = BinaryPrimitives.ReadUInt32LittleEndian(raw[(MagicLength + 4 + HashLength + 4)..]);
        var nameMappingCount = BinaryPrimitives.ReadUInt32LittleEndian(raw[(MagicLength + 4 + HashLength + 4 + 4)..]);

        var rest = raw[HeaderLength..];

        const int ObjFixedLen = 8 + 8 + 8 + 8 + 8 + 2 + 2 + 2 + 2;
        var objBuilder = ImmutableArray.CreateBuilder<SimbadObjectSnapshot>(checked((int)objCount));
        for (uint i = 0; i < objCount; i++)
        {
            if (rest.Length < ObjFixedLen)
            {
                throw new InvalidDataException("Snapshot truncated in object header.");
            }
            var index = (CatalogIndex)BinaryPrimitives.ReadUInt64LittleEndian(rest);
            var objType = (ObjectType)BinaryPrimitives.ReadUInt64LittleEndian(rest[8..]);
            var constellation = (Constellation)BinaryPrimitives.ReadUInt64LittleEndian(rest[16..]);
            var ra = BinaryPrimitives.ReadDoubleLittleEndian(rest[24..]);
            var dec = BinaryPrimitives.ReadDoubleLittleEndian(rest[32..]);
            var vMag = BinaryPrimitives.ReadHalfLittleEndian(rest[40..]);
            var surfBri = BinaryPrimitives.ReadHalfLittleEndian(rest[42..]);
            var bv = BinaryPrimitives.ReadHalfLittleEndian(rest[44..]);
            var nameCount = BinaryPrimitives.ReadUInt16LittleEndian(rest[46..]);
            rest = rest[ObjFixedLen..];

            ImmutableArray<string> commonNames;
            if (nameCount == 0)
            {
                commonNames = ImmutableArray<string>.Empty;
            }
            else
            {
                var nameBuilder = ImmutableArray.CreateBuilder<string>(nameCount);
                for (var n = 0; n < nameCount; n++)
                {
                    if (rest.Length < 2)
                    {
                        throw new InvalidDataException("Snapshot truncated in name length.");
                    }
                    var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(rest);
                    rest = rest[2..];
                    if (rest.Length < nameLen)
                    {
                        throw new InvalidDataException($"Snapshot truncated in name body (need {nameLen}, have {rest.Length}).");
                    }
                    nameBuilder.Add(Encoding.UTF8.GetString(rest[..nameLen]));
                    rest = rest[nameLen..];
                }
                commonNames = nameBuilder.MoveToImmutable();
            }
            objBuilder.Add(new SimbadObjectSnapshot(index, objType, constellation, ra, dec, vMag, surfBri, bv, commonNames));
        }

        var edgeBuilder = ImmutableArray.CreateBuilder<EdgeSnapshot>(checked((int)edgeCount));
        for (uint i = 0; i < edgeCount; i++)
        {
            if (rest.Length < 20)
            {
                throw new InvalidDataException("Snapshot truncated in edge header.");
            }
            var key = (CatalogIndex)BinaryPrimitives.ReadUInt64LittleEndian(rest);
            var v1 = (CatalogIndex)BinaryPrimitives.ReadUInt64LittleEndian(rest[8..]);
            var extCount = BinaryPrimitives.ReadUInt32LittleEndian(rest[16..]);
            rest = rest[20..];

            var extBytes = checked((int)extCount * 8);
            if (rest.Length < extBytes)
            {
                throw new InvalidDataException($"Snapshot truncated in edge ext (need {extBytes}, have {rest.Length}).");
            }
            ImmutableArray<CatalogIndex> ext;
            if (extCount == 0)
            {
                ext = ImmutableArray<CatalogIndex>.Empty;
            }
            else
            {
                var extArr = new CatalogIndex[extCount];
                for (uint j = 0; j < extCount; j++)
                {
                    extArr[j] = (CatalogIndex)BinaryPrimitives.ReadUInt64LittleEndian(rest[((int)j * 8)..]);
                }
                ext = ImmutableCollectionsMarshal.AsImmutableArray(extArr);
            }
            rest = rest[extBytes..];
            edgeBuilder.Add(new EdgeSnapshot(key, v1, ext));
        }

        var nmBuilder = ImmutableArray.CreateBuilder<NameMappingSnapshot>(checked((int)nameMappingCount));
        for (uint i = 0; i < nameMappingCount; i++)
        {
            if (rest.Length < 2)
            {
                throw new InvalidDataException("Snapshot truncated in name-mapping name length.");
            }
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(rest);
            rest = rest[2..];
            if (rest.Length < nameLen + 12)
            {
                throw new InvalidDataException($"Snapshot truncated in name-mapping body (need {nameLen + 12}, have {rest.Length}).");
            }
            var name = Encoding.UTF8.GetString(rest[..nameLen]);
            rest = rest[nameLen..];
            var v1 = (CatalogIndex)BinaryPrimitives.ReadUInt64LittleEndian(rest);
            var extCount = BinaryPrimitives.ReadUInt32LittleEndian(rest[8..]);
            rest = rest[12..];

            var nmExtBytes = checked((int)extCount * 8);
            if (rest.Length < nmExtBytes)
            {
                throw new InvalidDataException($"Snapshot truncated in name-mapping ext (need {nmExtBytes}, have {rest.Length}).");
            }
            ImmutableArray<CatalogIndex> nmExt;
            if (extCount == 0)
            {
                nmExt = ImmutableArray<CatalogIndex>.Empty;
            }
            else
            {
                var extArr = new CatalogIndex[extCount];
                for (uint j = 0; j < extCount; j++)
                {
                    extArr[j] = (CatalogIndex)BinaryPrimitives.ReadUInt64LittleEndian(rest[((int)j * 8)..]);
                }
                nmExt = ImmutableCollectionsMarshal.AsImmutableArray(extArr);
            }
            rest = rest[nmExtBytes..];
            nmBuilder.Add(new NameMappingSnapshot(name, v1, nmExt));
        }

        if (!rest.IsEmpty)
        {
            throw new InvalidDataException($"Snapshot has {rest.Length} unexpected trailing bytes.");
        }

        return new SimbadMergeSnapshot(objBuilder.MoveToImmutable(), edgeBuilder.MoveToImmutable(), nmBuilder.MoveToImmutable());
    }
}

/// <summary>
/// Computes the SHA-256 input hash that gates a <see cref="SimbadMergeSnapshot"/> at runtime.
/// Reuses the same byte-stream contract as <see cref="HdHipCrossInputHasher"/> (algo version
/// prefix + length-prefixed resource name + raw bytes per input, alphabetical order), but with
/// a narrower input set: only the SIMBAD + NGC <c>.gs.gz</c> files actually consumed by
/// <c>MergeSimbadRecords</c>. Tycho2 / HD-HIP cross-ref binaries don't influence the SIMBAD
/// merge output and are excluded so an unrelated Tycho2 update doesn't needlessly invalidate
/// this snapshot.
/// </summary>
internal static class SimbadMergeInputHasher
{
    private static readonly string[] Inputs =
    [
        "Barnard.gs.gz",
        "CG.gs.gz",
        "Ced.gs.gz",
        "Cl.gs.gz",
        "DG.gs.gz",
        "Dobashi.gs.gz",
        "GUM.gs.gz",
        "HH.gs.gz",
        "HR.gs.gz",
        "LDN.gs.gz",
        "NGC.addendum.gs.gz",
        "NGC.gs.gz",
        "RCW.gs.gz",
        "Sh.gs.gz",
        "vdB.gs.gz",
    ];

    public static byte[] Compute(System.Reflection.Assembly assembly, IReadOnlyList<string> manifestNames)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> intBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(intBuf, SimbadMergeSnapshot.AlgorithmVersion);
        sha.AppendData(intBuf);

        var rentedBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var inputSuffix in Inputs)
            {
                var nameBytes = Encoding.UTF8.GetBytes(inputSuffix);
                BinaryPrimitives.WriteUInt32LittleEndian(intBuf, (uint)nameBytes.Length);
                sha.AppendData(intBuf);
                sha.AppendData(nameBytes);

                var manifest = FindManifest(manifestNames, inputSuffix)
                    ?? throw new InvalidOperationException($"Embedded resource not found while computing simbad-merge input hash: {inputSuffix}");
                using var stream = assembly.GetManifestResourceStream(manifest)
                    ?? throw new InvalidOperationException($"GetManifestResourceStream returned null for {manifest}");

                int read;
                while ((read = stream.Read(rentedBuf, 0, rentedBuf.Length)) > 0)
                {
                    sha.AppendData(rentedBuf, 0, read);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuf);
        }

        return sha.GetHashAndReset();
    }

    private static string? FindManifest(IReadOnlyList<string> manifestNames, string suffix)
    {
        for (var i = 0; i < manifestNames.Count; i++)
        {
            if (manifestNames[i].EndsWith(suffix, StringComparison.Ordinal))
            {
                return manifestNames[i];
            }
        }
        return null;
    }
}
