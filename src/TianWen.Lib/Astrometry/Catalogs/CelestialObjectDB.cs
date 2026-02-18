using CsvHelper;
using CsvHelper.Configuration;
using SharpCompress.Readers.Tar;
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
using System.Threading;
using System.Threading.Tasks;
using static TianWen.Lib.Astrometry.Catalogs.CatalogUtils;
using static TianWen.Lib.Astrometry.CoordinateUtils;
using static TianWen.Lib.EnumHelper;

namespace TianWen.Lib.Astrometry.Catalogs;

internal sealed partial class CelestialObjectDB : ICelestialObjectDB
{
    private static readonly Half HalfUndefined = Half.NaN;

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
    private static readonly IReadOnlySet<CatalogIndex> EmptyCatalogIndexSet = ImmutableHashSet.Create<CatalogIndex>();

    private readonly CelestialObject[] _hip2000 = new CelestialObject[120404];
    private readonly Dictionary<CatalogIndex, CelestialObject> _objectsByIndex = new(32000);
    private readonly Dictionary<CatalogIndex, (CatalogIndex i1, CatalogIndex[]? ext)> _crossIndexLookuptable = new(39000);
    private readonly Dictionary<string, (CatalogIndex i1, CatalogIndex[]? ext)> _objectsByCommonName = new(5700);
    private readonly RaDecIndex _raDecIndex = new();
    
    private HashSet<CatalogIndex>? _catalogIndicesCache;
    private HashSet<Catalog>? _completeCatalogCache;
    private volatile int _tycho2DataProcessed;
    private volatile bool _isInitialized;
    private volatile int _hipCacheItemCount;

    public CelestialObjectDB() { }

    public IReadOnlyCollection<string> CommonNames => _objectsByCommonName.Keys;

    public IReadOnlySet<Catalog> Catalogs => GetOrRebuildIndex(ref _completeCatalogCache, RebuildCatalogCache);

    public IReadOnlySet<CatalogIndex> AllObjectIndices => GetOrRebuildIndex(ref _catalogIndicesCache, RebuildObjectIndices);

    public IRaDecIndex CoordinateGrid => _raDecIndex;

