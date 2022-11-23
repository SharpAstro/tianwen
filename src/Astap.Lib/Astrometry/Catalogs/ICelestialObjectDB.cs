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

    IRaDecIndex CoordinateGrid { get; }

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
