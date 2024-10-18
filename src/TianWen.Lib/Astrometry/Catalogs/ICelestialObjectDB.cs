using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using static TianWen.Lib.Astrometry.Catalogs.CatalogUtils;

namespace TianWen.Lib.Astrometry.Catalogs;

public interface ICelestialObjectDB
{
    bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> matches);

    bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlySet<CatalogIndex> crossIndices);

    IReadOnlySet<CatalogIndex> AllObjectIndices { get; }

    IReadOnlySet<Catalog> Catalogs { get; }

    Task<(int Processed, int Failed)> InitDBAsync();

    IReadOnlyCollection<string> CommonNames { get; }

    IRaDecIndex CoordinateGrid { get; }

    bool TryLookupByIndex(CatalogIndex index, [NotNullWhen(true)] out CelestialObject celestialObject);

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
