using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace Astap.Lib.Astrometry.Catalogs;

public class CombinedDB : ICelestialObjectDB
{
    private readonly IAUNamedStarDB _namedStarDB = new();
    private readonly OpenNGCDB _openNGCDB = new();
    private readonly ICelestialObjectDB[] _allDBs;
    private readonly HashSet<Catalog> _catalogs = new();
    private readonly HashSet<CatalogIndex> _objectIndices = new();
    private readonly HashSet<string> _commonNames = new();

    public CombinedDB() => _allDBs = new ICelestialObjectDB[] { _namedStarDB, _openNGCDB };

    /// <inheritdoc/>
    public IReadOnlySet<CatalogIndex> ObjectIndices => _objectIndices;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> CommonNames => _commonNames;

    /// <inheritdoc/>
    public IReadOnlySet<Catalog> Catalogs => _catalogs;

    /// <inheritdoc/>
    public async Task<(int processed, int failed)> InitDBAsync()
    {
        var initTasks = new Task<(int processed, int failed)>[_allDBs.Length];

        for (var i = 0; i < _allDBs.Length; i++)
        {
            initTasks[i] = _allDBs[i].InitDBAsync();
        }

        var results = await Task.WhenAll(initTasks);

        foreach (var db in _allDBs)
        {
            _objectIndices.UnionWith(db.ObjectIndices);
            _commonNames.UnionWith(db.CommonNames);
            _catalogs.UnionWith(db.Catalogs);
        }

        return results.Aggregate((processed: 0, failed: 0), (pPrev, pCurr) => (pPrev.processed + pCurr.processed, pPrev.failed + pCurr.failed));
    }

    /// <inheritdoc/>
    public bool TryLookupByIndex(CatalogIndex index, out CelestialObject celestialObject)
    {
        var cat = index.ToCatalog();
        foreach (var db in _allDBs)
        {
            if (db.Catalogs.Contains(cat) && db.TryLookupByIndex(index, out celestialObject))
            {
                return true;
            }
        }

        celestialObject = default;
        return false;
    }

    /// <inheritdoc/>
    public bool TryResolveCommonName(string name, [NotNullWhen(true)] out IReadOnlyList<CatalogIndex> matches)
    {
        var allResults = new HashSet<CatalogIndex>();
        var hasMatch = false;
        foreach (var db in _allDBs)
        {
            if (db.TryResolveCommonName(name, out var dbMatches))
            {
                hasMatch = true;
                allResults.UnionWith(dbMatches);
            }
        }

        matches = allResults.ToImmutableArray();
        return hasMatch;
    }

    /// <inheritdoc/>
    public bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlyList<CatalogIndex> crossIndices)
    {
        var allResults = new HashSet<CatalogIndex>();
        var hasMatch = false;
        foreach (var db in _allDBs)
        {
            if (db.TryGetCrossIndices(catalogIndex, out var dbMatches))
            {
                hasMatch = true;
                allResults.UnionWith(dbMatches);
            }
        }

        crossIndices = allResults.ToImmutableArray();
        return hasMatch;
    }
}