    /// <inheritdoc/>
    public bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> matches) => _objectsByCommonName.TryGetLookupEntries(name, out matches);

    private static readonly ImmutableHashSet<Catalog> CrossCats = [
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
        Catalog.vdB,
        Catalog.Tycho2
    ];

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

                if (followedObjs.Count is 1)
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

    public bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlySet<CatalogIndex> crossIndices)
    {
        var alreadyChecked = new HashSet<CatalogIndex>();
        var toCheckList = new List<CatalogIndex>
        {
            catalogIndex
        };

        while (toCheckList.Count > 0)
        {
            var lastIdx = toCheckList.Count - 1;
            var check = toCheckList[lastIdx];
            toCheckList.RemoveAt(lastIdx);

            if (_crossIndexLookuptable.TryGetLookupEntries(check, out var current))
            {
                alreadyChecked.Add(check);

                foreach (var item in current)
                {
                    if (alreadyChecked.Add(item))
                    {
                        toCheckList.Add(item);
                    }
                }
            }
        }

        // remove item to be looked up
        _ = alreadyChecked.Remove(catalogIndex);

        if (alreadyChecked.Count > 0)
        {
            crossIndices = alreadyChecked;
            return true;
        }

        crossIndices = EmptyCatalogIndexSet;
        return false;
    }

    /// <inheritdoc/>
    public async Task<(int Processed, int Failed)> InitDBAsync(CancellationToken cancellationToken)
    {
        if (_isInitialized)
        {
            throw new InvalidOperationException("Already initialized!");
        }

        var assembly = typeof(CelestialObjectDB).Assembly;
        var totalProcessed = 0;
        var totalFailed = 0;

        var initTycho2DataTask = ReadEmbeddedGzippedTycho2BinaryFileAsync(assembly);

        foreach (var predefined in _predefinedObjects)
        {
            var cat = predefined.Key.ToCatalog();

            var commonNames = new HashSet<string>(predefined.Value.CommonNames);
            commonNames.TrimExcess();
            _objectsByIndex[predefined.Key] = new CelestialObject(predefined.Key, predefined.Value.ObjType, double.NaN, double.NaN, 0, HalfUndefined, HalfUndefined, commonNames);
            AddCommonNameIndex(predefined.Key, commonNames);
            totalProcessed++;
        }

        foreach (var csvName in new[] { "NGC", "NGC.addendum" })
        {
            var (processed, failed) = await ReadEmbeddedGzippedCsvDataFileAsync(assembly, csvName, cancellationToken);
            totalProcessed += processed;
            totalFailed += failed;
        }

        (_tycho2DataProcessed, var hipFailed) = await initTycho2DataTask;
        totalProcessed += _tycho2DataProcessed;
        totalFailed += hipFailed;

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
            ("Cl", Catalog.Melotte),
            ("Cl", Catalog.Collinder)
        };
        foreach (var (fileName, catToAdd) in simbadCatalogs)
        {
            var (processed, failed) = await ReadEmbeddedGzippedJsonDataFileAsync(assembly, fileName, catToAdd, cancellationToken);
            totalProcessed += processed;
            totalFailed += failed;
        }

        _isInitialized = true;
        return (totalProcessed, totalFailed);
    }

    private async Task<(int Processed, int Failed)> ReadEmbeddedGzippedTycho2BinaryFileAsync(Assembly assembly)
    {
        var manifestFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith(".tyc2.bin.tar.bz2"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return (0, 0);
        }

        const int entrySize = 14; // update in Get-Tycho2Catalogs.ps1 as well

        await using var tar = await TarReader.OpenAsyncReader(stream, new SharpCompress.Readers.ReaderOptions { LeaveStreamOpen = false });

        if (!Environment.SpecialFolder.LocalApplicationData.TryGetOrCreateAppSubFolder(out var tempDir))
        {
            tempDir = new DirectoryInfo(Path.GetTempFileName()).CreateSubdirectory(SpecialFolderHelper.ApplicationName);
        }

        var tycho2DataDir = tempDir.CreateSubdirectory("tycho2-data");

        bool? upToDate = null;
        while (await tar.MoveToNextEntryAsync())
        {
            var entry = tar.Entry;
            if (entry.Key is { Length: > 0 } name)
            {
                if (entry.IsDirectory)
                {
                    var subDir = tycho2DataDir.CreateSubdirectory(name);
                    if (subDir.CreationTimeUtc > entry.LastModifiedTime)
                    {
                        upToDate ??= true;
                    }
                    else
                    {
                        upToDate = false;
                    }
                }
                else if (entry.Size % entrySize == 0)
                {
                    if (upToDate is true)
                    {
                        break;
                    }
                    var outPath = Path.Combine(tycho2DataDir.FullName, name);
                    await using var outStream = File.Create(outPath, entrySize * 400);
                    await using var entryStream = await tar.OpenEntryStreamAsync();
                    await entryStream.CopyToAsync(outStream);
                }
            }
        }
        return (0, 0);
    }
        /*
        var buffer = new byte[entrySize * 1000];
        int totalEntriesProcessed = 0;

        while (await reader.MoveToNextEntryAsync())
        {
            var entry = reader.Entry;
            if (!entry.IsDirectory && entry.Size > 0 && entry.Size % entrySize == 0)
            {
                await using var entryStream = await reader.OpenEntryStreamAsync();
                int bytesRead;

                while ((bytesRead = await entryStream.ReadAsync(buffer)) > 0)
                {
                    totalEntriesProcessed += bytesRead / entrySize;
                }
            }
        }

        return (totalEntriesProcessed, 0);
    }

    /*
        var chunks = Environment.ProcessorCount;
        await Parallel.ForAsync(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (chunk, cancellationToken) =>
        {
            Span<byte> int32Buffer = stackalloc byte[sizeof(int)];
            int32Buffer[0] = 0;

            var firstIdx = (int)Math.Floor(copiedItems * ((float)chunk / chunks));
            var lastIdx = (int)Math.Min(copiedItems, Math.Ceiling(copiedItems *  ((float)(chunk + 1) / chunks)));

            var entry = buffer.AsSpan()[(firstIdx * entrySize)..];

            for (var idx = firstIdx; idx < lastIdx; idx++, entry = entry[entrySize..])
            {
                var tycId1 = BinaryPrimitives.ReadUInt16BigEndian(entry);
                var tycId2 = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]);
                var tycId3 = entry[4];

                entry[5..8].CopyTo(int32Buffer[1..]);
                var hip = BinaryPrimitives.ReadUInt32BigEndian(int32Buffer);

                entry[8..11].CopyTo(int32Buffer[1..]);
                var hd1 = BinaryPrimitives.ReadUInt32BigEndian(int32Buffer);

                entry[11..14].CopyTo(int32Buffer[1..]);
                var hd2 = BinaryPrimitives.ReadUInt32BigEndian(int32Buffer);

                var ra = BinaryPrimitives.ReadSingleBigEndian(entry[14..]);
                var dec = BinaryPrimitives.ReadSingleBigEndian(entry[18..]);
                // TODO: calc visual mag for Tycho2 entries var vmag = BinaryPrimitives.ReadHalfBigEndian(entry[22..]);
                var catIndex = AbbreviationToCatalogIndex(EncodeTyc2CatalogIndex(Catalog.Tycho2, tycId1, tycId2, tycId3), isBase91Encoded: true);

                var vmag = HalfUndefined;

                // var catIndex = PrefixedNumericToASCIIPackedInt<CatalogIndex>((uint)Catalog.HIP, itemIdx + 1, digits);
                var constellation = ConstellationBoundary.FindConstellation(ra, dec);
            }

            return ValueTask.CompletedTask;
        });
    */

    private async Task<(int Processed, int Failed)> ReadEmbeddedGzippedCsvDataFileAsync(Assembly assembly, string csvName, CancellationToken cancellationToken)
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

        if (!cancellationToken.IsCancellationRequested && !await csvReader.ReadAsync() || !csvReader.ReadHeader())
        {
            return (processed, failed);
        }

        while (!cancellationToken.IsCancellationRequested && await csvReader.ReadAsync())
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
                    && Half.TryParse(vmagStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var vmagFloat)
                    ? vmagFloat
                    : HalfUndefined;

                var surfaceBrightness = csvReader.TryGetField<string>("SurfBr", out var surfaceBrightnessStr)
                    && Half.TryParse(surfaceBrightnessStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var surfaceBrightnessFloat)
                    ? surfaceBrightnessFloat
                    : HalfUndefined;

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

    static readonly Regex ClusterMemberPattern = ClusterMemberPatternGen();

    private async Task<(int Processed, int Failed)> ReadEmbeddedGzippedJsonDataFileAsync(Assembly assembly, string jsonName, Catalog catToAdd, CancellationToken cancellationToken)
    {
        const string NAME_CAT_PREFIX = "NAME ";
        const string NAME_IAU_CAT_PREFIX = "NAME-IAU ";
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

        await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable(gzipStream, SimbadCatalogDtoJsonSerializerContext.Default.SimbadCatalogDto, cancellationToken))
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
                var isCluster = idOrig.StartsWith(CLUSTER_PREFIX, StringComparison.Ordinal);
                var id = isCluster ? idOrig[CLUSTER_PREFIX.Length..] : idOrig;
                if (id.StartsWith(NAME_CAT_PREFIX, StringComparison.Ordinal))
                {
                    commonNames.Add(id[NAME_CAT_PREFIX.Length..].TrimStart());
                }
                else if (id.StartsWith(NAME_IAU_CAT_PREFIX, StringComparison.Ordinal))
                {
                    commonNames.Add(id[NAME_IAU_CAT_PREFIX.Length..].TrimStart());
                }
                else if (id.StartsWith(STAR_CAT_PREFIX, StringComparison.Ordinal))
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

            if (catToAddIdxs.Count > 0)
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
                    if (bestMatches.Count > 0)
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
                            var obj = _objectsByIndex[catToAddIdx] = new CelestialObject(catToAddIdx, objType, raInH, record.Dec, constellation, HalfUndefined, HalfUndefined, trimmedSetOrEmpty);

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
            && !commonNames.SetEquals(obj.CommonNames))
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

    private HashSet<T> GetOrRebuildIndex<T>(ref HashSet<T>? cacheVar, Func<HashSet<T>> rebuildFunc)
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
        var objCount = _objectsByIndex.Count + _crossIndexLookuptable.Count + _tycho2DataProcessed;
        if (objCount > 0 && _tycho2DataProcessed > 0)
        {
            // allow for a changing HIP array (recovered items etc.)
            var index = _catalogIndicesCache is { } cache && _hipCacheItemCount == _tycho2DataProcessed ? cache : HIPIndex();

            index.EnsureCapacity(objCount);
            index.UnionWith(_objectsByIndex.Keys);
            index.UnionWith(_crossIndexLookuptable.Keys);

            return index;
        }

        return [];

        HashSet<CatalogIndex> HIPIndex()
        {
            var hipIndex = new HashSet<CatalogIndex>(_tycho2DataProcessed);
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

    [GeneratedRegex(@"^[A-Za-z]+\s+\d+\s+\d+$", RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex ClusterMemberPatternGen();
}
