using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Snapshot of the state mutated by <see cref="CelestialObjectDB.BuildHdHipCrossIndicesViaTyc"/>.
///
/// <para>Captured at build time by <c>tools/precompute-hd-hip-cross</c> after a full live
/// init, then embedded as <c>hd_hip_cross.bin.gz</c>. At runtime, <see cref="CelestialObjectDB"/>
/// hash-verifies the snapshot against the embedded catalog inputs; on hit it applies the
/// snapshot directly (~30-50 ms) and skips the ~280 ms parallel scan + bulk-merge that
/// produced the same end state. On hash miss the runtime falls back to the live path with
/// a warning. See <c>PLAN-catalog-binary-format.md</c> § 2A.</para>
///
/// <para>Determinism note: stored edge entries are the final post-<see cref="CelestialObjectDB.MergeEdgesBulk"/>
/// <c>(i1, ext[])</c> tuple for each affected key, NOT the pre-merge edge delta. This sidesteps
/// HashSet-enumeration-order coupling — the apply path just dict-overwrites with the captured
/// tuple instead of re-running the merge. Pre-existing values for the same key are correctly
/// overwritten because the snapshot's tuple was computed at build time using the same
/// pre-existing dict state that the runtime will reach (deterministic SIMBAD merge).</para>
/// </summary>
internal sealed record HdHipCrossSnapshot(
    ImmutableArray<HdEntrySnapshot> HdEntries,
    ImmutableArray<EdgeSnapshot> Edges)
{
    public const uint SchemaVersion = 1;

    /// <summary>
    /// Bumped whenever the algorithm in <see cref="CelestialObjectDB.BuildHdHipCrossIndicesViaTyc"/>
    /// changes in a way that would alter the snapshot (e.g. new fields on <see cref="CelestialObject"/>,
    /// changed inheritance rules in <see cref="CelestialObjectDB.GetInheritedObjectType"/>). Mixed
    /// into the input hash so old snapshots invalidate even if the embedded resources are byte-identical.
    /// </summary>
    public const uint AlgorithmVersion = 1;
}

internal readonly record struct HdEntrySnapshot(
    CatalogIndex Index,
    ObjectType ObjType,
    Constellation Constellation,
    double Ra,
    double Dec,
    Half VMag,
    Half BvColor);

internal readonly record struct EdgeSnapshot(
    CatalogIndex Key,
    CatalogIndex V1,
    ImmutableArray<CatalogIndex> Ext);

internal static class HdHipCrossSnapshotIo
{
    // "TWHDHIP1" — fixed 8-byte magic. Bumping the schema increments the trailing digit.
    private static ReadOnlySpan<byte> Magic => "TWHDHIP1"u8;

    private const int MagicLength = 8;
    private const int HashLength = 32;
    private const int HeaderLength = MagicLength + 4 /*schema*/ + HashLength + 4 /*hdCount*/ + 4 /*edgeCount*/;

    // HD record on disk (no padding): index + objType + constellation + ra + dec + vMag + bvColor
    private const int HdRecordSize = 8 + 8 + 8 + 8 + 8 + 2 + 2;

