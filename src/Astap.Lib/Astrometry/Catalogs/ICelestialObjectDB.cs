using System.Collections.Generic;
using System.Threading.Tasks;
using static Astap.Lib.Astrometry.Catalogs.CatalogUtils;

namespace Astap.Lib.Astrometry.Catalogs;

public interface ICelestialObjectDB
{
    bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> matches);

    bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlyList<CatalogIndex> crossIndices);

    IReadOnlySet<CatalogIndex> ObjectIndices { get; }

    IReadOnlySet<Catalog> Catalogs => this.IndicesToCatalogs<HashSet<Catalog>>();

    Task<(int processed, int failed)> InitDBAsync();

    IReadOnlyCollection<string> CommonNames { get; }

    bool TryLookupByIndex(CatalogIndex index, out CelestialObject celestialObject);

    public bool TryLookupByIndex(string name, out CelestialObject celestialObject)
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


    public string[] InitAutoComplete()
    {
        var commonNames = CommonNames;
        var objIndices = ObjectIndices;
        var names = new string[objIndices.Count + commonNames.Count];
        int i = 0;
        foreach (var commonName in commonNames)
        {
            names[i++] = commonName;
        }
        foreach (var objIndex in objIndices)
        {
            names[i++] = objIndex.ToCanonical();
        }

        return names;
    }
}

public static class ICelestialObjectDBEx
{
    internal static TSet IndicesToCatalogs<TSet>(this ICelestialObjectDB @this)
         where TSet : ISet<Catalog>, new()
    {
        var catalogs = new TSet();
        foreach (var objIndex in @this.ObjectIndices)
        {
            _ = catalogs.Add(objIndex.ToCatalog());
        }

        return catalogs;
    }
}
