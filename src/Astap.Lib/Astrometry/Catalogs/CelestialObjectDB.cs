using CsvHelper;
using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Astap.Lib.Astrometry.Catalogs.CatalogUtils;
using static Astap.Lib.Astrometry.CoordinateUtils;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry.Catalogs;

public sealed class CelestialObjectDB : ICelestialObjectDB
{
    private static readonly Dictionary<CatalogIndex, (ObjectType objType, string[] commonNames)> _predefinedObjects = new()
    {
        [CatalogIndex.Sol] = (ObjectType.Star, new []{ "Sun", "Sol" }),
        [CatalogIndex.Mercury] = (ObjectType.Planet, new[] { "Mercury" }),
        [CatalogIndex.Venus] = (ObjectType.Planet, new[] { "Venus" }),
        [CatalogIndex.Earth] = (ObjectType.Planet, new[] { "Earth" }),
        [CatalogIndex.Moon] = (ObjectType.Planet, new[] { "Moon", "Luna" }),
        [CatalogIndex.Mars] = (ObjectType.Planet, new[] { "Mars" }),
        [CatalogIndex.Jupiter] = (ObjectType.Planet, new[] { "Jupiter" }),
        [CatalogIndex.Saturn] = (ObjectType.Planet, new[] { "Saturn" }),
        [CatalogIndex.Uranus] = (ObjectType.Planet, new[] { "Uranus" }),
        [CatalogIndex.Neptune] = (ObjectType.Planet, new[] { "Neptune" })
    };

    private static readonly IReadOnlySet<string> EmptySet = ImmutableHashSet.Create<string>();

    private readonly Dictionary<CatalogIndex, CelestialObject> _objectsByIndex = new(17000);
    private readonly Dictionary<CatalogIndex, (CatalogIndex i1, CatalogIndex[]? ext)> _crossIndexLookuptable = new(11000);
    private readonly Dictionary<string, (CatalogIndex i1, CatalogIndex[]? ext)> _objectsByCommonName = new(5600);
    private readonly RaDecIndex _raDecIndex = new();

    private HashSet<CatalogIndex>? _catalogIndicesCache;
    private HashSet<Catalog>? _catalogCache;

    public CelestialObjectDB() { }

    public IReadOnlyCollection<string> CommonNames => _objectsByCommonName.Keys;

    public IReadOnlySet<Catalog> Catalogs
    {
        get
        {
            if (_catalogCache is var cache and not null)
            {
                return cache;
            }

            var objs = _objectsByIndex.Count + _crossIndexLookuptable.Count;

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

            var objs = _objectsByIndex.Count + _crossIndexLookuptable.Count;
            if (objs > 0)
            {
                cache = new HashSet<CatalogIndex>(objs);
                cache.UnionWith(_objectsByIndex.Keys);
                cache.UnionWith(_crossIndexLookuptable.Keys);

                return _catalogIndicesCache ??= cache;
            }

            return new HashSet<CatalogIndex>(0);
        }
    }

    public IRaDecIndex CoordinateGrid => _raDecIndex;

