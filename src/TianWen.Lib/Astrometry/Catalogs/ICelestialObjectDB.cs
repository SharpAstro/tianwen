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
    /// of all names and designations.
    /// </summary>
    /// <param name="this">Initialised object db</param>
    /// <returns>copied array of all names and canonical designations</returns>
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

        return names;
    }
}