    /// <summary>
    /// Writes the snapshot in the format documented on <see cref="HdHipCrossSnapshot"/>, gzip-compressed.
    /// Caller-provided <paramref name="inputHash"/> must be the SHA-256 over all catalog inputs that
    /// affect the snapshot output (see <see cref="HdHipCrossInputHasher"/>).
    /// </summary>
    public static void Write(Stream output, ReadOnlySpan<byte> inputHash, HdHipCrossSnapshot snapshot)
    {
        if (inputHash.Length != HashLength)
        {
            throw new ArgumentException($"Input hash must be {HashLength} bytes (SHA-256), got {inputHash.Length}.", nameof(inputHash));
        }

        using var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header[..MagicLength]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[MagicLength..], HdHipCrossSnapshot.SchemaVersion);
        inputHash.CopyTo(header[(MagicLength + 4)..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(MagicLength + 4 + HashLength)..], (uint)snapshot.HdEntries.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(MagicLength + 4 + HashLength + 4)..], (uint)snapshot.Edges.Length);
        gz.Write(header);

        Span<byte> hdBuf = stackalloc byte[HdRecordSize];
        foreach (var hd in snapshot.HdEntries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(hdBuf, (ulong)hd.Index);
            BinaryPrimitives.WriteUInt64LittleEndian(hdBuf[8..], (ulong)hd.ObjType);
            BinaryPrimitives.WriteUInt64LittleEndian(hdBuf[16..], (ulong)hd.Constellation);
            BinaryPrimitives.WriteDoubleLittleEndian(hdBuf[24..], hd.Ra);
            BinaryPrimitives.WriteDoubleLittleEndian(hdBuf[32..], hd.Dec);
            BinaryPrimitives.WriteHalfLittleEndian(hdBuf[40..], hd.VMag);
            BinaryPrimitives.WriteHalfLittleEndian(hdBuf[42..], hd.BvColor);
            gz.Write(hdBuf);
        }

        // Edge entries are variable-width: [key u64][v1 u64][extCount u32][ext u64 * extCount].
        // 20 bytes header + 8 * extCount payload — write header to stack, payload via a pooled or
        // small heap buffer keyed off extCount. Most ext arrays are 1-3 entries so the payload fits
        // in a small stackalloc; cap the stack size to keep frames small.
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
    }

    /// <summary>
    /// Reads a snapshot stream and returns the decoded payload alongside the embedded
    /// <paramref name="storedInputHash"/>. The caller is responsible for hash-verifying.
    /// Throws on malformed input (wrong magic, schema mismatch, truncated payload).
    /// </summary>
    public static HdHipCrossSnapshot Read(Stream gzipped, out byte[] storedInputHash)
    {
        using var gz = new GZipStream(gzipped, CompressionMode.Decompress, leaveOpen: true);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        if (!ms.TryGetBuffer(out var seg))
        {
            // GetBuffer fallback — MemoryStream-from-default-ctor is always exposable, but be defensive.
            seg = new ArraySegment<byte>(ms.ToArray());
        }
        return ReadFromSpan(seg.AsSpan(), out storedInputHash);
    }

    public static HdHipCrossSnapshot ReadFromSpan(ReadOnlySpan<byte> raw, out byte[] storedInputHash)
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
        if (schema != HdHipCrossSnapshot.SchemaVersion)
        {
            throw new InvalidDataException($"Snapshot schema {schema} != expected {HdHipCrossSnapshot.SchemaVersion}.");
        }
        storedInputHash = raw.Slice(MagicLength + 4, HashLength).ToArray();
        var hdCount = BinaryPrimitives.ReadUInt32LittleEndian(raw[(MagicLength + 4 + HashLength)..]);
        var edgeCount = BinaryPrimitives.ReadUInt32LittleEndian(raw[(MagicLength + 4 + HashLength + 4)..]);

        var rest = raw[HeaderLength..];
        var hdBytes = checked((int)hdCount * HdRecordSize);
        if (rest.Length < hdBytes)
        {
            throw new InvalidDataException($"Snapshot truncated in HD section (need {hdBytes}, have {rest.Length}).");
        }

        var hdBuilder = ImmutableArray.CreateBuilder<HdEntrySnapshot>(checked((int)hdCount));
        for (uint i = 0; i < hdCount; i++)
        {
            var rec = rest.Slice((int)i * HdRecordSize, HdRecordSize);
            var index = (CatalogIndex)BinaryPrimitives.ReadUInt64LittleEndian(rec);
            var objType = (ObjectType)BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
            var constellation = (Constellation)BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]);
            var ra = BinaryPrimitives.ReadDoubleLittleEndian(rec[24..]);
            var dec = BinaryPrimitives.ReadDoubleLittleEndian(rec[32..]);
            var vMag = BinaryPrimitives.ReadHalfLittleEndian(rec[40..]);
            var bv = BinaryPrimitives.ReadHalfLittleEndian(rec[42..]);
            hdBuilder.Add(new HdEntrySnapshot(index, objType, constellation, ra, dec, vMag, bv));
        }

        rest = rest[hdBytes..];

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

        if (!rest.IsEmpty)
        {
            throw new InvalidDataException($"Snapshot has {rest.Length} unexpected trailing bytes.");
        }

        return new HdHipCrossSnapshot(hdBuilder.MoveToImmutable(), edgeBuilder.MoveToImmutable());
    }
}

/// <summary>
/// Computes the SHA-256 input hash that gates a <see cref="HdHipCrossSnapshot"/> at runtime.
/// The hash MUST be the same byte sequence whether computed at build time (precompute tool)
/// or at runtime (live load), so it only consumes embedded resource bytes + a hardcoded
/// algorithm version — no timestamps, no paths, no environment.
/// </summary>
internal static class HdHipCrossInputHasher
{
    /// <summary>
    /// Embedded resources whose content fully determines the snapshot output.
    /// Order is fixed (alphabetical) so the hash is reproducible. Append-only — adding
    /// a new resource here without bumping <see cref="HdHipCrossSnapshot.AlgorithmVersion"/>
    /// would silently change the hash for the same content.
    /// </summary>
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
        "hd_to_tyc.bin.lz",
        "hd_to_tyc_multi.json.lz",
        "hip_to_tyc.bin.lz",
        "hip_to_tyc_multi.json.lz",
        "tyc2.bin.lz",
        "tyc2_gsc_bounds.bin.lz",
        "vdB.gs.gz",
    ];

    public static byte[] Compute(System.Reflection.Assembly assembly, IReadOnlyList<string> manifestNames)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Mix in the algorithm version FIRST so two builds with the same inputs but
        // different algorithm versions produce different hashes.
        Span<byte> algoBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(algoBuf, HdHipCrossSnapshot.AlgorithmVersion);
        sha.AppendData(algoBuf);

        var rentedBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var inputSuffix in Inputs)
            {
                // Mix in the resource name (with a length prefix) so renames affect the hash.
                var nameBytes = System.Text.Encoding.UTF8.GetBytes(inputSuffix);
                BinaryPrimitives.WriteUInt32LittleEndian(algoBuf, (uint)nameBytes.Length);
                sha.AppendData(algoBuf);
                sha.AppendData(nameBytes);

                var manifest = FindManifest(manifestNames, inputSuffix)
                    ?? throw new InvalidOperationException($"Embedded resource not found while computing hd-hip-cross input hash: {inputSuffix}");
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
