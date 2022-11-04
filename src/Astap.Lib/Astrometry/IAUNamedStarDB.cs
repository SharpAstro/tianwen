using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    string? WDS_J,
    float? Vmag,
    double RA_J2000, // in degrees 0..360
    double Dec_J2000,
    DateTime ApprovalDate
);

public class IAUNamedStarDB : ICelestialObjectDB
{
    private readonly Dictionary<CatalogIndex, CelestialObject> _stellarObjectsByCatalogIndex = new(460);
    private readonly Dictionary<string, CatalogIndex> _namesToCatalogIndex = new(460);
    private readonly Dictionary<CatalogIndex, (CatalogIndex i1, CatalogIndex[]? ext)> _crossIndexLookuptable = new(460);

    private HashSet<CatalogIndex>? _catalogIndicesCache;
    private HashSet<Catalog>? _catalogCache;

    public IReadOnlyCollection<string> CommonNames => _namesToCatalogIndex.Keys;

    public IReadOnlySet<Catalog> Catalogs
    {
        get
        {
            if (_catalogCache is var cache and not null)
            {
                return cache;
            }

            if (_stellarObjectsByCatalogIndex.Count > 0)
            {
                return _catalogCache ??= this.IndicesToCatalogs<HashSet<Catalog>>();
            }
            return new HashSet<Catalog>(0);
        }
    }

    /// <inheritdoc/>
    public IReadOnlySet<CatalogIndex> ObjectIndices
    {
        get
        {
            if (_catalogIndicesCache is var cache and not null)
            {
                return cache;
            }

            var objs = _stellarObjectsByCatalogIndex.Count;
            if (objs > 0)
            {
                cache = new HashSet<CatalogIndex>(objs);
                cache.UnionWith(_stellarObjectsByCatalogIndex.Keys);

                return _catalogIndicesCache ??= cache;
            }
            return new HashSet<CatalogIndex>(0);
        }
    }

    /// <inheritdoc/>
    public async Task<(int processed, int failed)> InitDBAsync()
    {
        var processed = 0;
        var failed = 0;
        var assembly = typeof(IAUNamedStarDB).Assembly;
        var namedStarsJsonFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith(".iau-named-stars.json"));

        if (namedStarsJsonFileName is not null && assembly.GetManifestResourceStream(namedStarsJsonFileName) is Stream stream)
        {
            await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<IAUNamedStarDTO>(stream))
            {
                if (record is not null && Utils.TryGetCleanedUpCatalogName(record.Designation, out var catalogIndex))
                {
                    var objType = catalogIndex.ToCatalog() == Catalog.PSR ? ObjectType.Pulsar : ObjectType.Star;
                    var constellation = AbbreviationToEnumMember<Constellation>(record.Constellation);
                    var commonNames = new string[record.ID is not null ? 2 : 1];
                    commonNames[0] = record.IAUName;

                    if (record.ID is not null)
                    {
                        var bayerName = string.Join(' ', record.ID, constellation.ToIAUAbbreviation(), record.WDSComponentId).TrimEnd();
                        commonNames[1] = bayerName;
                    }

                    var stellarObject = _stellarObjectsByCatalogIndex[catalogIndex] = new CelestialObject(
                        catalogIndex,
                        objType,
                        record.RA_J2000 / 15.0,
                        record.Dec_J2000,
                        constellation,
                        record.Vmag ?? float.NaN,
                        float.NaN,
                        commonNames
                    );

                    if (record.WDS_J is string wdsJ && Utils.TryGetCleanedUpCatalogName($"{Catalog.WDS.ToCanonical()}J{wdsJ}", out var wdsCatalogIndex))
                    {
                        _crossIndexLookuptable.AddLookupEntry(catalogIndex, wdsCatalogIndex);
                        _crossIndexLookuptable.AddLookupEntry(wdsCatalogIndex, catalogIndex);
                    }

                    foreach (var commonName in commonNames)
                    {
                        _namesToCatalogIndex[commonName] = stellarObject.Index;
                    }
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

    /// <inheritdoc/>
    public bool TryLookupByIndex(CatalogIndex catalogIndex, out CelestialObject namedStar)
        => _stellarObjectsByCatalogIndex.TryGetValue(catalogIndex, out namedStar);

    /// <inheritdoc/>
    public bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> starIndices)
    {
        if (_namesToCatalogIndex.TryGetValue(name, out var idx))
        {
            starIndices = new[] { idx };
            return true;
        }

        starIndices = Array.Empty<CatalogIndex>();
        return false;
    }

    /// <inheritdoc/>
    public bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlyList<CatalogIndex> crossIndices)
        => _crossIndexLookuptable.TryGetLookupEntries(catalogIndex, out crossIndices);
}
