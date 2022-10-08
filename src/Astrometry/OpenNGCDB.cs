using CsvHelper;
using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using static Astap.Lib.Astrometry.Utils;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry;

public class OpenNGCDB : ICelestialObjectDB
{
    private readonly Dictionary<CatalogIndex, CelestialObject> _objectsByIndex = new(14000);
    private readonly Dictionary<CatalogIndex, (CatalogIndex i1, CatalogIndex[]? ext)> _crossLookupTable = new(900);
    private readonly Dictionary<string, CatalogIndex[]> _objectsByCommonName = new(200);

    private HashSet<CatalogIndex>? _catalogIndicesCache;
    private HashSet<Catalog>? _catalogCache;

    public OpenNGCDB() { }

    public IReadOnlyCollection<string> CommonNames => _objectsByCommonName.Keys;

    public IReadOnlySet<Catalog> Catalogs
    {
        get
        {
            if (_catalogCache is var cache and not null)
            {
                return cache;
            }

            var objs = _objectsByIndex.Count + _crossLookupTable.Count;

            if (objs > 0)
            {
                return _catalogCache ??= this.IndicesToCatalogs<HashSet<Catalog>>();
            }
            return new HashSet<Catalog>(0);
        }
    }

    public IReadOnlySet<CatalogIndex> ObjectIndices
    {
        get
        {
            if (_catalogIndicesCache is var cache and not null)
            {
                return cache;
            }

            var objs = _objectsByIndex.Count + _crossLookupTable.Count;
            if (objs > 0)
            {
                cache = new HashSet<CatalogIndex>(objs);
                cache.UnionWith(_objectsByIndex.Keys);
                cache.UnionWith(_crossLookupTable.Keys);

                return _catalogIndicesCache ??= cache;
            }

            return new HashSet<CatalogIndex>(0);
        }
    }

    public bool TryResolveCommonName(string name, [NotNullWhen(true)] out CatalogIndex[]? matches)
        => _objectsByCommonName.TryGetValue(name, out matches);

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

    private static readonly Catalog[] CrossCats = new[] {
        Catalog.Messier,
        Catalog.IC,
        Catalog.Caldwell,
        Catalog.Collinder,
        Catalog.Melotte,
        Catalog.UGC
    }.OrderBy(x => (ulong)x).ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCrossCat(Catalog cat) => Array.BinarySearch(CrossCats, cat) >= 0;

    public bool TryLookupByIndex(CatalogIndex index, [NotNullWhen(true)] out CelestialObject celestialObject)
    {
        if (!_objectsByIndex.TryGetValue(index, out celestialObject)
            && IsCrossCat(index.ToCatalog())
            && _crossLookupTable.TryGetValue(index, out var crossIndices)
        )
        {
            if (crossIndices.i1 > 0 && crossIndices.i1 != index && _objectsByIndex.TryGetValue(crossIndices.i1, out celestialObject))
            {
                index = crossIndices.i1;
            }
            else if (crossIndices.ext is not null)
            {
                foreach (var crossIndex in crossIndices.ext)
                {
                    if (crossIndex > 0 && crossIndex != index && _objectsByIndex.TryGetValue(crossIndex, out celestialObject))
                    {
                        index = crossIndex;
                        break;
                    }
                }
            }
        }

        if (celestialObject.Index is 0)
        {
            return false;
        }
        if (celestialObject.ObjectType is not ObjectType.Duplicate)
        {
            return true;
        }

        if (_crossLookupTable.TryGetValue(index, out var followIndicies) && followIndicies.i1 > 0)
        {
            if (followIndicies.ext == null && followIndicies.i1 != index)
            {
                return TryLookupByIndex(followIndicies.i1, out celestialObject);
            }
            else if (followIndicies.ext is CatalogIndex[] { Length: > 0 } ext)
            {
                var followedObjs = new List<CelestialObject>(ext.Length + 1);
                AddToFollowObjs(followedObjs, index, followIndicies.i1);

                foreach (var followIndex in followIndicies.ext)
                {
                    AddToFollowObjs(followedObjs, index, followIndex);
                }

                if (followedObjs.Count == 1)
                {
                    celestialObject = followedObjs[0];
                    return true;
                }
            }
        }
        return false;

        void AddToFollowObjs(List<CelestialObject> followedObjs, CatalogIndex index, CatalogIndex followIndex)
        {
            if (followIndex != index && _objectsByIndex.TryGetValue(followIndex, out var followedObj) && followedObj.ObjectType != ObjectType.Duplicate)
            {
                followedObjs.Add(followedObj);
            }
        }
    }

