using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using static TianWen.Lib.Astrometry.Catalogs.CatalogUtils;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Compact per-star record used for bulk enumeration of the Tycho-2 catalog,
/// primarily for GPU sky map rendering. RA in hours, Dec in degrees,
/// <paramref name="VMag"/> is Johnson V, <paramref name="BMinusV"/> is the
/// colour index (~0.65 default for solar-type stars when BT is missing).
/// <para>
/// Proper motions are stored as <see cref="int"/> in units of <c>0.1 mas/yr</c>
/// (= source value times 10) to preserve the Tycho-2 F7.1 source precision
/// losslessly across the full catalog range. Use the
/// <see cref="PmRaMasPerYr"/> / <see cref="PmDecMasPerYr"/> derived properties
/// for convenient float access in mas/yr.
/// </para>
/// <para>
/// A stored value of <c>0</c> conflates two source states:
/// (a) <c>posflg='X'</c> entries where Tycho-2 has no derived proper motion
/// (~4.3% of the catalog) and (b) legitimate zero-pm stars. Both produce
/// no drift under any <c>dt</c> propagation, so no downstream consumer
/// can distinguish or needs to.
/// </para>
/// </summary>
public readonly record struct Tycho2StarLite(
    float RaHours, float DecDeg, float VMag, float BMinusV,
    int PmRaTenthMasPerYr, int PmDecTenthMasPerYr)
{
    /// <summary>
    /// Proper motion in RA*cos(Dec), in mas/yr. Derived from the stored
    /// <see cref="PmRaTenthMasPerYr"/>. Returns <c>0</c> for missing/zero pm.
    /// </summary>
    public float PmRaMasPerYr  => PmRaTenthMasPerYr  * 0.1f;

    /// <summary>
    /// Proper motion in Dec, in mas/yr. Derived from the stored
    /// <see cref="PmDecTenthMasPerYr"/>. Returns <c>0</c> for missing/zero pm.
    /// </summary>
    public float PmDecMasPerYr => PmDecTenthMasPerYr * 0.1f;
}

/// <summary>
/// Per-match record produced by <see cref="ICelestialObjectDB.FindTycho2ByCanonicalPrefix"/>.
/// Carries the raw (tyc1, tyc2, tyc3) triple plus V magnitude. The caller
/// formats the canonical "TYC tyc1-tyc2-tyc3" display string and builds the
/// <see cref="CatalogIndex"/> from the triple via the Base91 round-trip only
/// when materialising a result row -- the CatalogIndex enum value isn't the
/// raw bit layout, it's the ASCII-packed form of the Base91-encoded bytes, so
/// constructing it eagerly during the byte-walk would burn ~2 string
/// allocations per scanned match (most of which never reach the UI when the
/// buffer overflows). Returned in stream-walk order; consumers that need a
/// stable sort post-sort themselves.
/// </summary>
public readonly record struct Tycho2PrefixMatch(ushort Tyc1, ushort Tyc2, byte Tyc3, float VMag);