    /// <inheritdoc/>
    public bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> matches) => _objectsByCommonName.TryGetLookupEntries(name, out matches);

    private static readonly ulong[] CrossCats = new[] {
        Catalog.Barnard,
        Catalog.Caldwell,
        Catalog.Ced,
        Catalog.Collinder,
        Catalog.CG,
        Catalog.DG,
        Catalog.Dobashi,
        Catalog.GUM,
        Catalog.HD,
        Catalog.HR,
        Catalog.HH,
        Catalog.IC,
        Catalog.LDN,
        Catalog.Messier,
        Catalog.Melotte,
        Catalog.Sharpless,
        Catalog.RCW,
        Catalog.UGC,
        Catalog.vdB
    }.Select(c => (ulong)c).OrderBy(x => x).ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCrossCat(Catalog cat) => Array.BinarySearch(CrossCats, (ulong)cat) >= 0;

    /// <inheritdoc/>
    public bool TryLookupByIndex(CatalogIndex index, [NotNullWhen(true)] out CelestialObject celestialObject)
    {
        if (!_objectsByIndex.TryGetValue(index, out celestialObject)
            && IsCrossCat(index.ToCatalog())
            && _crossIndexLookuptable.TryGetValue(index, out var crossIndices)
        )
        {
            if (crossIndices.i1 != 0 && crossIndices.i1 != index && _objectsByIndex.TryGetValue(crossIndices.i1, out celestialObject))
            {
                index = crossIndices.i1;
            }
            else if (crossIndices.ext is { Length: > 0 })
            {
                foreach (var crossIndex in crossIndices.ext)
                {
                    if (crossIndex != 0 && crossIndex != index && _objectsByIndex.TryGetValue(crossIndex, out celestialObject))
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

        if (_crossIndexLookuptable.TryGetValue(index, out var followIndicies) && followIndicies.i1 > 0)
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

    public bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlyList<CatalogIndex> crossIndices)
        => _crossIndexLookuptable.TryGetLookupEntries(catalogIndex, out crossIndices);

    /// <inheritdoc/>
    public async Task<(int processed, int failed)> InitDBAsync()
    {
        var assembly = typeof(CelestialObjectDB).Assembly;
        int totalProcessed = 0;
        int totalFailed = 0;

        foreach (var predefined in _predefinedObjects)
        {
            var cat = predefined.Key.ToCatalog();
            var commonNames = new HashSet<string>(predefined.Value.commonNames);
            commonNames.TrimExcess();
            _objectsByIndex[predefined.Key] = new CelestialObject(predefined.Key, predefined.Value.objType, double.NaN, double.NaN, 0, float.NaN, float.NaN, commonNames);
            AddCommonNameIndex(predefined.Key, commonNames);
            totalProcessed++;
        }

        foreach (var csvName in new[] { "NGC", "NGC.addendum" })
        {
            var (processed, failed) = await ReadEmbeddedGzippedCsvDataFileAsync(assembly, csvName);
            totalProcessed += processed;
            totalFailed += failed;
        }

        var simbadCatalogs = new[] {
            ("HR", Catalog.HR),
            ("GUM", Catalog.GUM),
            ("RCW", Catalog.RCW),
            ("LDN", Catalog.LDN),
            ("Dobashi", Catalog.Dobashi),
            ("Sh", Catalog.Sharpless),
            ("Barnard", Catalog.Barnard),
            ("Ced", Catalog.Ced),
            ("CG", Catalog.CG),
            ("vdB", Catalog.vdB),
            ("DG", Catalog.DG),
            ("HH", Catalog.HH),
            ("OCl", Catalog.Melotte),
            ("OCl", Catalog.Collinder)
        };
        foreach (var (fileName, catToAdd) in simbadCatalogs)
        {
            var (processed, failed) = await ReadEmbeddedGzippedJsonDataFileAsync(assembly, fileName, catToAdd);
            totalProcessed += processed;
            totalFailed += failed;
        }

        return (totalProcessed, totalFailed);
    }

    private async Task<(int processed, int failed)> ReadEmbeddedGzippedCsvDataFileAsync(Assembly assembly, string csvName)
    {
        const string NGC = nameof(NGC);
        const string IC = nameof(IC);
        const string M = nameof(M);

        int processed = 0;
        int failed = 0;
        var manifestFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith("." + csvName + ".csv.gz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return (processed, failed);
        }

        using var gzipStream = new GZipStream(stream, CompressionMode.Decompress, false);
        using var streamReader = new StreamReader(gzipStream, new UTF8Encoding(false), leaveOpen: true);
        using var csvReader = new CsvReader(new CsvParser(streamReader, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = ";" }, leaveOpen: true));

        if (!await csvReader.ReadAsync() || !csvReader.ReadHeader())
        {
            return (processed, failed);
        }

        while (await csvReader.ReadAsync())
        {
            if (csvReader.TryGetField<string>("Name", out var entryName)
                && csvReader.TryGetField<string>("Type", out var objectTypeAbbr)
                && objectTypeAbbr is { Length: > 0 }
                && csvReader.TryGetField<string>("RA", out var raHMS)
                && raHMS is not null
                && csvReader.TryGetField<string>("Dec", out var decDMS)
                && decDMS is not null
                && csvReader.TryGetField<string>("Const", out var constAbbr)
                && constAbbr is not null
                && TryGetCleanedUpCatalogName(entryName, out var indexEntry)
            )
            {
                var objectType = AbbreviationToEnumMember<OpenNGCObjectType>(objectTypeAbbr).ToObjectType();
                var @const = AbbreviationToEnumMember<Constellation>(constAbbr);

                var vmag = csvReader.TryGetField<string>("V-Mag", out var vmagStr)
                    && float.TryParse(vmagStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var vmagFloat)
                    ? vmagFloat
                    : float.NaN;

                var surfaceBrightness = csvReader.TryGetField<string>("SurfBr", out var surfaceBrightnessStr)
                    && float.TryParse(surfaceBrightnessStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var surfaceBrightnessFloat)
                    ? surfaceBrightnessFloat
                    : float.NaN;

                IReadOnlySet<string> commonNames;
                if (csvReader.TryGetField<string>("Common names", out var commonNamesEntry) && !string.IsNullOrWhiteSpace(commonNamesEntry))
                {
                    commonNames = new HashSet<string>(commonNamesEntry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else
                {
                    commonNames = EmptySet;
                }

                var ra = HMSToHours(raHMS);
                var dec = DMSToDegree(decDMS);
                var obj = _objectsByIndex[indexEntry] = new CelestialObject(
                    indexEntry,
                    objectType,
                    ra,
                    dec,
                    @const,
                    vmag,
                    surfaceBrightness,
                    commonNames
                );

                AddCommonNameAndPosIndices(obj);

                if (objectType == ObjectType.Duplicate)
                {
                    // when the entry is a duplicate, use the cross lookup table to list the entries it duplicates
                    if (TryGetCatalogField(NGC, out var ngcIndexEntry))
                    {
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, ngcIndexEntry);
                    }
                    if (TryGetCatalogField(M, out var messierIndexEntry))
                    {
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, messierIndexEntry);
                    }
                    if (TryGetCatalogField(IC, out var icIndexEntry))
                    {
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, icIndexEntry);
                    }
                }
                else
                {
                    if (TryGetCatalogField(IC, out var icIndexEntry) && indexEntry != icIndexEntry)
                    {
                        _crossIndexLookuptable.AddLookupEntry(icIndexEntry, indexEntry);
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, icIndexEntry);
                    }
                    if (TryGetCatalogField(M, out var messierIndexEntry) && indexEntry != messierIndexEntry)
                    {
                        // Adds Messier to NGC/IC entry lookup, but only if its not a duplicate
                        _crossIndexLookuptable.AddLookupEntry(messierIndexEntry, indexEntry);
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, messierIndexEntry);
                    }

                    if (csvReader.TryGetField<string>("Identifiers", out var identifiersEntry) && identifiersEntry is { Length: > 0 })
                    {
                        var identifiers = identifiersEntry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var identifier in identifiers)
                        {
                            if (identifier[0] is 'C' or 'M' or 'U' or 'S'
                                && identifier.Length >= 2
                                && (identifier[1] is 'G' or 'H' or 'e' or 'l' or 'r' or ' ' || char.IsDigit(identifier[1]))
                                && TryGetCleanedUpCatalogName(identifier, out var crossCatIdx)
                                && IsCrossCat(crossCatIdx.ToCatalog())
                            )
                            {
                                _crossIndexLookuptable.AddLookupEntry(crossCatIdx, indexEntry);
                                _crossIndexLookuptable.AddLookupEntry(indexEntry, crossCatIdx);
                            }
                        }
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

    static readonly Regex ClusterMemberPattern = new(@"^[A-Za-z]+\s+\d+\s+\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

    private async Task<(int processed, int failed)> ReadEmbeddedGzippedJsonDataFileAsync(Assembly assembly, string jsonName, Catalog catToAdd)
    {
        const string NAME_CAT_PREFIX = "NAME ";
        const string STAR_CAT_PREFIX = "* ";
        const string CLUSTER_PREFIX = "Cl ";

        var processed = 0;
        var failed = 0;

        var manifestFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith("." + jsonName + ".json.gz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return (processed, failed);
        }

        using var gzipStream = new GZipStream(stream, CompressionMode.Decompress, false);

        var mainCatalogs = new HashSet<Catalog>();
        foreach (var objIndex in new HashSet<CatalogIndex>(_objectsByIndex.Keys))
        {
            _ = mainCatalogs.Add(objIndex.ToCatalog());
        }

        await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<SimbadCatalogDto>(gzipStream))
        {
            if (record is null)
            {
                continue;
            }

            var catToAddIdxs = new SortedSet<CatalogIndex>();
            var relevantIds = new Dictionary<Catalog, CatalogIndex[]>();
            var commonNames = new HashSet<string>(8);
            foreach (var idOrig in record.Ids)
            {
                var isCluster = idOrig.StartsWith(CLUSTER_PREFIX);
                var id = isCluster ? idOrig[CLUSTER_PREFIX.Length..] : idOrig;
                if (id.StartsWith(NAME_CAT_PREFIX))
                {
                    commonNames.Add(id[NAME_CAT_PREFIX.Length..].TrimStart());
                }
                else if (id.StartsWith(STAR_CAT_PREFIX))
                {
                    commonNames.Add(id[STAR_CAT_PREFIX.Length..].TrimStart());
                }
                else if ((isCluster || !ClusterMemberPattern.IsMatch(id)) && TryGetCleanedUpCatalogName(id, out var catId))
                {
                    // skip open cluster members for now
                    var cat = catId.ToCatalog();
                    if (cat == catToAdd)
                    {
                        catToAddIdxs.Add(catId);
                    }
                    else if (mainCatalogs.Contains(cat))
                    {
                        relevantIds.AddLookupEntry(cat, catId);
                    }
                }
            }
            commonNames.TrimExcess();

            if (catToAddIdxs.Any())
            {
                var bestMatches = (
                    from relevantIdPerCat in relevantIds
                    from relevantId in relevantIdPerCat.Value
                    where _objectsByIndex.ContainsKey(relevantId)
                    let sortKey = relevantIdPerCat.Key switch
                    {
                        Catalog.NGC => 1u,
                        Catalog.IC => 2u,
                        Catalog.Messier => 3u,
                        _ => (ulong)relevantIdPerCat.Key
                    }
                    orderby sortKey, relevantId
                    select relevantId
                ).ToList();

                foreach (var catToAddIdx in catToAddIdxs)
                {
                    if (bestMatches.Any())
                    {
                        foreach (var bestMatch in bestMatches)
                        {
                            _crossIndexLookuptable.AddLookupEntry(bestMatch, catToAddIdx);
                            _crossIndexLookuptable.AddLookupEntry(catToAddIdx, bestMatch);

                            if (commonNames.Count > 0)
                            {
                                UpdateObjectCommonNames(bestMatch, commonNames);
                            }
                        }
                    }
                    else if (TryGetCrossIndices(catToAddIdx, out var crossIndices))
                    {
                        if (commonNames.Count > 0)
                        {
                            foreach (var crossIndex in crossIndices)
                            {
                                UpdateObjectCommonNames(crossIndex, commonNames);
                            }
                        }
                    }
                    else
                    {
                        var raInH = record.Ra / 15;
                        if (ConstellationBoundary.TryFindConstellation(raInH, record.Dec, out var constellation))
                        {
                            var objType = AbbreviationToEnumMember<ObjectType>(record.ObjType);
                            var trimmedSetOrEmpty = commonNames.Count > 0 ? new HashSet<string>(commonNames) : EmptySet;
                            var obj = _objectsByIndex[catToAddIdx] = new CelestialObject(catToAddIdx, objType, raInH, record.Dec, constellation, float.NaN, float.NaN, trimmedSetOrEmpty);

                            AddCommonNameAndPosIndices(obj);
                        }
                        else
                        {
                            failed++;
                        }
                    }
                }
            }

            processed++;
        }

        return (processed, failed);
    }

    void UpdateObjectCommonNames(CatalogIndex id, HashSet<string> commonNames)
    {
        if (_objectsByIndex.TryGetValue(id, out var obj) && !obj.CommonNames.SetEquals(commonNames))
        {
            var modObj = new CelestialObject(
                id,
                obj.ObjectType,
                obj.RA,
                obj.Dec,
                obj.Constellation,
                obj.V_Mag,
                obj.SurfaceBrightness,
                commonNames.UnionWithAsReadOnlyCopy(obj.CommonNames)
            );

            _objectsByIndex[id] = modObj;

            commonNames.ExceptWith(obj.CommonNames);
            AddCommonNameIndex(id, commonNames);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    void AddCommonNameAndPosIndices(in CelestialObject obj)
    {
        _raDecIndex.Add(obj);

        AddCommonNameIndex(obj.Index, obj.CommonNames);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    void AddCommonNameIndex(CatalogIndex catIdx, IReadOnlySet<string> commonNames)
    {
        if (ReferenceEquals(commonNames, EmptySet))
        {
            return;
        }

        foreach (var commonName in commonNames)
        {
            _objectsByCommonName.AddLookupEntry(commonName, catIdx);
        }
    }
}