    public async Task<(int processed, int failed)> InitDBAsync()
    {
        var assembly = typeof(OpenNGCDB).Assembly;
        int totalProcessed = 0;
        int totalFailed = 0;

        foreach (var csvName in new[] { "NGC.csv", "addendum.csv" })
        {
            var (processed, failed) = await ReadEmbeddedDataFileAsync(assembly, csvName);
            totalProcessed += processed;
            totalFailed += failed;
        }

        return (totalProcessed, totalFailed);
    }

    private async Task<(int processed, int failed)> ReadEmbeddedDataFileAsync(Assembly assembly, string csvName)
    {
        const string NGC = nameof(NGC);
        const string IC = nameof(IC);
        const string M = nameof(M);

        int processed = 0;
        int failed = 0;
        var manifestFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith("." + csvName));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return (processed, failed);
        }

        using var streamReader = new StreamReader(stream, new UTF8Encoding(false));
        using var csvReader = new CsvReader(streamReader, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = ";" });

        if (!await csvReader.ReadAsync() || !csvReader.ReadHeader())
        {
            return (processed, failed);
        }

        while (await csvReader.ReadAsync())
        {
            if (csvReader.TryGetField<string>("Name", out var entryName)
                && csvReader.TryGetField<string>("Type", out var objectTypeAbbr)
                && csvReader.TryGetField<string>("RA", out var raHMS)
                && csvReader.TryGetField<string>("Dec", out var decDMS)
                && csvReader.TryGetField<string>("Const", out var constAbbr)
                && TryGetCleanedUpCatalogName(entryName, out var indexEntry)
            )
            {
                var objectType = AbbreviationToEnumMember<ObjectType>(objectTypeAbbr);
                var @const = AbbreviationToEnumMember<Constellation>(constAbbr);

                var vmag = csvReader.TryGetField<string>("V-Mag", out var vmagStr)
                    && double.TryParse(vmagStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var vmagDbl)
                    ? vmagDbl
                    : double.NaN;

                var surfaceBrightness = csvReader.TryGetField<string>("SurfBr", out var surfaceBrightnessStr)
                    && double.TryParse(surfaceBrightnessStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var surfaceBrightnessDbl)
                    ? surfaceBrightnessDbl
                    : double.NaN;

                _objectsByIndex[indexEntry] = new CelestialObject(indexEntry, objectType, HMSToDegree(raHMS), DMSToDegree(decDMS), @const, vmag, surfaceBrightness);

                if (objectType == ObjectType.Duplicate)
                {
                    // when the entry is a duplicate, use the cross lookup table to list the entries it duplicates
                    if (TryGetCatalogField(NGC, out var ngcIndexEntry))
                    {
                        _crossLookupTable.AddLookupEntry(indexEntry, ngcIndexEntry);
                    }
                    if (TryGetCatalogField(M, out var messierIndexEntry))
                    {
                        _crossLookupTable.AddLookupEntry(indexEntry, messierIndexEntry);
                    }
                    if (TryGetCatalogField(IC, out var icIndexEntry))
                    {
                        _crossLookupTable.AddLookupEntry(indexEntry, icIndexEntry);
                    }
                }
                else
                {
                    if (TryGetCatalogField(IC, out var icIndexEntry))
                    {
                        _crossLookupTable.AddLookupEntry(icIndexEntry, indexEntry);
                    }
                    if (TryGetCatalogField(M, out var messierIndexEntry))
                    {
                        // Adds Messier to NGC/IC entry lookup, but only if its not a duplicate
                        _crossLookupTable.AddLookupEntry(messierIndexEntry, indexEntry);
                    }

                    if (csvReader.TryGetField<string>("Identifiers", out var identifiersEntry))
                    {
                        var identifiers = identifiersEntry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var identifier in identifiers)
                        {
                            if (identifier[0] is 'C' or 'M' or 'U'
                                && identifier.Length >= 2
                                && identifier[1] is 'G' or 'e' or 'l' or 'r' or ' ' or '0'
                                && TryGetCleanedUpCatalogName(identifier, out var crossCatIdx)
                                && IsCrossCat(crossCatIdx.ToCatalog())
                            )
                            {
                                _crossLookupTable.AddLookupEntry(crossCatIdx, indexEntry);
                            }
                        }
                    }
                }

                if (csvReader.TryGetField<string>("Common names", out var commonNamesEntry))
                {
                    var commonNames = commonNamesEntry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var commonName in commonNames)
                    {
                        _objectsByCommonName.AddLookupEntry(commonName, indexEntry);
                    }
                }

                processed++;
            }
            else
            {
                failed++;
            }
        }

        return (processed, failed);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool TryGetCatalogField(string catPrefix, out CatalogIndex entry)
        {
            entry = 0;
            return csvReader.TryGetField<string>(catPrefix, out var suffix) && TryGetCleanedUpCatalogName(catPrefix + suffix, out entry);
        }
    }
}
