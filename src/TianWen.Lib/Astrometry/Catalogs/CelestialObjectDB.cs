using CsvHelper;
using CsvHelper.Configuration;
using SharpCompress.Compressors.LZMA;
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

    private readonly Dictionary<CatalogIndex, CelestialObject> _objectsByIndex = new(32000);
    private readonly Dictionary<CatalogIndex, (CatalogIndex i1, CatalogIndex[]? ext)> _crossIndexLookuptable = new(39000);
    private readonly Dictionary<string, (CatalogIndex i1, CatalogIndex[]? ext)> _objectsByCommonName = new(5700);
    private readonly RaDecIndex _raDecIndex = new();

    private byte[]? _tycho2Data;
    private int _tycho2StreamCount;
    private Tycho2RaDecIndex? _tycho2RaDecIndex;
    private CatalogIndex[]? _hipToTyc;
    private CatalogIndex[]? _hdToTyc;

    private HashSet<CatalogIndex>? _catalogIndicesCache;
    private HashSet<Catalog>? _completeCatalogCache;
    private volatile bool _isInitialized;

    public CelestialObjectDB() { }

    public IReadOnlyCollection<string> CommonNames => _objectsByCommonName.Keys;

    public IReadOnlySet<Catalog> Catalogs => GetOrRebuildIndex(ref _completeCatalogCache, RebuildCatalogCache);

    public IReadOnlySet<CatalogIndex> AllObjectIndices => GetOrRebuildIndex(ref _catalogIndicesCache, RebuildObjectIndices);

    public IRaDecIndex CoordinateGrid => new CompositeRaDecIndex(_raDecIndex, _tycho2RaDecIndex);

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
        Catalog.vdB
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IsCrossCat(Catalog cat) => CrossCats.Contains(cat);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool TryLookupByIndexDirect(CatalogIndex index, [NotNullWhen(true)] out CelestialObject celestialObject, out Catalog cat, out int? arrayIndex)
    {
        (cat, var value, var msbSet) = index.ToCatalogAndValue();
        arrayIndex = null;

        if (_objectsByIndex.TryGetValue(index, out celestialObject))
        {
            return true;
        }
        else if (cat is Catalog.HIP && !msbSet && TryLookupHIPFromTycho2(index, value, out celestialObject))
        {
            return true;
        }
        else if (cat is Catalog.Tycho2 && msbSet && TryLookupTycho2StarFromBinaryData(index, value, out celestialObject))
        {
            return true;
        }

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

        var initTycho2DataTask = ReadEmbeddedTycho2DataAsync(assembly);

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

        await initTycho2DataTask;

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
            var (processed, failed) = await ReadEmbeddedLzippedJsonDataFileAsync(assembly, fileName, catToAdd, cancellationToken);
            totalProcessed += processed;
            totalFailed += failed;
        }

        _isInitialized = true;
        return (totalProcessed, totalFailed);
    }

    private async Task ReadEmbeddedTycho2DataAsync(Assembly assembly)
    {
        // 1. Load tyc2.bin.lz binary data
        var tyc2Manifest = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith(".tyc2.bin.lz"));
        if (tyc2Manifest is null || assembly.GetManifestResourceStream(tyc2Manifest) is not Stream tyc2Stream)
        {
            return;
        }

        using (var lzipStream = new LZipStream(tyc2Stream, SharpCompress.Compressors.CompressionMode.Decompress))
        {
            var sizeInfo = ReadLzSizeInfo(assembly, tyc2Manifest);
            if (sizeInfo is var (uncompressedSize, _))
            {
                _tycho2Data = new byte[uncompressedSize];
                await lzipStream.ReadExactlyAsync(_tycho2Data);
            }
            else
            {
                var tycMs = new MemoryStream(capacity: (int)tyc2Stream.Length * 4);
                await lzipStream.CopyToAsync(tycMs);
                _tycho2Data = tycMs.ToArray();
            }
        }

        _tycho2StreamCount = BinaryPrimitives.ReadInt32LittleEndian(_tycho2Data);

        // 2. Load GSC region bounding boxes and build spatial index
        var boundsManifest = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith(".tyc2_gsc_bounds.bin.lz"));
        if (boundsManifest is not null && assembly.GetManifestResourceStream(boundsManifest) is Stream boundsStream)
        {
            using var boundsLzip = new LZipStream(boundsStream, SharpCompress.Compressors.CompressionMode.Decompress);
            var boundsSize = _tycho2StreamCount * 16; // 4 × float32 per GSC region
            var boundsData = new byte[boundsSize];
            await boundsLzip.ReadExactlyAsync(boundsData);
            _tycho2RaDecIndex = new Tycho2RaDecIndex(_tycho2Data, _tycho2StreamCount, boundsData);
        }

        // 3. Load HIP → TYC cross-reference
        _hipToTyc = LoadCrossRefBinFile(assembly, "hip_to_tyc");
        LoadCrossRefMultiJson(assembly, "hip_to_tyc_multi", Catalog.HIP, Catalog.HIP.GetNumericalIndexSize(), _hipToTyc);

        // 4. Load HD → TYC cross-reference
        _hdToTyc = LoadCrossRefBinFile(assembly, "hd_to_tyc");
        LoadCrossRefMultiJson(assembly, "hd_to_tyc_multi", Catalog.HD, Catalog.HD.GetNumericalIndexSize(), _hdToTyc);
    }

    private static CatalogIndex[]? LoadCrossRefBinFile(Assembly assembly, string name)
    {
        var manifestFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith("." + name + ".bin.lz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return null;
        }

        using var lzipStream = new LZipStream(stream, SharpCompress.Compressors.CompressionMode.Decompress);
        byte[] data;
        var sizeInfo = ReadLzSizeInfo(assembly, manifestFileName);
        if (sizeInfo is var (uncompressedSize, _))
        {
            data = new byte[uncompressedSize];
            lzipStream.ReadExactly(data);
        }
        else
        {
            var ms = new MemoryStream(capacity: (int)stream.Length * 4);
            lzipStream.CopyTo(ms);
            data = ms.ToArray();
        }

        const int recordSize = 5;
        var count = data.Length / recordSize;
        var result = new CatalogIndex[count];

        for (int i = 0; i < count; i++)
        {
            var offset = i * recordSize;
            var tyc1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            var tyc2 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
            var tyc3 = data[offset + 4];

            if (tyc1 != 0 || tyc2 != 0 || tyc3 != 0)
            {
                var encoded = EncodeTyc2CatalogIndex(Catalog.Tycho2, tyc1, tyc2, tyc3);
                result[i] = AbbreviationToCatalogIndex(encoded, isBase91Encoded: true);
            }
        }

        return result;
    }

    private static (int UncompressedSize, int EntryCount)? ReadLzSizeInfo(Assembly assembly, string lzManifestName)
    {
        var sizeManifest = lzManifestName + ".size";
        if (assembly.GetManifestResourceStream(sizeManifest) is not Stream sizeStream)
        {
            return null;
        }

        using (sizeStream)
        {
            Span<byte> buf = stackalloc byte[8];
            sizeStream.ReadExactly(buf);
            var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(buf);
            var entryCount = BinaryPrimitives.ReadInt32LittleEndian(buf[4..]);
            return (uncompressedSize, entryCount);
        }
    }

    private static void LoadCrossRefMultiJson(Assembly assembly, string name, Catalog catalog, int digits, CatalogIndex[]? crossRefArray)
    {
        var manifestFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith("." + name + ".json.lz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return;
        }

        using (stream)
        using (var lzipStream = new LZipStream(stream, SharpCompress.Compressors.CompressionMode.Decompress))
        using (var doc = JsonDocument.Parse(lzipStream))
        {
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
                {
                    continue;
                }

                var catalogIndex = PrefixedNumericToASCIIPackedInt<CatalogIndex>((ulong)catalog, number, digits);

                foreach (var tycStr in property.Value.EnumerateArray())
                {
                    var parts = tycStr.GetString()?.Split('-');
                    if (parts is { Length: 3 }
                        && ushort.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var tyc1)
                        && ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tyc2)
                        && byte.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var tyc3))
                    {
                        var encoded = EncodeTyc2CatalogIndex(Catalog.Tycho2, tyc1, tyc2, tyc3);
                        var tycIndex = AbbreviationToCatalogIndex(encoded, isBase91Encoded: true);

                        if (crossRefArray is not null && number <= crossRefArray.Length && crossRefArray[number - 1] == 0)
                        {
                            crossRefArray[number - 1] = tycIndex;
                        }
                    }
                }
            }
        }
    }

    private bool TryGetTycho2RaDec(CatalogIndex tycIndex, out double ra, out double dec)
    {
        ra = dec = 0;
        if (_tycho2Data is null)
        {
            return false;
        }

        var (cat, value, _) = tycIndex.ToCatalogAndValue();
        if (cat != Catalog.Tycho2)
        {
            return false;
        }

        var (tyc1, tyc2, tyc3) = DecodeTyc2CatalogIndex(value);
        return TryGetTycho2RaDec(tyc1, (ushort)tyc2, tyc3, out ra, out dec);
    }

    private bool TryGetTycho2RaDec(ushort tyc1, ushort tyc2, byte tyc3, out double ra, out double dec)
    {
        ra = dec = 0;
        if (_tycho2Data is null || tyc1 == 0 || tyc1 > _tycho2StreamCount)
        {
            return false;
        }

        const int entrySize = 11;
        var data = _tycho2Data.AsSpan();

        var gscIdx = tyc1 - 1;
        var startOffset = BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 1) * 4)..]);
        var endOffset = gscIdx + 1 < _tycho2StreamCount
            ? BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 2) * 4)..])
            : _tycho2Data.Length;

        var entryCount = (endOffset - startOffset) / entrySize;

        for (int i = 0; i < entryCount; i++)
        {
            var entry = data[(startOffset + i * entrySize)..];
            var entryTyc2 = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            var entryTyc3 = entry[2];

            if (entryTyc2 == tyc2 && entryTyc3 == tyc3)
            {
                ra = BinaryPrimitives.ReadSingleLittleEndian(entry[3..]);
                dec = BinaryPrimitives.ReadSingleLittleEndian(entry[7..]);
                return true;
            }
        }

        return false;
    }

    private bool TryLookupHIPFromTycho2(CatalogIndex hipIndex, ulong hipValue, out CelestialObject celestialObject)
    {
        celestialObject = default;
        var hipNumber = (int)EnumValueToNumeric(hipValue);

        if (_hipToTyc is not null && hipNumber > 0 && hipNumber <= _hipToTyc.Length)
        {
            var tycIndex = _hipToTyc[hipNumber - 1];
            if (tycIndex != 0 && TryGetTycho2RaDec(tycIndex, out var ra, out var dec)
                && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
            {
                celestialObject = new CelestialObject(hipIndex, ObjectType.Star, ra, dec, constellation, HalfUndefined, HalfUndefined, EmptyNameSet);
                return true;
            }
        }

        return false;
    }

    private bool TryLookupTycho2StarFromBinaryData(CatalogIndex tycIndex, ulong decodedValue, out CelestialObject celestialObject)
    {
        celestialObject = default;
        var (tyc1, tyc2, tyc3) = DecodeTyc2CatalogIndex(decodedValue);

        if (TryGetTycho2RaDec(tyc1, (ushort)tyc2, (byte)tyc3, out var ra, out var dec)
            && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
        {
            celestialObject = new CelestialObject(tycIndex, ObjectType.Star, ra, dec, constellation, HalfUndefined, HalfUndefined, EmptyNameSet);
            return true;
        }

        return false;
    }

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

    private async Task<(int Processed, int Failed)> ReadEmbeddedLzippedJsonDataFileAsync(Assembly assembly, string jsonName, Catalog catToAdd, CancellationToken cancellationToken)
    {
        const string NAME_CAT_PREFIX = "NAME ";
        const string NAME_IAU_CAT_PREFIX = "NAME-IAU ";
        const string STAR_CAT_PREFIX = "* ";
        const string CLUSTER_PREFIX = "Cl ";

        var processed = 0;
        var failed = 0;

        var manifestFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith("." + jsonName + ".json.lz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return (processed, failed);
        }

        using var lzipStream = new LZipStream(stream, SharpCompress.Compressors.CompressionMode.Decompress);

        // will not be using cache as initialization is not complete
        var mainCatalogs = new HashSet<Catalog>(new HashSet<CatalogIndex>(_objectsByIndex.Keys).Select(idx => idx.ToCatalog()))
        {
            Catalog.HIP
        };

        await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable(lzipStream, SimbadCatalogDtoJsonSerializerContext.Default.SimbadCatalogDto, cancellationToken))
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
                // Populate HIP entries from Simbad data with authoritative coordinates
                if (relevantIds.TryGetValue(Catalog.HIP, out var hipIds))
                {
                    var raInH = record.Ra / 15;
                    foreach (var hipId in hipIds)
                    {
                        if (!_objectsByIndex.ContainsKey(hipId)
                            && ConstellationBoundary.TryFindConstellation(raInH, record.Dec, out var hipConst))
                        {
                            var hipObj = new CelestialObject(hipId, ObjectType.Star, raInH, record.Dec, hipConst, HalfUndefined, HalfUndefined, EmptyNameSet);
                            _objectsByIndex[hipId] = hipObj;
                            AddCommonNameAndPosIndices(hipObj);
                        }
                    }
                }

                var bestMatches = (
                    from relevantIdPerCat in relevantIds
                    from relevantId in relevantIdPerCat.Value
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
        if (TryLookupByIndexDirect(catIdx, out var obj, out _, out _)
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

            _objectsByIndex[catIdx] = modObj;

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
        var objCount = _objectsByIndex.Count + _crossIndexLookuptable.Count;
        if (objCount > 0)
        {
            var index = new HashSet<CatalogIndex>(objCount);
            index.UnionWith(_objectsByIndex.Keys);
            index.UnionWith(_crossIndexLookuptable.Keys);
            return index;
        }

        return [];
    }

    [GeneratedRegex(@"^[A-Za-z]+\s+\d+\s+\d+$", RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex ClusterMemberPatternGen();
}