public interface ICelestialObjectDB
{
    bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> matches);

    bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlySet<CatalogIndex> crossIndices);

    IReadOnlySet<CatalogIndex> AllObjectIndices { get; }

    IReadOnlySet<Catalog> Catalogs { get; }

    /// <summary>
    /// Build all in-memory catalog indices from embedded resources. Idempotent: subsequent
    /// calls return immediately once the first init succeeded.
    /// <para>
    /// The ~21 MB Tycho-2 binary catalog is decompressed in the background and is NOT awaited
    /// before this returns by default. Callers that need it (sky map, plate solver, anything
    /// touching <see cref="CoordinateGrid"/> / <see cref="CopyTycho2Stars"/>) must
    /// <c>await</c> <see cref="EnsureTycho2DataLoadedAsync"/> first. Pass
    /// <paramref name="waitForTycho2BulkLoad"/>=<c>true</c> to make init block until the bulk
    /// Tycho-2 data is ready (precompute tools, tests that probe Tycho-2 state synchronously).
    /// </para>
    /// </summary>
    Task InitDBAsync(bool waitForTycho2BulkLoad = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// True once <see cref="InitDBAsync"/> has completed at least once (the main catalogs are
    /// queryable; the background Tycho-2 bulk load may still be running). Consumers that build
    /// one-shot caches from the DB (e.g. the GPU sky-map geometry) must gate on this so a
    /// render racing the init doesn't latch an empty catalog. Default true: test stubs are
    /// born initialised.
    /// </summary>
    bool IsInitialized => true;

    /// <summary>
    /// Awaits the background Tycho-2 bulk-load task started by <see cref="InitDBAsync"/>.
    /// Cheap to call repeatedly — the underlying decode runs at most once per instance.
    /// </summary>
    Task EnsureTycho2DataLoadedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Number of catalog entries successfully processed in the last <see cref="InitDBAsync"/>.
    /// Diagnostic surface for tooling and tests.
    /// </summary>
    int LastInitProcessed { get; }

    /// <summary>
    /// Number of catalog entries that failed to parse in the last <see cref="InitDBAsync"/>.
    /// </summary>
    int LastInitFailed { get; }

    IReadOnlyCollection<string> CommonNames { get; }

    IRaDecIndex CoordinateGrid { get; }

    /// <summary>
    /// Coordinate grid excluding star catalogs (Tycho-2). Use this for deep-sky object searches
    /// where star entries are not needed, as enumerating the full grid with Tycho-2 is ~235x slower.
    /// </summary>
    IRaDecIndex DeepSkyCoordinateGrid { get; }

    bool TryLookupByIndex(CatalogIndex index, [NotNullWhen(true)] out CelestialObject celestialObject);

    bool TryGetShape(CatalogIndex index, out CelestialObjectShape shape);

    /// <summary>
    /// Fast HIP number → RA/Dec/mag/color lookup via the Tycho-2 cross-reference array.
    /// Avoids string parsing of "HIP nnn" on every call. O(1) array index.
    /// </summary>
    bool TryLookupHIP(int hipNumber, out double ra, out double dec, out float vMag, out float bv);

    /// <summary>
    /// Lightweight HIP → RA/Dec/mag/color for bulk star-dot seeding. Unlike
    /// <see cref="TryLookupHIP"/>, it skips constellation-boundary assignment, cross-reference
    /// magnitude refinement, and object-type inheritance — none of which a plotted star dot
    /// needs, and which dominate the per-star cost at catalogue scale (the sky-map HIP seed).
    /// Default delegates to <see cref="TryLookupHIP"/>; <c>CelestialObjectDB</c> overrides with
    /// a Tycho-2-array fast path.
    /// </summary>
    bool TryGetHipStarLite(int hipNumber, out double ra, out double dec, out float vMag, out float bv)
        => TryLookupHIP(hipNumber, out ra, out dec, out vMag, out bv);

    /// <summary>
    /// Number of entries in the HIP→Tycho-2 cross-reference array.
    /// Iterate 1..HipStarCount with <see cref="TryLookupHIP"/> to access all HIP stars.
    /// </summary>
    int HipStarCount { get; }

    /// <summary>
    /// Total number of stars in the embedded Tycho-2 binary catalog (~2.5M).
    /// Use with <see cref="CopyTycho2Stars"/> to stream every entry for bulk
    /// operations like GPU vertex-buffer population.
    /// </summary>
    int Tycho2StarCount { get; }

    /// <summary>
    /// Copy up to <c>destination.Length</c> Tycho-2 star records into the provided
    /// span, starting from <paramref name="startIndex"/>. Zero per-star allocations.
    /// Pass a span of length <see cref="Tycho2StarCount"/> to capture the full catalog.
    /// </summary>
    /// <param name="destination">Pre-allocated buffer to receive star records.</param>
    /// <param name="startIndex">Global offset into the catalog (0-based). Defaults to 0.</param>
    /// <returns>Number of records written.</returns>
    int CopyTycho2Stars(Span<Tycho2StarLite> destination, int startIndex = 0);

    /// <summary>
    /// Injects an externally-fetched, still-lzip-compressed <c>tyc2.bin.lz</c> payload,
    /// decompressing it and wiring the bulk Tycho-2 star data so <see cref="Tycho2StarCount"/> /
    /// <see cref="CopyTycho2Stars"/> return the full catalog. This is the browser/Lightweight
    /// path: the ~30 MB catalog is stripped from the WASM bundle (the embedded manifest entry is
    /// absent), so the atlas fetches <c>tyc2.bin.lz</c> as a same-origin static asset and feeds
    /// the bytes here. Idempotent (a no-op returning <c>true</c> once bulk data is present) and
    /// deliberately <b>display-only</b>: it builds only the flat star records, NOT the searchable
    /// spatial index or the HD/HIP cross maps (a plotted star dot needs neither). The default is a
    /// no-op for hosts that never inject (the embedded desktop build, tests) - only
    /// <c>CelestialObjectDB</c> performs the decode.
    /// </summary>
    /// <param name="compressedLz">The raw <c>tyc2.bin.lz</c> bytes as fetched over HTTP.</param>
    /// <returns><c>true</c> when the Tycho-2 catalog is available after the call
    /// (freshly injected, or already loaded); <c>false</c> on empty input or a no-op host.</returns>
    bool TryLoadTycho2BulkFromCompressed(byte[] compressedLz) => false;

    /// <summary>
    /// Single-star lookup by Tycho-2 catalog index. One walk through the
    /// catalog byte[] produces RA/Dec/photometry/pm all at once -- use this
    /// in SPCC matching and plate-solving where you need both position and
    /// pm for the propagation step. Bulk enumeration (sky map, MilkyWay
    /// baking) should keep using <see cref="CopyTycho2Stars"/>.
    /// <para>
    /// Returns <c>true</c> with a fully-populated <see cref="Tycho2StarLite"/>
    /// when the star is found. <see cref="Tycho2StarLite.VMag"/> is
    /// <see cref="float.NaN"/> when source VT is missing (~0.04% of entries).
    /// Pm fields are <c>0</c> when the source posflg='X' or pm is exactly
    /// zero (the two cases are indistinguishable -- both yield no drift
    /// under propagation, which is the right downstream behaviour).
    /// </para>
    /// </summary>
    /// <param name="index">Catalog index. Non-Tycho-2 indices return false.</param>
    /// <param name="star">Decoded star record on success.</param>
    /// <returns><c>true</c> when found; <c>false</c> when not a Tycho-2 index
    /// or the Tycho-2 bulk data hasn't loaded yet.</returns>
    bool TryGetTycho2Star(CatalogIndex index, out Tycho2StarLite star)
    {
        // Default impl for test stubs / fakes: no Tycho-2 data. The real
        // CelestialObjectDB overrides with the byte[] decode.
        star = default;
        return false;
    }

    /// <summary>
    /// Virtual prefix-search over the ~2.5M Tycho-2 stars without materialising
    /// "TYC nnnn-nnnn-n" strings into <see cref="CreateAutoCompleteList"/>. Walks
    /// the byte[] sorted by <c>(tyc1, tyc2, tyc3)</c> directly, applying the
    /// numeric-prefix query and stopping at the destination's length. Zero
    /// allocation beyond the destination span itself.
    /// <para>
    /// The <paramref name="query"/> is the part AFTER the literal "TYC" prefix
    /// (caller strips the catalog tag + any leading whitespace). It is parsed
    /// by splitting on <c>-</c>:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>"425"</c> -- tyc1 string-prefix; matches tyc1=425 and tyc1=4250..4259.</description></item>
    /// <item><description><c>"425-"</c> -- tyc1 EXACTLY 425, tyc2 wildcard.</description></item>
    /// <item><description><c>"425-25"</c> -- tyc1 EXACTLY 425, tyc2 string-prefix "25".</description></item>
    /// <item><description><c>"425-2502-1"</c> -- tyc1+tyc2 exact, tyc3 string-prefix "1".</description></item>
    /// </list>
    /// <para>
    /// Within each scanned stream, entries are visited in their stored order
    /// (sorted by <c>(tyc2, tyc3)</c> ascending). Cross-stream order follows
    /// tyc1 ascending, with exact-tyc1 matches always preceding prefix-only
    /// matches so e.g. "425" surfaces tyc1=425 records before tyc1=4250 records.
    /// </para>
    /// </summary>
    /// <param name="query">User text after the "TYC" tag (e.g. "425-2502").</param>
    /// <param name="destination">Pre-allocated buffer; method returns when this fills.</param>
    /// <returns>Number of matches written to <paramref name="destination"/>.</returns>
    int FindTycho2ByCanonicalPrefix(ReadOnlySpan<char> query, Span<Tycho2PrefixMatch> destination)
    {
        // Default impl for test stubs: no Tycho-2 data, no matches.
        return 0;
    }

    public bool TryLookupByIndex(string name, [NotNullWhen(true)] out CelestialObject celestialObject)
    {
        if (TryGetCleanedUpCatalogName(name, out var index) && TryLookupByIndex(index, out celestialObject))
        {
            return true;
        }
        else
        {
            celestialObject = default;
            return false;
        }
    }

    /// <summary>
    /// Uses <see cref="ICelestialObjectDB.CommonNames"/> and <see cref="ICelestialObjectDB.AllObjectIndices"/> to create a list
    /// of all names and designations, **sorted ordinal-ignore-case ascending** so callers
    /// can binary-search the prefix range in O(log N) and iterate matches as a contiguous
    /// run. The sort also eliminates HashSet-iteration ordering randomness that previously
    /// caused identically-scored prefix matches to surface in arbitrary order.
    /// </summary>
    /// <param name="this">Initialised object db</param>
    /// <returns>sorted (ordinal-ignore-case ascending) array of all names and canonical designations</returns>
    public string[] CreateAutoCompleteList()
    {
        var commonNames = CommonNames;
        var objIndices = AllObjectIndices;

        var canonicalSet = new HashSet<string>((int)(objIndices.Count * 1.3f));
        foreach (var objIndex in objIndices)
        {
            canonicalSet.Add(objIndex.ToCanonical(CanonicalFormat.Normal));
            canonicalSet.Add(objIndex.ToCanonical(CanonicalFormat.Alternative));
        }

        var names = new string[canonicalSet.Count + commonNames.Count];
        canonicalSet.CopyTo(names, 0);
        commonNames.CopyTo(names, canonicalSet.Count);

        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }
}
