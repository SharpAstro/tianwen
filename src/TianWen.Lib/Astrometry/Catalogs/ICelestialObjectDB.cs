using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using static TianWen.Lib.Astrometry.Catalogs.CatalogUtils;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Compact per-star record used for bulk enumeration of the Tycho-2 catalog,
/// primarily for GPU sky map rendering.
/// RA is in hours, Dec in degrees, <paramref name="VMag"/> is Johnson V, <paramref name="BMinusV"/>
/// is the colour index (≈ 0.65 for solar-type stars when the blue channel is missing).
/// </summary>
public readonly record struct Tycho2StarLite(float RaHours, float DecDeg, float VMag, float BMinusV);

public interface ICelestialObjectDB
{
    bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> matches);

    bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlySet<CatalogIndex> crossIndices);

    IReadOnlySet<CatalogIndex> AllObjectIndices { get; }

    IReadOnlySet<Catalog> Catalogs { get; }

    Task InitDBAsync(CancellationToken cancellationToken);

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
