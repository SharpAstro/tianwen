using CsvHelper;
using CsvHelper.Configuration;
using System;
using System.Buffers.Binary;
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
    private static readonly Dictionary<CatalogIndex, (ObjectType ObjType, string[] CommonNames)> _predefinedObjects = new()
    {
        [CatalogIndex.Sol] = (ObjectType.Star, new[] { "Sun", "Sol" }),
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

    private static readonly IReadOnlySet<string> EmptyNameSet = ImmutableHashSet.Create<string>();

    private readonly CelestialObject[] _hip2000 = new CelestialObject[120404];
    private readonly Dictionary<CatalogIndex, CelestialObject> _objectsByIndex = new(32000);
    private readonly Dictionary<CatalogIndex, (CatalogIndex i1, CatalogIndex[]? ext)> _crossIndexLookuptable = new(39000);
    private readonly Dictionary<string, (CatalogIndex i1, CatalogIndex[]? ext)> _objectsByCommonName = new(5700);
    private readonly RaDecIndex _raDecIndex = new();
    
    private HashSet<CatalogIndex>? _catalogIndicesCache;
    private HashSet<Catalog>? _completeCatalogCache;
    private volatile int _hipProcessed;
    private volatile bool _isInitialized;
    private volatile int _hipCacheItemCount;

    public CelestialObjectDB() { }

    public IReadOnlyCollection<string> CommonNames => _objectsByCommonName.Keys;

    public IReadOnlySet<Catalog> Catalogs => GetOrRebuildIndex(ref _completeCatalogCache, RebuildCatalogCache);

    public IReadOnlySet<CatalogIndex> AllObjectIndices => GetOrRebuildIndex(ref _catalogIndicesCache, RebuildObjectIndices);

    public IRaDecIndex CoordinateGrid => _raDecIndex;

    /// <inheritdoc/>
    public bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> matches) => _objectsByCommonName.TryGetLookupEntries(name, out matches);

    private static readonly IReadOnlySet<Catalog> CrossCats = new HashSet<Catalog>() {
        Catalog.Barnard,
        Catalog.Caldwell,
        Catalog.Ced,
        Catalog.Collinder,
        Catalog.CG,
        Catalog.DG,
        Catalog.Dobashi,
        Catalog.GUM,
        Catalog.HD,
        Catalog.HIP,
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
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IsCrossCat(Catalog cat) => CrossCats.Contains(cat);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool TryLookupByIndexDirect(CatalogIndex index, [NotNullWhen(true)] out CelestialObject celestialObject, out Catalog cat, out int? arrayIndex)
    {
        (cat, var value, var msbSet) = index.ToCatalogAndValue();

        if (cat is Catalog.HIP && !msbSet)
        {
            var number = EnumValueToNumeric(value);
            if (number <= (ulong)_hip2000.Length)
            {
                var idx = (int)number - 1;
                celestialObject = _hip2000[idx];

                if (celestialObject.Index != 0)
                {
                    arrayIndex = idx;

                    return true;
                }
                else
                {
                    arrayIndex = null;

                    return false;
                }
            }
        }
        else if (_objectsByIndex.TryGetValue(index, out celestialObject))
        {
            arrayIndex = null;
            return true;
        }

        arrayIndex = null;
        celestialObject = default;
        return false;
    }

    /// <inheritdoc/>
    public bool TryLookupByIndex(CatalogIndex index, [NotNullWhen(true)] out CelestialObject celestialObject)
    {
        if (!TryLookupByIndexDirect(index, out celestialObject, out var cat, out _)
            && IsCrossCat(cat)
            && _crossIndexLookuptable.TryGetValue(index, out var crossIndices)
        )
        {
            if (crossIndices.i1 != 0 && crossIndices.i1 != index && TryLookupByIndexDirect(crossIndices.i1, out celestialObject, out _, out _))
            {
                index = crossIndices.i1;
            }
            else if (crossIndices.ext is { Length: > 0 } ext)
            {
                foreach (var crossIndex in ext)
                {
                    if (crossIndex != 0 && crossIndex != index && TryLookupByIndexDirect(crossIndex, out celestialObject, out _, out _))
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
        else if (celestialObject.ObjectType is not ObjectType.Duplicate)
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
            if (followIndex != index && TryLookupByIndexDirect(followIndex, out var followedObj, out _, out _) && followedObj.ObjectType != ObjectType.Duplicate)
            {
                followedObjs.Add(followedObj);
            }
        }
    }

    public bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlyList<CatalogIndex> crossIndices)
        => _crossIndexLookuptable.TryGetLookupEntries(catalogIndex, out crossIndices);

    /// <inheritdoc/>
    public async Task<(int Processed, int Failed)> InitDBAsync()
    {
        if (_isInitialized)
        {
            throw new InvalidOperationException("Already initialized!");
        }

        var assembly = typeof(CelestialObjectDB).Assembly;
        var totalProcessed = 0;
        var totalFailed = 0;

        var initHIP200Task = ReadEmbeddedGzippedHIP200BinaryFileAsync(assembly);

        foreach (var predefined in _predefinedObjects)
        {
            var cat = predefined.Key.ToCatalog();

            var commonNames = new HashSet<string>(predefined.Value.CommonNames);
            commonNames.TrimExcess();
            _objectsByIndex[predefined.Key] = new CelestialObject(predefined.Key, predefined.Value.ObjType, double.NaN, double.NaN, 0, float.NaN, float.NaN, commonNames);
            AddCommonNameIndex(predefined.Key, commonNames);
            totalProcessed++;
        }

        foreach (var csvName in new[] { "NGC", "NGC.addendum" })
        {
            var (processed, failed) = await ReadEmbeddedGzippedCsvDataFileAsync(assembly, csvName);
            totalProcessed += processed;
            totalFailed += failed;
        }

        (_hipProcessed, var hipFailed) = await initHIP200Task;
        totalProcessed += _hipProcessed;
        totalFailed += hipFailed;

        var simbadCatalogs = new[] {
            ("HR", Catalog.HR),
            ("HD", Catalog.HD),
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
            ("Cl", Catalog.Melotte),
            ("Cl", Catalog.Collinder)
        };
        foreach (var (fileName, catToAdd) in simbadCatalogs)
        {
            var (processed, failed) = await ReadEmbeddedGzippedJsonDataFileAsync(assembly, fileName, catToAdd);
            totalProcessed += processed;
            totalFailed += failed;
        }

        _isInitialized = true;
        return (totalProcessed, totalFailed);
    }

    private Task<(int Processed, int Failed)> ReadEmbeddedGzippedHIP200BinaryFileAsync(Assembly assembly)
    {
        var manifestFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith(".HIP.bin.gz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return Task.FromResult((0, 0));
        }

        return Task.Run(() =>
        {
            const int entrySize = 20; // update in ConvertTo-HIP2000Bin.ps1 as well
            var table = new byte[entrySize * _hip2000.Length];
            var entry = table.AsSpan();

            // Span<byte> entry = stackalloc byte[entrySize];
            int processed = 0;
            int failed = 0;

            var digits = Catalog.HIP.GetNumericalIndexSize();

            using var gzipStream = new GZipStream(stream, CompressionMode.Decompress, false);
            using var memStream = new MemoryStream(table);
            gzipStream.CopyTo(memStream);
            for (var index = 0; index < _hip2000.Length; index++, entry = entry[entrySize..])
            {
                var ra = BinaryPrimitives.ReadDoubleBigEndian(entry);
                var dec = BinaryPrimitives.ReadDoubleBigEndian(entry[8..]);
                var vmag = BinaryPrimitives.ReadSingleBigEndian(entry[16..]);
                if (ra != 0d || dec != 0d || vmag != 0f)
                {
                    var catIndex = PrefixedNumericToASCIIPackedInt<CatalogIndex>((uint)Catalog.HIP, index + 1, digits);
                    if (ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
                    {
                        var obj = _hip2000[index] = new CelestialObject(catIndex, ObjectType.Star, ra, dec, constellation, vmag, float.NaN, EmptyNameSet);

                        AddCommonNameAndPosIndices(obj);
                        processed++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }

            return (processed, failed);
        });
    }

    private async Task<(int Processed, int Failed)> ReadEmbeddedGzippedCsvDataFileAsync(Assembly assembly, string csvName)
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

        using var gzipStream = new GZipStream(stream, CompressionMode.Decompress);
        using var streamReader = new StreamReader(gzipStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var csvParser = new CsvParser(streamReader, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = ";" }, leaveOpen: true);
        using var csvReader = new CsvReader(csvParser);

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
                    commonNames = EmptyNameSet;
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

    private async Task<(int Processed, int Failed)> ReadEmbeddedGzippedJsonDataFileAsync(Assembly assembly, string jsonName, Catalog catToAdd)
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

        // will not be using cache as initialization is not complete
        var mainCatalogs = new HashSet<Catalog>(new HashSet<CatalogIndex>(_objectsByIndex.Keys).Select(idx => idx.ToCatalog()))
        {
            Catalog.HIP
        };

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
                else if ((isCluster || !ClusterMemberPattern.IsMatch(id))
                    && TryGetCleanedUpCatalogName(id, out var catId))
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
                    // TODO: Fixup missing HIP entries for which we have a HR/HD entry
                    where TryLookupByIndexDirect(relevantId, out _, out _, out _)
                    let sortKey = relevantIdPerCat.Key switch
                    {
                        Catalog.NGC => 1u,
                        Catalog.IC => 2u,
                        Catalog.Messier => 3u,
                        Catalog.HIP => uint.MaxValue,
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
                            var trimmedSetOrEmpty = commonNames.Count > 0 ? new HashSet<string>(commonNames) : EmptyNameSet;
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

    /// <summary>
    /// WARNING: Destructively updates <paramref name="commonNames"/>.
    /// </summary>
    /// <param name="catIdx"></param>
    /// <param name="commonNames"></param>
    private void UpdateObjectCommonNames(CatalogIndex catIdx, HashSet<string> commonNames)
    {
        if (TryLookupByIndexDirect(catIdx, out var obj, out var cat, out var arrayIndex)
            && !obj.CommonNames.SetEquals(commonNames))
        {
            var modObj = new CelestialObject(
                catIdx,
                obj.ObjectType,
                obj.RA,
                obj.Dec,
                obj.Constellation,
                obj.V_Mag,
                obj.SurfaceBrightness,
                commonNames.UnionWithAsReadOnlyCopy(obj.CommonNames)
            );

            if (arrayIndex is { } idx)
            {
                if (cat == Catalog.HIP)
                {
                    _hip2000[idx] = modObj;
                }
            }
            else
            {
                _objectsByIndex[catIdx] = modObj;
            }

            commonNames.ExceptWith(obj.CommonNames);
            AddCommonNameIndex(catIdx, commonNames);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void AddCommonNameAndPosIndices(in CelestialObject obj)
    {
        _raDecIndex.Add(obj);

        AddCommonNameIndex(obj.Index, obj.CommonNames);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void AddCommonNameIndex(CatalogIndex catIdx, IReadOnlySet<string> commonNames)
    {
        if (ReferenceEquals(commonNames, EmptyNameSet))
        {
            return;
        }

        foreach (var commonName in commonNames)
        {
            _objectsByCommonName.AddLookupEntry(commonName, catIdx);
        }
    }

    private IReadOnlySet<T> GetOrRebuildIndex<T>(ref HashSet<T>? cacheVar, Func<HashSet<T>> rebuildFunc)
    {
        if (cacheVar is { } cache && _isInitialized)
        {
            return cache;
        }

        var rebuildIndex = rebuildFunc();

        if (_isInitialized)
        {
            // this is the finite version of the cache as after intialization the DB
            // is immutable
            return cacheVar = rebuildIndex;
        }
        else
        {
            // return the cache but do no
            return rebuildIndex;
        }
    }

    private HashSet<Catalog> RebuildCatalogCache()
    {
        var catalogs = new HashSet<Catalog>(50);

        foreach (var objIndex in AllObjectIndices)
        {
            _ = catalogs.Add(objIndex.ToCatalog());
        }

        catalogs.EnsureCapacity(catalogs.Count + CrossCats.Count);
        catalogs.UnionWith(CrossCats);
        return catalogs;
    }

    private HashSet<CatalogIndex> RebuildObjectIndices()
    {
        var objCount = _objectsByIndex.Count + _crossIndexLookuptable.Count + _hipProcessed;
        if (objCount > 0 && _hipProcessed > 0)
        {
            // allow for a changing HIP array (recovered items etc.)
            var index = _catalogIndicesCache is { } cache && _hipCacheItemCount == _hipProcessed ? cache : HIPIndex();

            index.EnsureCapacity(objCount);
            index.UnionWith(_objectsByIndex.Keys);
            index.UnionWith(_crossIndexLookuptable.Keys);

            return index;
        }

        return new HashSet<CatalogIndex>(0);

        HashSet<CatalogIndex> HIPIndex()
        {
            var hipIndex = new HashSet<CatalogIndex>(_hipProcessed);
            for (var i = 0; i < _hip2000.Length; i++)
            {
                if (_hip2000[i].Index is { } idx and not 0)
                {
                    _ = hipIndex.Add(idx);
                }
            }
            // keep track of how many items we used to create the HIP index cache with
            // this works as we dont remove items but only potentially add items
            _hipCacheItemCount = hipIndex.Count;
            return hipIndex;
        }
    }
}
