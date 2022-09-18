using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry;

record IAUNamedStarDTO(
    string IAUName,
    string Designation,
    string? ID,
    string Constellation,
    string? WDSComponentId,
    double? Vmag,
    double RA_J2000,
    double Dec_J2000,
    DateTime ApprovalDate
);

public record IAUNamedStar(string Name, double? Vmag, CatalogIndex Index, ObjectType ObjectType, double RA, double Dec, Constellation Constellation)
    : CelestialObject(Index, ObjectType, RA, Dec, Constellation);

public class IAUNamedStarDB : ICelestialObjectDB<IAUNamedStar>
{
    private readonly Dictionary<CatalogIndex, IAUNamedStar> _stellarObjectsByCatalogIndex = new(460);
    private readonly Dictionary<string, CatalogIndex> _namesToCatalogIndex = new(460);

    private HashSet<CatalogIndex>? _catalogIndicesCache;

    public IReadOnlyCollection<string> CommonNames => _namesToCatalogIndex.Keys;

    public async Task<(int processed, int failed)> InitDBAsync()
    {
        var processed = 0;
        var failed = 0;
        var assembly = typeof(IAUNamedStarDB).Assembly;
        var namedStarsJsonFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith("iau-named-stars.json"));

        if (namedStarsJsonFileName is not null && assembly.GetManifestResourceStream(namedStarsJsonFileName) is Stream stream)
        {
            await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<IAUNamedStarDTO>(stream))
            {
                if (record is not null && Utils.TryGetCleanedUpCatalogName(record.Designation, out var catalogIndex))
                {
                    var objType = catalogIndex.ToCatalog() == Catalog.PSR ? ObjectType.Pulsar : ObjectType.Star;
                    var constellation = AbbreviationToEnumMember<Constellation>(record.Constellation);
                    var stellarObject = new IAUNamedStar(record.IAUName, record.Vmag, catalogIndex, objType, record.RA_J2000, record.Dec_J2000, constellation);
                    _stellarObjectsByCatalogIndex[catalogIndex] = stellarObject;
                    _namesToCatalogIndex[stellarObject.Name] = stellarObject.Index;
                    processed++;
                }
                else
                {
                    failed++;
                }
            }
        }

        return (processed, failed);
    }

    public IReadOnlySet<CatalogIndex> ObjectIndices
    {
        get
        {
            if (_catalogIndicesCache is var cache and not null)
            {
                return cache;
            }

            var cap = _stellarObjectsByCatalogIndex.Count;
            if (cap > 0)
            {
                cache = new HashSet<CatalogIndex>(cap);
                cache.UnionWith(_stellarObjectsByCatalogIndex.Keys);

                return _catalogIndicesCache ??= cache;
            }
            else
            {
                return new HashSet<CatalogIndex>(0);
            }
        }
    }

    public bool TryLookupByIndex(CatalogIndex catalogIndex, [NotNullWhen(true)] out IAUNamedStar? namedStar)
        => _stellarObjectsByCatalogIndex.TryGetValue(catalogIndex, out namedStar);

    public bool TryResolveCommonName(string name, [NotNullWhen(true)] out CatalogIndex[]? starIndices)
    {
        if (_namesToCatalogIndex.TryGetValue(name, out var idx))
        {
            starIndices = new[] { idx };
            return true;
        }
        else
        {
            starIndices = default;
            return false;
        }
    }
}
