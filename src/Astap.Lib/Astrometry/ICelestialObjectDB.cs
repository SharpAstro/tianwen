using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using static Astap.Lib.Astrometry.Utils;

namespace Astap.Lib.Astrometry;

public interface ICelestialObjectDB
{
    bool TryResolveCommonName(string name, [NotNullWhen(true)] out CatalogIndex[]? matches);

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
