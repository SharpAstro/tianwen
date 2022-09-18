using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Astap.Lib.Astrometry;

public interface ICelestialObjectDB<TObj>
    where TObj : CelestialObject
{
    bool TryResolveCommonName(string name, [NotNullWhen(true)] out CatalogIndex[]? matches);

    IReadOnlySet<CatalogIndex> ObjectIndices { get; }

    IReadOnlyCollection<string> CommonNames { get; }

    bool TryLookupByIndex(CatalogIndex index, [NotNullWhen(true)] out TObj? celestialObject);

    Task<(int processed, int failed)> InitDBAsync();
}
