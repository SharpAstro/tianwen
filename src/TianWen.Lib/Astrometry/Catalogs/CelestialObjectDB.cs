using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
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
    private readonly Dictionary<CatalogIndex, CelestialObjectShape> _shapesByIndex = new(11000);
    private readonly RaDecIndex _raDecIndex = new();
    internal RaDecIndex PrimaryRaDecIndex => _raDecIndex;

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

    public IRaDecIndex DeepSkyCoordinateGrid => _raDecIndex;

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
        else if (cat is Catalog.HD && !msbSet && TryLookupHDFromTycho2(index, value, out celestialObject))
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

    /// <inheritdoc/>
    public bool TryGetShape(CatalogIndex index, out CelestialObjectShape shape) => _shapesByIndex.TryGetValue(index, out shape);

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
            return (0, 0);
        }

        var assembly = typeof(CelestialObjectDB).Assembly;
        var manifestNames = assembly.GetManifestResourceNames();
        var totalProcessed = 0;
        var totalFailed = 0;

        var initTycho2DataTask = Task.Run(() => ReadEmbeddedTycho2DataAsync(assembly, manifestNames));

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
            var (processed, failed) = ReadEmbeddedLzCsvData(assembly, manifestNames, csvName, cancellationToken);
            totalProcessed += processed;
            totalFailed += failed;
        }

        // Compute mainCatalogs once before SIMBAD processing (avoids re-scanning 13K+ keys per file)
        var mainCatalogs = new HashSet<Catalog>(new HashSet<CatalogIndex>(_objectsByIndex.Keys).Select(idx => idx.ToCatalog()))
        {
            Catalog.HIP,
            Catalog.HD
        };

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
            var (processed, failed) = await ReadEmbeddedLzippedJsonDataFileAsync(assembly, manifestNames, fileName, catToAdd, mainCatalogs, cancellationToken);
            totalProcessed += processed;
            totalFailed += failed;
            mainCatalogs.Add(catToAdd);
        }

        // Wait for Tycho2 data (runs in parallel with CSV + SIMBAD processing)
        await initTycho2DataTask;

        // Load cross-ref multi-json files (modifies shared _crossIndexLookuptable, must be sequential)
        LoadCrossRefMultiJson(assembly, manifestNames, "hip_to_tyc_multi", Catalog.HIP, Catalog.HIP.GetNumericalIndexSize(), _hipToTyc);
        LoadCrossRefMultiJson(assembly, manifestNames, "hd_to_tyc_multi", Catalog.HD, Catalog.HD.GetNumericalIndexSize(), _hdToTyc);

        // Build cross-indices between HD and HIP via shared TYC stars
        // (must happen after all catalog loading so HIP cross-refs to HR, vdB etc. are present)
        BuildHdHipCrossIndicesViaTyc();

        _isInitialized = true;
        return (totalProcessed, totalFailed);
    }

    private async Task ReadEmbeddedTycho2DataAsync(Assembly assembly, string[] manifestNames)
    {
        // 1. Load tyc2.bin.lz binary data
        var tyc2Manifest = manifestNames.FirstOrDefault(p => p.EndsWith(".tyc2.bin.lz"));
        if (tyc2Manifest is null || assembly.GetManifestResourceStream(tyc2Manifest) is not Stream tyc2Stream)
        {
            return;
        }

        _tycho2Data = LzipDecoder.Decompress(tyc2Stream);

        _tycho2StreamCount = BinaryPrimitives.ReadInt32LittleEndian(_tycho2Data);

        // 2. Load GSC region bounding boxes and build spatial index
        var boundsManifest = manifestNames.FirstOrDefault(p => p.EndsWith(".tyc2_gsc_bounds.bin.lz"));
        if (boundsManifest is not null && assembly.GetManifestResourceStream(boundsManifest) is Stream boundsStream)
        {
            var boundsData = LzipDecoder.Decompress(boundsStream);
            _tycho2RaDecIndex = new Tycho2RaDecIndex(_tycho2Data, _tycho2StreamCount, boundsData);
        }

        // 3. Load HIP → TYC cross-reference (binary only; multi-json modifies shared state, done after await)
        _hipToTyc = LoadCrossRefBinFile(assembly, manifestNames, "hip_to_tyc");

        // 4. Load HD → TYC cross-reference (binary only)
        _hdToTyc = LoadCrossRefBinFile(assembly, manifestNames, "hd_to_tyc");
    }

    private void BuildHdHipCrossIndicesViaTyc()
    {
        if (_hipToTyc is not { } hipToTyc || _hdToTyc is not { } hdToTyc)
        {
            throw new InvalidOperationException("HIP→TYC and HD→TYC cross-reference data must be loaded before building HD↔HIP cross-indices.");
        }

        // Build TYC → HIP reverse index
        var tycToHip = new Dictionary<CatalogIndex, CatalogIndex>(hipToTyc.Length / 2);
        for (int i = 0; i < hipToTyc.Length; i++)
        {
            var tycIndex = hipToTyc[i];
            if (tycIndex != 0)
            {
                var hipIndex = PrefixedNumericToASCIIPackedInt<CatalogIndex>((ulong)Catalog.HIP, i + 1, Catalog.HIP.GetNumericalIndexSize());
                tycToHip[tycIndex] = hipIndex;
            }
        }

        // For each HD → TYC, check if the TYC also maps to a HIP; if so, link HD ↔ HIP
        // and propagate to all existing cross-indices of both
        for (int i = 0; i < hdToTyc.Length; i++)
        {
            var tycIndex = hdToTyc[i];
            if (tycIndex != 0 && tycToHip.TryGetValue(tycIndex, out var hipIndex))
            {
                var hdIndex = PrefixedNumericToASCIIPackedInt<CatalogIndex>((ulong)Catalog.HD, i + 1, Catalog.HD.GetNumericalIndexSize());

                // Ensure HD entry is in _objectsByIndex with TYC coordinates for spatial indexing
                if (!_objectsByIndex.ContainsKey(hdIndex) && TryGetTycho2RaDec(tycIndex, out var ra, out var dec, out var vMag)
                    && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
                {
                    var hdObj = new CelestialObject(hdIndex, ObjectType.Star, ra, dec, constellation, vMag, HalfUndefined, EmptyNameSet);
                    _objectsByIndex[hdIndex] = hdObj;
                    AddCommonNameAndPosIndices(hdObj);
                }

                _crossIndexLookuptable.AddLookupEntry(hdIndex, hipIndex);
                _crossIndexLookuptable.AddLookupEntry(hipIndex, hdIndex);

                // Propagate HD to all existing cross-refs of HIP (e.g., HR, vdB)
                if (_crossIndexLookuptable.TryGetLookupEntries(hipIndex, out var hipCross))
                {
                    foreach (var ci in hipCross)
                    {
                        if (ci != hdIndex)
                        {
                            _crossIndexLookuptable.AddLookupEntry(ci, hdIndex);
                            _crossIndexLookuptable.AddLookupEntry(hdIndex, ci);
                        }
                    }
                }
            }
        }
    }

    private void PopulateSimbadStarEntries(Dictionary<Catalog, CatalogIndex[]> relevantIds, Catalog catalog, double raInH, double dec)
    {
        if (relevantIds.TryGetValue(catalog, out var ids))
        {
            foreach (var id in ids)
            {
                if (!_objectsByIndex.ContainsKey(id)
                    && ConstellationBoundary.TryFindConstellation(raInH, dec, out var constellation))
                {
                    var obj = new CelestialObject(id, ObjectType.Star, raInH, dec, constellation, HalfUndefined, HalfUndefined, EmptyNameSet);
                    _objectsByIndex[id] = obj;
                    AddCommonNameAndPosIndices(obj);
                }
            }
        }
    }

    private static CatalogIndex[]? LoadCrossRefBinFile(Assembly assembly, string[] manifestNames, string name)
    {
        var manifestFileName = manifestNames.FirstOrDefault(p => p.EndsWith("." + name + ".bin.lz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return null;
        }

        var data = LzipDecoder.Decompress(stream);

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

    private void LoadCrossRefMultiJson(Assembly assembly, string[] manifestNames, string name, Catalog catalog, int digits, CatalogIndex[]? crossRefArray)
    {
        var manifestFileName = manifestNames.FirstOrDefault(p => p.EndsWith("." + name + ".json.lz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return;
        }

        using (stream)
        using (var decompressed = LzipDecoder.DecompressToStream(stream))
        using (var doc = JsonDocument.Parse(decompressed))
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

                        _crossIndexLookuptable.AddLookupEntry(tycIndex, catalogIndex);
                        _crossIndexLookuptable.AddLookupEntry(catalogIndex, tycIndex);
                    }
                }
            }
        }
    }

    private bool TryGetTycho2RaDec(CatalogIndex tycIndex, out double ra, out double dec, out Half vMag)
    {
        ra = dec = 0;
        vMag = HalfUndefined;
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
        return TryGetTycho2RaDec(tyc1, (ushort)tyc2, tyc3, out ra, out dec, out vMag);
    }

    /// <summary>
    /// Reads a Tycho-2 star entry from the binary catalog data.
    /// <para>
    /// Each entry is 13 bytes, packed as:
    /// <list type="table">
    /// <listheader><term>Offset</term><term>Size</term><description>Field</description></listheader>
    /// <item><term>0</term><term>2</term><description>TYC2 (UInt16 LE) — running number within GSC region</description></item>
    /// <item><term>2</term><term>1</term><description>TYC3 (byte) — component identifier (normally 1)</description></item>
    /// <item><term>3</term><term>4</term><description>RA (float LE) — Right Ascension in hours [0, 24), J2000</description></item>
    /// <item><term>7</term><term>4</term><description>Dec (float LE) — Declination in degrees [-90, +90], J2000</description></item>
    /// <item><term>11</term><term>1</term><description>VTmag decimag — Tycho-2 VT magnitude encoded as <c>clamp(round(mag × 10) + 20, 0, 254)</c>; 0xFF = missing</description></item>
    /// <item><term>12</term><term>1</term><description>BTmag decimag — Tycho-2 BT magnitude encoded identically; 0xFF = missing</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Johnson photometry can be derived from VT and BT magnitudes:
    /// <c>V = VT − 0.090 × (BT − VT)</c> and <c>B−V = 0.850 × (BT − VT)</c>.
    /// </para>
    /// <para>
    /// The TYC1 (GSC region number, 1-based) determines which stream partition to search.
    /// Entries within a partition are stored contiguously and located via the offset table
    /// at the start of the binary file (int32 LE per stream).
    /// </para>
    /// </summary>
    private bool TryGetTycho2RaDec(ushort tyc1, ushort tyc2, byte tyc3, out double ra, out double dec, out Half vMag)
    {
        ra = dec = 0;
        vMag = HalfUndefined;
        if (_tycho2Data is null || tyc1 == 0 || tyc1 > _tycho2StreamCount)
        {
            return false;
        }

        const int entrySize = 13;
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
                vMag = DecodeJohnsonVFromDecimags(entry[11], entry[12]);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decodes Johnson V magnitude from biased decimag-encoded VT and BT bytes.
    /// <para>
    /// Decimag encoding: <c>byte = clamp(round(mag × 10) + 20, 0, 254)</c>, 0xFF = missing.
    /// Decoding: <c>mag = (byte − 20) / 10.0</c>.
    /// </para>
    /// <para>
    /// If both VT and BT are available, computes Johnson V = VT − 0.090 × (BT − VT).
    /// If only VT is available, returns VT as an approximation.
    /// If VT is missing, returns <see cref="Half.NaN"/>.
    /// </para>
    /// </summary>
    private static Half DecodeJohnsonVFromDecimags(byte vtDecimag, byte btDecimag)
    {
        if (vtDecimag == 0xFF)
        {
            return HalfUndefined;
        }

        var vt = (vtDecimag - 20) / 10.0;

        if (btDecimag != 0xFF)
        {
            var bt = (btDecimag - 20) / 10.0;
            return (Half)(vt - 0.090 * (bt - vt));
        }

        return (Half)vt;
    }

    private bool TryLookupHIPFromTycho2(CatalogIndex hipIndex, ulong hipValue, out CelestialObject celestialObject)
    {
        celestialObject = default;
        var hipNumber = (int)EnumValueToNumeric(hipValue);

        if (_hipToTyc is not null && hipNumber > 0 && hipNumber <= _hipToTyc.Length)
        {
            var tycIndex = _hipToTyc[hipNumber - 1];
            if (tycIndex != 0 && TryGetTycho2RaDec(tycIndex, out var ra, out var dec, out var vMag)
                && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
            {
                celestialObject = new CelestialObject(hipIndex, ObjectType.Star, ra, dec, constellation, vMag, HalfUndefined, EmptyNameSet);
                return true;
            }
        }

        return false;
    }

    private bool TryLookupHDFromTycho2(CatalogIndex hdIndex, ulong hdValue, out CelestialObject celestialObject)
    {
        celestialObject = default;
        var hdNumber = (int)EnumValueToNumeric(hdValue);

        if (_hdToTyc is not null && hdNumber > 0 && hdNumber <= _hdToTyc.Length)
        {
            var tycIndex = _hdToTyc[hdNumber - 1];
            if (tycIndex != 0 && TryGetTycho2RaDec(tycIndex, out var ra, out var dec, out var vMag)
                && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
            {
                celestialObject = new CelestialObject(hdIndex, ObjectType.Star, ra, dec, constellation, vMag, HalfUndefined, EmptyNameSet);
                return true;
            }
        }

        return false;
    }

    private bool TryLookupTycho2StarFromBinaryData(CatalogIndex tycIndex, ulong decodedValue, out CelestialObject celestialObject)
    {
        celestialObject = default;
        var (tyc1, tyc2, tyc3) = DecodeTyc2CatalogIndex(decodedValue);

        if (TryGetTycho2RaDec(tyc1, (ushort)tyc2, (byte)tyc3, out var ra, out var dec, out var vMag)
            && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
        {
            celestialObject = new CelestialObject(tycIndex, ObjectType.Star, ra, dec, constellation, vMag, HalfUndefined, EmptyNameSet);
            return true;
        }

        return false;
    }

    private (int Processed, int Failed) ReadEmbeddedLzCsvData(Assembly assembly, string[] manifestNames, string csvName, CancellationToken cancellationToken)
    {
        const string NGC = nameof(NGC);
        const string IC = nameof(IC);
        const string M = nameof(M);

        int processed = 0;
        int failed = 0;
        var manifestFileName = manifestNames.FirstOrDefault(p => p.EndsWith("." + csvName + ".csv.lz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return (processed, failed);
        }

        using var decompressed = LzipDecoder.DecompressToStream(stream);
        var csvText = decompressed.TryGetBuffer(out var buffer) && buffer is { Array.Length: > 0 }
            ? new UTF8Encoding(false).GetString(buffer.Array, buffer.Offset, buffer.Count)
            : new UTF8Encoding(false).GetString(decompressed.ToArray());
        var csvReader = new CsvFieldReader(csvText, ';');

        while (!cancellationToken.IsCancellationRequested && csvReader.Read())
        {
            if (csvReader.TryGetFieldString("Name", out var entryName)
                && csvReader.TryGetField("Type", out var objectTypeAbbr)
                && objectTypeAbbr is { Length: > 0 }
                && csvReader.TryGetField("RA", out var raHMS)
                && csvReader.TryGetField("Dec", out var decDMS)
                && csvReader.TryGetFieldString("Const", out var constAbbr)
                && TryGetCleanedUpCatalogName(entryName, out var indexEntry)
            )
            {
                var objectType = AbbreviationToEnumMember<OpenNGCObjectType>(objectTypeAbbr.ToString()).ToObjectType();
                var @const = AbbreviationToEnumMember<Constellation>(constAbbr);

                var vmag = csvReader.TryGetField("V-Mag", out var vmagSpan)
                    && Half.TryParse(vmagSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var vmagFloat)
                    ? vmagFloat
                    : HalfUndefined;

                var surfaceBrightness = csvReader.TryGetField("SurfBr", out var surfaceBrightnessSpan)
                    && Half.TryParse(surfaceBrightnessSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var surfaceBrightnessFloat)
                    ? surfaceBrightnessFloat
                    : HalfUndefined;

                IReadOnlySet<string> commonNames;
                if (csvReader.TryGetFieldString("Common names", out var commonNamesEntry) && !string.IsNullOrWhiteSpace(commonNamesEntry))
                {
                    commonNames = new HashSet<string>(commonNamesEntry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else
                {
                    commonNames = EmptyNameSet;
                }

                if (csvReader.TryGetField("MajAx", out var majAxSpan)
                    && Half.TryParse(majAxSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var majAx)
                    && csvReader.TryGetField("MinAx", out var minAxSpan)
                    && Half.TryParse(minAxSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var minAx))
                {
                    var posAng = csvReader.TryGetField("PosAng", out var posAngSpan)
                        && Half.TryParse(posAngSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var posAngFloat)
                        ? posAngFloat
                        : HalfUndefined;

                    _shapesByIndex[indexEntry] = new CelestialObjectShape(majAx, minAx, posAng);
                }

                var ra = HMSToHours(raHMS.ToString());
                var dec = DMSToDegree(decDMS.ToString());
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

                if (objectType == ObjectType.Duplicate)
                {
                    // Duplicates are stored in _objectsByIndex for cross-reference resolution
                    // but NOT added to the spatial grid — only the primary entry belongs there.
                    AddCommonNameIndex(obj.Index, obj.CommonNames);
                    // when the entry is a duplicate, use the cross lookup table to list the entries it duplicates
                    if (csvReader.TryGetFieldString(NGC, out var ngcSuffix) && TryGetCleanedUpCatalogName(NGC + ngcSuffix, out var ngcIndexEntry))
                    {
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, ngcIndexEntry);
                    }
                    if (csvReader.TryGetFieldString(M, out var messierSuffix) && TryGetCleanedUpCatalogName(M + messierSuffix, out var messierIndexEntry))
                    {
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, messierIndexEntry);
                    }
                    if (csvReader.TryGetFieldString(IC, out var icSuffix) && TryGetCleanedUpCatalogName(IC + icSuffix, out var icIndexEntry))
                    {
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, icIndexEntry);
                    }
                }
                else
                {
                    AddCommonNameAndPosIndices(obj);

                    if (csvReader.TryGetFieldString(IC, out var icSuffix) && TryGetCleanedUpCatalogName(IC + icSuffix, out var icIndexEntry) && indexEntry != icIndexEntry)
                    {
                        _crossIndexLookuptable.AddLookupEntry(icIndexEntry, indexEntry);
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, icIndexEntry);
                    }
                    if (csvReader.TryGetFieldString(M, out var messierSuffix) && TryGetCleanedUpCatalogName(M + messierSuffix, out var messierIndexEntry) && indexEntry != messierIndexEntry)
                    {
                        // Adds Messier to NGC/IC entry lookup, but only if its not a duplicate
                        _crossIndexLookuptable.AddLookupEntry(messierIndexEntry, indexEntry);
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, messierIndexEntry);
                        AddCommonNameIndex(messierIndexEntry, commonNames);
                    }

                    if (csvReader.TryGetFieldString("Identifiers", out var identifiersEntry) && identifiersEntry is { Length: > 0 })
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
    }

    static readonly Regex ClusterMemberPattern = ClusterMemberPatternGen();

    private async Task<(int Processed, int Failed)> ReadEmbeddedLzippedJsonDataFileAsync(Assembly assembly, string[] manifestNames, string jsonName, Catalog catToAdd, HashSet<Catalog> mainCatalogs, CancellationToken cancellationToken)
    {
        const string NAME_CAT_PREFIX = "NAME ";
        const string NAME_IAU_CAT_PREFIX = "NAME-IAU ";
        const string STAR_CAT_PREFIX = "* ";
        const string CLUSTER_PREFIX = "Cl ";

        var processed = 0;
        var failed = 0;

        var manifestFileName = manifestNames.FirstOrDefault(p => p.EndsWith("." + jsonName + ".json.lz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return (processed, failed);
        }

        using var decompressed = LzipDecoder.DecompressToStream(stream);

        // Reuse collections across records to reduce allocations
        var catToAddIdxs = new SortedSet<CatalogIndex>();
        var relevantIds = new Dictionary<Catalog, CatalogIndex[]>();
        var commonNames = new HashSet<string>(8);

        await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable(decompressed, SimbadCatalogDtoJsonSerializerContext.Default.SimbadCatalogDto, cancellationToken))
        {
            if (record is null)
            {
                continue;
            }

            catToAddIdxs.Clear();
            relevantIds.Clear();
            commonNames.Clear();
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

            if (catToAddIdxs.Count > 0)
            {
                // Populate HIP/HD entries from Simbad data with authoritative coordinates
                var raInHForStars = record.Ra / 15;
                PopulateSimbadStarEntries(relevantIds, Catalog.HIP, raInHForStars, record.Dec);
                PopulateSimbadStarEntries(relevantIds, Catalog.HD, raInHForStars, record.Dec);

                var bestMatches = (
                    from relevantIdPerCat in relevantIds
                    from relevantId in relevantIdPerCat.Value
                    where TryLookupByIndexDirect(relevantId, out _, out _, out _)
                    let sortKey = relevantIdPerCat.Key switch
                    {
                        Catalog.NGC => 1u,
                        Catalog.IC => 2u,
                        Catalog.Messier => 3u,
                        Catalog.HIP => uint.MaxValue - 1,
                        Catalog.HD => uint.MaxValue,
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
                                // Register bestMatch common names first so main catalog entries (NGC, etc.) are v1
                                UpdateObjectCommonNames(bestMatch, commonNames);
                            }
                        }

                        // Associate catToAddIdx with common names (appended after bestMatches)
                        // Use the bestMatch object's names since UpdateObjectCommonNames may have consumed commonNames
                        if (_objectsByIndex[bestMatches[0]].CommonNames is { Count: > 0 } bestMatchNames)
                        {
                            AddCommonNameIndex(catToAddIdx, bestMatchNames);
                        }

                        // Also link non-bestMatch relevantIds (e.g. HD without TYC) to catToAddIdx and bestMatches
                        // Only cross-link entries from cross-ref catalogs to avoid phantom entries (e.g. NGC 1502A)
                        foreach (var relevantIdPerCat in relevantIds)
                        {
                            if (!IsCrossCat(relevantIdPerCat.Key))
                            {
                                continue;
                            }

                            foreach (var relevantId in relevantIdPerCat.Value)
                            {
                                if (!bestMatches.Contains(relevantId))
                                {
                                    _crossIndexLookuptable.AddLookupEntry(relevantId, catToAddIdx);
                                    _crossIndexLookuptable.AddLookupEntry(catToAddIdx, relevantId);

                                    foreach (var bestMatch in bestMatches)
                                    {
                                        _crossIndexLookuptable.AddLookupEntry(relevantId, bestMatch);
                                        _crossIndexLookuptable.AddLookupEntry(bestMatch, relevantId);
                                    }
                                }
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
                            var setOrEmpty = commonNames.Count > 0 ? new HashSet<string>(commonNames) : EmptyNameSet;
                            var obj = _objectsByIndex[catToAddIdx] = new CelestialObject(catToAddIdx, objType, raInH, record.Dec, constellation, HalfUndefined, HalfUndefined, setOrEmpty);

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
