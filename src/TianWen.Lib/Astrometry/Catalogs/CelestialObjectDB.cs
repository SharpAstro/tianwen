using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Lzip;
using static TianWen.Lib.Astrometry.Catalogs.CatalogUtils;
using static TianWen.Lib.Astrometry.CoordinateUtils;
using static TianWen.Lib.EnumHelper;

namespace TianWen.Lib.Astrometry.Catalogs;

internal sealed partial class CelestialObjectDB : ICelestialObjectDB
{
    private static readonly Half HalfUndefined = Half.NaN;

    // B-V colour indices from published photometry (approximate for extended bodies).
    // Used by the sky-map planet renderer to give each body its characteristic colour.
    private static readonly Dictionary<CatalogIndex, (ObjectType ObjType, string[] CommonNames, Half VMag, Half BVColor)> _predefinedObjects = new()
    {
        [CatalogIndex.Sol]     = (ObjectType.Star,    ["Sun", "Sol"],    (Half)(-26.74), (Half)0.65),
        [CatalogIndex.Mercury] = (ObjectType.Planet,  ["Mercury"],       (Half)(-0.36),  (Half)0.93),
        [CatalogIndex.Venus]   = (ObjectType.Planet,  ["Venus"],         (Half)(-4.14),  (Half)0.82),
        [CatalogIndex.Earth]   = (ObjectType.Planet,  ["Earth"],         HalfUndefined,  (Half)0.2),
        [CatalogIndex.Moon]    = (ObjectType.Planet,  ["Moon", "Luna"],  (Half)(-12.7),  (Half)0.92),
        [CatalogIndex.Mars]    = (ObjectType.Planet,  ["Mars"],          (Half)(1.84),   (Half)1.36),
        [CatalogIndex.Jupiter] = (ObjectType.Planet,  ["Jupiter"],       (Half)(-2.20),  (Half)0.83),
        [CatalogIndex.Saturn]  = (ObjectType.Planet,  ["Saturn"],        (Half)(0.46),   (Half)1.04),
        [CatalogIndex.Uranus]  = (ObjectType.Planet,  ["Uranus"],        (Half)(5.38),   (Half)0.56),
        [CatalogIndex.Neptune] = (ObjectType.Planet,  ["Neptune"],       (Half)(7.67),   (Half)0.41),
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

    /// <summary>
    /// Exact-pm overrides for the ~11 Tycho-2 stars whose |pmRA| or |pmDec| exceeds
    /// the int16 x 10 inline encoding's range of +/-3276.7 mas/yr (Barnard's,
    /// Kapteyn's, etc.). Built once from <c>tyc2_pm_sidecar.bin.lz</c> during the
    /// bulk load. Key packs (tyc1, tyc2, tyc3) into a long; value is exact
    /// (pmRa, pmDec) in tenth-mas/yr (= source mas/yr * 10) as int32 -- this
    /// covers Barnard's 10277 mas/yr losslessly. Null until
    /// <see cref="ReadTycho2Bulk"/> populates.
    /// </summary>
    private Dictionary<long, (int PmRaTenthMasPerYr, int PmDecTenthMasPerYr)>? _tycho2PmSidecar;

    /// <summary>Packs a TYC identifier into the long key used by <see cref="_tycho2PmSidecar"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackTycKey(ushort tyc1, ushort tyc2, byte tyc3)
        => ((long)tyc1 << 24) | ((long)tyc2 << 8) | tyc3;
    private CatalogIndex[]? _hipToTyc;
    private CatalogIndex[]? _hdToTyc;

    /// <summary>
    /// Background task that decompresses the ~21 MB <c>tyc2.bin.lz</c> + bounds and builds
    /// <see cref="_tycho2RaDecIndex"/>. Kicked off during init, intentionally NOT awaited
    /// before <see cref="InitDBAsync"/> returns (unless the caller passes
    /// <c>waitForTycho2BulkLoad: true</c>). Runtime callers that touch <see cref="_tycho2Data"/>
    /// or <see cref="_tycho2RaDecIndex"/> must <c>await</c> <see cref="EnsureTycho2DataLoadedAsync"/>
    /// first. Set exactly once per instance — re-entry into <see cref="InitDBAsync"/> after
    /// successful init returns early without restarting it.
    /// </summary>
    private Task? _tycho2BulkLoadTask;

    private HashSet<CatalogIndex>? _catalogIndicesCache;
    private HashSet<Catalog>? _completeCatalogCache;
    private volatile bool _isInitialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <inheritdoc/>
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Background bake of the sorted ordinal-ignore-case auto-complete list, kicked off
    /// during init as soon as <see cref="_objectsByIndex"/>, <see cref="_crossIndexLookuptable"/>,
    /// and <see cref="_objectsByCommonName"/> are fully populated (i.e. after the hd-hip-cross
    /// phase — Tycho-2 bulk does not contribute names so we don't have to wait for it).
    /// Costs ~300-600 ms on the ~400-600K entries the sky-map search modal binary-searches.
    /// Doing it here pulls the cost off the first F3-keystroke critical path: by the time the
    /// user opens search, the result is almost always already there.
    /// </summary>
    private Task<string[]>? _autoCompleteListTask;

    /// <summary>
    /// Per-phase wall-clock timings captured during the last <see cref="InitDBAsync"/>
    /// call. Ordered by completion time. Populated fresh on every init; useful for
    /// benchmarks and diagnostics. Empty before first init.
    /// </summary>
    public IReadOnlyList<(string Phase, TimeSpan Elapsed)> LastInitPhaseTimings => _lastInitPhaseTimings;
    private readonly List<(string Phase, TimeSpan Elapsed)> _lastInitPhaseTimings = new(24);

    /// <summary>Entry count successfully processed in the last <see cref="InitDBAsync"/>.</summary>
    public int LastInitProcessed { get; private set; }

    /// <summary>Entry count that failed to parse in the last <see cref="InitDBAsync"/>.</summary>
    public int LastInitFailed { get; private set; }

    public CelestialObjectDB() { }

    /// <summary>
    /// Build-time hook for <c>tools/precompute-hd-hip-cross</c>. When true, init forces the
    /// live <see cref="BuildHdHipCrossIndicesViaTyc"/> path (skipping any embedded snapshot)
    /// and captures the result into <see cref="LastHdHipCrossSnapshot"/> for serialisation.
    /// Has no effect at runtime where this flag stays false.
    /// </summary>
    internal bool ForceLiveHdHipCrossWithCapture { get; init; }

    /// <summary>
    /// Build-time hook for <c>tools/precompute-simbad-merge</c>. When true, init forces the
    /// live SIMBAD merge loop (skipping any embedded snapshot) and captures the post-merge
    /// delta into <see cref="LastSimbadMergeSnapshot"/> for serialisation. Has no effect at
    /// runtime where this flag stays false.
    /// </summary>
    internal bool ForceLiveSimbadMergeWithCapture { get; init; }

    public IReadOnlyCollection<string> CommonNames => _objectsByCommonName.Keys;

    public IReadOnlySet<Catalog> Catalogs => GetOrRebuildIndex(ref _completeCatalogCache, RebuildCatalogCache);

    public IReadOnlySet<CatalogIndex> AllObjectIndices => GetOrRebuildIndex(ref _catalogIndicesCache, RebuildObjectIndices);

    public IRaDecIndex CoordinateGrid => new CompositeRaDecIndex(_raDecIndex, _tycho2RaDecIndex);

    public IRaDecIndex DeepSkyCoordinateGrid => _raDecIndex;

    /// <summary>
    /// Internal diagnostic: surfaces the inner state of the Tycho-2 bulk load
    /// so a test (or precompute tool) can verify the binary was decoded
    /// successfully. Production code should never need this -- use
    /// <see cref="Tycho2StarCount"/> + <see cref="CopyTycho2Stars"/> instead.
    /// </summary>
    internal (bool DataLoaded, int StreamCount, bool IndexBuilt, bool BulkTaskRan, TaskStatus? BulkTaskStatus) Tycho2BulkLoadState =>
        (_tycho2Data is not null,
         _tycho2StreamCount,
         _tycho2RaDecIndex is not null,
         _tycho2BulkLoadTask is not null,
         _tycho2BulkLoadTask?.Status);

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
        else if (cat is Catalog.HIP && !msbSet && TryLookupHIPCore(index, value, out celestialObject))
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

    /// <summary>
    /// Resolves an identifier to the index of a directly-stored entry, following the cross-index
    /// table when the identifier is only an alias. Every Messier number is such an alias -- the NGC
    /// ingestion registers e.g. M 8 purely as a cross-ref of NGC 6523, never as its own
    /// <see cref="_objectsByIndex"/> entry -- so a SIMBAD record whose only main-catalog identifier
    /// is an M-number would never match a bare <see cref="TryLookupByIndexDirect"/> and its
    /// cross-links were silently dropped (how Sh2-25 ended up as a standalone "Lagoon Nebula"
    /// duplicate). Strictly widens the direct check: anything it accepted resolves to itself; a
    /// followed alias must land on an <see cref="_objectsByIndex"/> entry (the Tycho-2/HD binary
    /// fallback objects are not directly indexable downstream). Returns 0 when nothing resolves.
    /// </summary>
    private CatalogIndex ResolveToDirectIndex(CatalogIndex index)
    {
        if (TryLookupByIndexDirect(index, out _, out _, out _))
        {
            return index;
        }

        return TryLookupByIndex(index, out var followed) && _objectsByIndex.ContainsKey(followed.Index)
            ? followed.Index
            : 0;
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
    public async Task InitDBAsync(bool waitForTycho2BulkLoad = false, CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            // Re-entry contract: if the caller wants the bulk Tycho-2 data ready and we
            // already initialised on a prior call (with or without that wait), join the
            // background decode here without restarting it. Idempotent — the underlying
            // task is created exactly once per instance.
            if (waitForTycho2BulkLoad)
            {
                await EnsureTycho2DataLoadedAsync(cancellationToken);
            }
            return;
        }

        // Serialize concurrent first-time callers. Without this, two parallel
        // InitDBAsync calls (e.g. InitializePlanner + a Recompute tick that also
        // lands in ComputeTonightsBestAsync) both see _isInitialized=false, both
        // enter, and both mutate _objectsByIndex in parallel -> "Collection was
        // modified" from HashSet<CatalogIndex>(_objectsByIndex.Keys) in one of them.
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
            {
                if (waitForTycho2BulkLoad)
                {
                    await EnsureTycho2DataLoadedAsync(cancellationToken);
                }
                return;
            }

            await InitDBCoreAsync(waitForTycho2BulkLoad, cancellationToken);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task InitDBCoreAsync(bool waitForTycho2BulkLoad, CancellationToken cancellationToken)
    {
        _lastInitPhaseTimings.Clear();
        LastInitProcessed = 0;
        LastInitFailed = 0;
        var phaseSw = new Stopwatch();

        var assembly = typeof(CelestialObjectDB).Assembly;
        var manifestNames = assembly.GetManifestResourceNames();
        var totalProcessed = 0;
        var totalFailed = 0;

        // Probe for the SIMBAD merge snapshot up front so we can skip pre-starting
        // hrParseTask + the SIMBAD prefetch pipeline entirely on the apply path. Embedded
        // resource lookup is a manifest scan, no I/O. ForceLiveSimbadMergeWithCapture
        // (precompute path) bypasses the snapshot apply attempt.
        var simbadSnapshotResource = ForceLiveSimbadMergeWithCapture
            ? null
            : manifestNames.FirstOrDefault(n => n.EndsWith(".simbad_merge.bin.gz", StringComparison.Ordinal));

        phaseSw.Restart();
        // Split the Tycho-2 read into two tasks. The hot one (HIP/HD→TYC cross-ref arrays,
        // ~1.2 MB compressed) is cheap to decompress and is needed inline by the cross-ref
        // multi-json loader and the live hd-hip-cross fallback; we await it at the
        // tycho2-cross-ref-join phase. The bulk one (tyc2.bin.lz ~21 MB + bounds + spatial
        // index, ~200-400 ms) is NOT on init's critical path when the Phase 2A snapshot
        // applies cleanly; it stays in `_tycho2BulkLoadTask` for runtime callers
        // (sky map / plate solver) to await via EnsureTycho2DataLoadedAsync().
        var tycho2CrossRefTask = Task.Run(() => ReadTycho2CrossRefArrays(assembly, manifestNames), cancellationToken);
        _tycho2BulkLoadTask = Task.Run(() => ReadTycho2Bulk(assembly, manifestNames), cancellationToken);

        // Start HR's SIMBAD parse on the thread pool too - HR is the heaviest SIMBAD
        // file (~200ms to decompress + parse) and by launching it now, it can run
        // alongside the predefined-object loop and NGC CSV parsing on the main thread.
        // By the time the SIMBAD merge loop begins, HR records are typically ready to
        // merge immediately.
        // Skip pre-starting if a SIMBAD snapshot is embedded — the apply path won't need
        // HR's parse; on hash miss the live fallback path starts the parse synchronously.
        Task<List<SimbadCatalogDto>?>? hrParseTask = simbadSnapshotResource is null
            ? ParseSimbadFileAsync(assembly, manifestNames, "HR", cancellationToken)
            : null;

        // Same trick for the NGC catalogs: LZ-decompress them in the background so that
        // when the main thread reaches the NGC merge phase, the bytes are ready and only
        // the (synchronous, main-thread-only) merge runs there. DecompressNgcAsync prefers
        // the ASCII-separated .gs.gz and falls back to the legacy .csv.lz.
        var ngcDecompressTasks = new[]
        {
            DecompressNgcAsync(assembly, manifestNames, "NGC", cancellationToken),
            DecompressNgcAsync(assembly, manifestNames, "NGC.addendum", cancellationToken),
        };

        foreach (var predefined in _predefinedObjects)
        {
            var cat = predefined.Key.ToCatalog();

            var commonNames = new HashSet<string>(predefined.Value.CommonNames);
            commonNames.TrimExcess();
            _objectsByIndex[predefined.Key] = new CelestialObject(predefined.Key, predefined.Value.ObjType, double.NaN, double.NaN, 0, predefined.Value.VMag, HalfUndefined, predefined.Value.BVColor, commonNames);
            AddCommonNameIndex(predefined.Key, commonNames);
            totalProcessed++;
        }
        _lastInitPhaseTimings.Add(("predefined", phaseSw.Elapsed));

        phaseSw.Restart();
        // Merge in declared order - NGC first (baseline entries), then NGC.addendum
        // (overrides / extras). Each task's decompress already happened in parallel
        // with the predefined-object loop, so what runs here is just the merge which
        // must stay serial because it mutates _objectsByIndex.
        foreach (var decompressTask in ngcDecompressTasks)
        {
            var (bytes, isGs) = await decompressTask;
            var (processed, failed) = isGs
                ? MergeNgcGsData(bytes, cancellationToken)
                : MergeLzCsvData(bytes, cancellationToken);
            totalProcessed += processed;
            totalFailed += failed;
        }
        _lastInitPhaseTimings.Add(("ngc-csv", phaseSw.Elapsed));

        phaseSw.Restart();
        // Fast path: apply embedded simbad_merge.bin.gz snapshot if its input hash matches
        // the embedded SIMBAD/NGC catalog inputs; otherwise fall back to live parse + merge.
        // See docs/plans/catalog-binary-format.md § 2B.
        PreSimbadCaptureState? preSimbadState = null;
        var simbadAppliedFromSnapshot = simbadSnapshotResource is not null
            && await TryApplySimbadMergeSnapshotFromEmbeddedAsync(assembly, manifestNames, simbadSnapshotResource);

        if (!simbadAppliedFromSnapshot)
        {
            if (ForceLiveSimbadMergeWithCapture)
            {
                preSimbadState = CapturePreSimbadState();
            }

            // Compute mainCatalogs once before SIMBAD processing (avoids re-scanning 13K+ keys per file)
            var mainCatalogs = new HashSet<Catalog>(new HashSet<CatalogIndex>(_objectsByIndex.Keys).Select(idx => idx.ToCatalog()))
            {
                Catalog.HIP,
                Catalog.HD
            };

            (string FileName, Catalog Cat)[] simbadCatalogs =
            [
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
                ("Cl", Catalog.Collinder),
            ];
            // Depth-1 prefetch pipeline: while the main thread merges file N, exactly
            // ONE background task decompresses + JSON-parses file N+1. This overlaps
            // LZ decompress (CPU-bound, stateless) with merge (dict mutations, must be
            // serial), without piling 14 concurrent tasks onto the thread pool. That
            // matters for cold-start UX (14 tasks all JITing async state machines at
            // once adds tens of ms per task vs a single warmed path), and leaves CPU
            // headroom for Tycho2's own multi-threaded LZ decode running in parallel.
            // HR's parse was kicked off up front (see the hrParseTask assignment near the
            // Tycho2 launch) UNLESS a SIMBAD snapshot was embedded — in that case the
            // hash check here failed, so we start it synchronously now.
            Task<List<SimbadCatalogDto>?>? nextParseTask = simbadCatalogs.Length switch
            {
                0 => null,
                _ when simbadCatalogs[0].FileName == "HR" => hrParseTask ?? ParseSimbadFileAsync(assembly, manifestNames, "HR", cancellationToken),
                _ => ParseSimbadFileAsync(assembly, manifestNames, simbadCatalogs[0].FileName, cancellationToken),
            };
            for (var i = 0; i < simbadCatalogs.Length; i++)
            {
                var (fileName, catToAdd) = simbadCatalogs[i];
                // Kick off parse of the NEXT file before we do this file's merge.
                var prefetchTask = (i + 1 < simbadCatalogs.Length)
                    ? ParseSimbadFileAsync(assembly, manifestNames, simbadCatalogs[i + 1].FileName, cancellationToken)
                    : null;

                var perFileSw = Stopwatch.StartNew();
                var records = await nextParseTask!;
                if (records is not null)
                {
                    var (processed, failed) = MergeSimbadRecords(records, catToAdd, mainCatalogs);
                    totalProcessed += processed;
                    totalFailed += failed;
                }
                // mainCatalogs growth stays load-bearing: cross-ref recording for
                // entry N+1 depends on catalogs 0..N being visible here.
                mainCatalogs.Add(catToAdd);
                _lastInitPhaseTimings.Add(($"simbad:{fileName}:{catToAdd}", perFileSw.Elapsed));

                nextParseTask = prefetchTask;
            }

            if (preSimbadState is { } pre)
            {
                LastSimbadMergeSnapshot = EmitSimbadMergeSnapshot(pre);
            }
        }
        _lastInitPhaseTimings.Add(("simbad-total", phaseSw.Elapsed));

        // Second pass: load per-catalog *.shapes.json.lz companion files in
        // descending-quality order so the best measurement wins for objects that
        // exist in multiple catalogs. Cross-refs are fully established by the
        // main Simbad loop above, so ReadCatalogObjectShapesAsync can fan each
        // entry out to every known catalog variant of the same physical object.
        // Within a catalog, TryAdd is first-write-wins; across catalogs, the
        // earlier entry in this list wins on cross-referenced objects.
        //
        // Ranking:
        //   Dobashi 2011 — 7614 entries, modern whole-sky IR+visible survey
        //   LDN 1962     — 1802 entries, northern bias, sq-deg area (coarser)
        //   Barnard 1927 — 349  entries, classical, single Diam only
        //   Ced 1946     — 420  reflection nebulae; different object class, so
        //                  rarely overlaps with the dark-cloud three above, but
        //                  has real Dim1 x Dim2 ellipses where data exists
        (string FileName, Catalog Cat)[] shapeSources =
        [
            ("Dobashi", Catalog.Dobashi),
            ("LDN",     Catalog.LDN),
            ("Barnard", Catalog.Barnard),
            ("Ced",     Catalog.Ced),
        ];
        phaseSw.Restart();
        foreach (var (fileName, cat) in shapeSources)
        {
            await ReadCatalogObjectShapesAsync(assembly, manifestNames, fileName, cat, cancellationToken);
        }
        _lastInitPhaseTimings.Add(("shapes", phaseSw.Elapsed));

        // Wait for the small cross-ref arrays only — the bulk tyc2.bin.lz decode keeps
        // running in the background and only blocks init if the Phase 2A snapshot apply
        // misses (live BuildHdHipCrossIndicesViaTyc needs _tycho2Data) or the caller asked
        // to wait for it explicitly.
        phaseSw.Restart();
        await tycho2CrossRefTask;
        _lastInitPhaseTimings.Add(("tycho2-cross-ref-join", phaseSw.Elapsed));

        phaseSw.Restart();
        // Load cross-ref multi-json files (modifies shared _crossIndexLookuptable, must be sequential)
        LoadCrossRefMultiJson(assembly, manifestNames, "hip_to_tyc_multi", Catalog.HIP, Catalog.HIP.GetNumericalIndexSize(), _hipToTyc);
        LoadCrossRefMultiJson(assembly, manifestNames, "hd_to_tyc_multi", Catalog.HD, Catalog.HD.GetNumericalIndexSize(), _hdToTyc);
        _lastInitPhaseTimings.Add(("cross-ref-json", phaseSw.Elapsed));

        phaseSw.Restart();
        // Build cross-indices between HD and HIP via shared TYC stars
        // (must happen after all catalog loading so HIP cross-refs to HR, vdB etc. are present).
        // Fast path: apply embedded hd_hip_cross.bin.gz snapshot if its input hash matches
        // the embedded catalog inputs; otherwise fall back to live compute. See
        // docs/plans/catalog-binary-format.md § 2A.
        if (ForceLiveHdHipCrossWithCapture)
        {
            // Live build needs _tycho2Data + _tycho2RaDecIndex; the bulk task is in flight.
            await EnsureTycho2DataLoadedAsync(cancellationToken);
            BuildHdHipCrossIndicesViaTyc(captureSnapshot: true);
        }
        else if (!await TryApplyHdHipCrossSnapshotFromEmbeddedAsync(assembly, manifestNames))
        {
            if (manifestNames.Any(n => n.EndsWith(".tyc2.bin.lz", StringComparison.Ordinal)))
            {
                // Snapshot stale/missing/malformed -> fall back to live compute, which needs the
                // full Tycho-2 binary. Block here until the background bulk decode finishes.
                await EnsureTycho2DataLoadedAsync(cancellationToken);
                BuildHdHipCrossIndicesViaTyc();
            }
            else
            {
                // Lightweight build (tyc2 stripped) with no usable snapshot: the live compute is
                // impossible without the Tycho-2 binary. Degrade to no HD<->HIP cross-indices
                // (bright-star lookups still resolve via their direct HR/HD identities) rather
                // than failing init.
                _lastInitPhaseTimings.Add(("hd-hip-cross:skipped-lightweight", phaseSw.Elapsed));
            }
        }
        _lastInitPhaseTimings.Add(("hd-hip-cross", phaseSw.Elapsed));

        // Kick off the sorted auto-complete list bake in the background. At this point every
        // input the bake reads is frozen for the lifetime of this instance: _objectsByIndex
        // and _crossIndexLookuptable are append-only during init (no further mutation after
        // hd-hip-cross), and _objectsByCommonName likewise. The bake therefore needs no
        // synchronisation with the rest of init; it runs in parallel with the (possibly
        // already-finished) Tycho-2 bulk wait. First caller of CreateAutoCompleteList awaits
        // the result; if init was kicked off well before search opens, the bake is done by
        // then and the await is a no-op.
        _autoCompleteListTask = Task.Run(BuildSortedAutoCompleteList, cancellationToken);

        // Optional: gate init completion on the bulk Tycho-2 load. Default false — runtime
        // callers that need this data will await EnsureTycho2DataLoadedAsync themselves.
        // Set true when the caller wants InitDBAsync to return only after every bit of
        // catalog state is materialised (e.g. a precompute tool, or a test that immediately
        // queries Tycho-2 spatial state without going through an async pre-step).
        if (waitForTycho2BulkLoad)
        {
            phaseSw.Restart();
            await EnsureTycho2DataLoadedAsync(cancellationToken);
            _lastInitPhaseTimings.Add(("tycho2-bulk-wait", phaseSw.Elapsed));
        }

        _isInitialized = true;
        LastInitProcessed = totalProcessed;
        LastInitFailed = totalFailed;
    }

    /// <summary>
    /// Hot phase: decompresses the small (~1.2 MB) HIP→TYC and HD→TYC cross-reference arrays.
    /// Awaited inline by <see cref="InitDBCoreAsync"/> at the <c>tycho2-join</c> phase because
    /// the very next step (<c>LoadCrossRefMultiJson</c>) reads <see cref="_hipToTyc"/> and
    /// <see cref="_hdToTyc"/> to merge multi-target rows. Runs in ~30-50 ms warm.
    /// </summary>
    private void ReadTycho2CrossRefArrays(Assembly assembly, string[] manifestNames)
    {
        _hipToTyc = LoadCrossRefBinFile(assembly, manifestNames, "hip_to_tyc");
        _hdToTyc = LoadCrossRefBinFile(assembly, manifestNames, "hd_to_tyc");
    }

    /// <summary>
    /// Bulk phase: decompresses the ~21 MB Tycho-2 binary catalog plus its GSC region bounds
    /// and builds <see cref="_tycho2RaDecIndex"/>. Costs ~200-400 ms (multi-member parallel
    /// lzip decode, dominated by raw decompression). NOT on <see cref="InitDBAsync"/>'s
    /// critical path: kicked off in the background and only awaited when (a) the caller asked
    /// for <c>waitForTycho2BulkLoad</c>, (b) the Phase 2A snapshot apply path failed and we
    /// fall back to <see cref="BuildHdHipCrossIndicesViaTyc"/> (which needs <c>_tycho2Data</c>),
    /// or (c) a runtime caller invokes <see cref="EnsureTycho2DataLoadedAsync"/>.
    /// </summary>
    private void ReadTycho2Bulk(Assembly assembly, string[] manifestNames)
    {
        var tyc2Manifest = manifestNames.FirstOrDefault(p => p.EndsWith(".tyc2.bin.lz"));
        if (tyc2Manifest is null || assembly.GetManifestResourceStream(tyc2Manifest) is not Stream tyc2Stream)
        {
            return;
        }

        WireTycho2BulkData(LzipDecoder.Decompress(tyc2Stream));

        var boundsManifest = manifestNames.FirstOrDefault(p => p.EndsWith(".tyc2_gsc_bounds.bin.lz"));
        if (boundsManifest is not null && assembly.GetManifestResourceStream(boundsManifest) is Stream boundsStream)
        {
            var boundsData = LzipDecoder.Decompress(boundsStream);
            _tycho2RaDecIndex = new Tycho2RaDecIndex(_tycho2Data, _tycho2StreamCount, boundsData);
        }

        var sidecarManifest = manifestNames.FirstOrDefault(p => p.EndsWith(".tyc2_pm_sidecar.bin.lz"));
        if (sidecarManifest is not null && assembly.GetManifestResourceStream(sidecarManifest) is Stream sidecarStream)
        {
            // Format: 13 bytes per entry, tyc1 u16 | tyc2 u16 | tyc3 u8 | pmRA int32 | pmDec int32.
            // Entry count = byte length / 13 (no header). Sorted ascending by (tyc1, tyc2, tyc3).
            var sidecarBytes = LzipDecoder.Decompress(sidecarStream);
            var entryCount = sidecarBytes.Length / 13;
            var dict = new Dictionary<long, (int, int)>(entryCount);
            var src = sidecarBytes.AsSpan();
            for (int i = 0; i < entryCount; i++)
            {
                var off = i * 13;
                var tyc1 = BinaryPrimitives.ReadUInt16LittleEndian(src[off..]);
                var tyc2 = BinaryPrimitives.ReadUInt16LittleEndian(src[(off + 2)..]);
                var tyc3 = src[off + 4];
                var pmRa  = BinaryPrimitives.ReadInt32LittleEndian(src[(off + 5)..]);
                var pmDec = BinaryPrimitives.ReadInt32LittleEndian(src[(off + 9)..]);
                dict[PackTycKey(tyc1, tyc2, tyc3)] = (pmRa, pmDec);
            }
            _tycho2PmSidecar = dict;
        }
    }

    /// <summary>
    /// Publishes the decompressed Tycho-2 bulk buffer + its stream count. Shared by the embedded
    /// path (<see cref="ReadTycho2Bulk"/>) and the byte[] injection path
    /// (<see cref="TryLoadTycho2BulkFromCompressed"/>) so the "read stream count, then publish the
    /// data" wiring lives in one place. Takes the array by reference (no copy) and assigns
    /// <see cref="_tycho2Data"/> LAST: <see cref="Tycho2StarCount"/> / <see cref="CopyTycho2Stars"/>
    /// guard on <c>_tycho2Data != null</c>, so the stream count must already be set when the field
    /// they key off becomes visible (single-threaded on WASM today, but the ordering keeps the
    /// reader correct if a wasm-threads build ever injects off the render thread).
    /// </summary>
    [MemberNotNull(nameof(_tycho2Data))]
    private void WireTycho2BulkData(byte[] decompressed)
    {
        _tycho2StreamCount = BinaryPrimitives.ReadInt32LittleEndian(decompressed);
        _tycho2Data = decompressed;
    }

    /// <inheritdoc/>
    public bool TryLoadTycho2BulkFromCompressed(byte[] compressedLz)
    {
        // Already loaded (the embedded desktop path, or a prior injection) - nothing to do.
        if (_tycho2Data is not null)
        {
            return true;
        }
        if (compressedLz is null || compressedLz.Length == 0)
        {
            return false;
        }

        return TryLoadTycho2BulkFromDecoded(LzipDecoder.Decompress(compressedLz));
    }

    /// <inheritdoc/>
    public bool TryLoadTycho2BulkFromDecoded(byte[] decodedData)
    {
        // Already loaded (embedded desktop path, or a prior injection) - nothing to do.
        if (_tycho2Data is not null)
        {
            return true;
        }
        if (decodedData is null || decodedData.Length < 4)
        {
            return false;
        }

        // Publish the bulk star records (torn-free), then build the GSC-bounds spatial index so the
        // composite CoordinateGrid answers FOV queries against Tycho-2 - the click-to-identify /
        // overlay-label path resolves individual TYC stars. The high-pm sidecar (~11 stars) is still
        // NOT wired: it only refines proper motion, which a plotted/clicked dot doesn't need
        // (CopyTycho2Stars falls back to the inline rail value for those).
        WireTycho2BulkData(decodedData);
        BuildTycho2SpatialIndexFromEmbeddedBounds();
        return true;
    }

    /// <summary>
    /// Builds the Tycho-2 spatial index (<see cref="_tycho2RaDecIndex"/>) from the embedded
    /// GSC-region bounds (<c>tyc2_gsc_bounds.bin.lz</c>, still embedded on the Lightweight build).
    /// Requires <see cref="_tycho2Data"/> already set. Cheap: it indexes the ~9.5k GSC region
    /// bounding boxes, not the 2.5M stars. Idempotent + a no-op when the bounds resource is absent.
    /// </summary>
    private void BuildTycho2SpatialIndexFromEmbeddedBounds()
    {
        var data = _tycho2Data;
        if (data is null || _tycho2RaDecIndex is not null)
        {
            return;
        }

        var assembly = typeof(CelestialObjectDB).Assembly;
        var boundsManifest = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".tyc2_gsc_bounds.bin.lz", StringComparison.Ordinal));
        if (boundsManifest is null)
        {
            return;
        }
        using var boundsStream = assembly.GetManifestResourceStream(boundsManifest);
        if (boundsStream is null)
        {
            return;
        }
        var boundsData = LzipDecoder.Decompress(boundsStream);
        _tycho2RaDecIndex = new Tycho2RaDecIndex(data, _tycho2StreamCount, boundsData);
    }

    /// <summary>
    /// Awaits the background Tycho-2 bulk load (<c>tyc2.bin.lz</c> + GSC bounds + spatial
    /// index). Callers that read <see cref="CoordinateGrid"/>, <see cref="CopyTycho2Stars"/>,
    /// or any Tycho-2 spatial query must <c>await</c> this before accessing those APIs;
    /// otherwise the data may not be available yet (the lookups silently no-op when
    /// <see cref="_tycho2Data"/> is null).
    /// <para>
    /// Cheap to call repeatedly: the underlying task is started exactly once during
    /// <see cref="InitDBAsync"/>. <see cref="Task.WaitAsync(CancellationToken)"/> abandons
    /// the wait on cancellation but does not cancel the underlying decode.
    /// </para>
    /// </summary>
    public Task EnsureTycho2DataLoadedAsync(CancellationToken cancellationToken = default)
        => _tycho2BulkLoadTask is { } t ? t.WaitAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Overrides the default interface implementation in <see cref="ICelestialObjectDB"/>.
    /// Returns the result of the background bake started during init (see
    /// <see cref="_autoCompleteListTask"/>) once it has completed; while the bake is still
    /// in flight it builds the list synchronously on the calling thread rather than blocking
    /// on the task. Once the bake has finished, subsequent calls return the same baked array.
    /// <para>
    /// Building inline is also the fallback for the pre-init / not-yet-started case (defensive
    /// only — init always starts the bake) so unit tests that construct a partially-populated
    /// db can still call this without depending on init internals.
    /// </para>
    /// </summary>
    public string[] CreateAutoCompleteList()
        => _autoCompleteListTask is { IsCompletedSuccessfully: true } t ? t.Result : BuildSortedAutoCompleteList();

    /// <summary>
    /// Pure CPU bake: enumerates <see cref="AllObjectIndices"/> + <see cref="CommonNames"/>
    /// into a deduped array, then sorts ordinal-ignore-case so callers can binary-search
    /// the prefix range. Identical contract to the default
    /// <see cref="ICelestialObjectDB.CreateAutoCompleteList"/>, just packaged for off-init
    /// thread-pool execution.
    /// </summary>
    private string[] BuildSortedAutoCompleteList()
    {
        var objIndices = AllObjectIndices;
        var canonicalSet = new HashSet<string>((int)(objIndices.Count * 1.3f));
        foreach (var objIndex in objIndices)
        {
            canonicalSet.Add(objIndex.ToCanonical(CanonicalFormat.Normal));
            canonicalSet.Add(objIndex.ToCanonical(CanonicalFormat.Alternative));
        }

        var commonNames = CommonNames;
        var names = new string[canonicalSet.Count + commonNames.Count];
        canonicalSet.CopyTo(names, 0);
        commonNames.CopyTo(names, canonicalSet.Count);

        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Snapshot of the state mutated by the most recent <see cref="BuildHdHipCrossIndicesViaTyc"/>
    /// call. Captured opportunistically during the live path; consumed by the precompute tool
    /// (<c>tools/precompute-hd-hip-cross</c>) to bake the result into <c>hd_hip_cross.bin.gz</c>.
    /// Null until live compute runs (or after a successful apply-from-snapshot init).
    /// </summary>
    internal HdHipCrossSnapshot? LastHdHipCrossSnapshot { get; private set; }

    /// <summary>
    /// Snapshot of the SIMBAD merge phase delta. Populated during the precompute path
    /// (<see cref="ForceLiveSimbadMergeWithCapture"/>); consumed by
    /// <c>tools/precompute-simbad-merge</c> to bake <c>simbad_merge.bin.gz</c>. Null at
    /// runtime when the snapshot apply path runs successfully.
    /// </summary>
    internal SimbadMergeSnapshot? LastSimbadMergeSnapshot { get; private set; }

    /// <summary>
    /// Cheap capture of the dict shape that the SIMBAD merge phase will mutate. Storing
    /// CommonNames count is sufficient because SIMBAD only ever ADDS names via
    /// <c>UpdateObjectCommonNames</c> (UnionWith); a count change is therefore a reliable
    /// "this entry was touched" signal. For <c>_crossIndexLookuptable</c> and
    /// <c>_objectsByCommonName</c> the (i1, ext.Length) pair is sufficient for the same reason:
    /// <c>AddLookupEntry</c> never mutates i1 once set and only appends to ext.
    /// </summary>
    private sealed class PreSimbadCaptureState
    {
        public Dictionary<CatalogIndex, int> ObjectsCommonNamesCount = new();
        public Dictionary<CatalogIndex, (CatalogIndex i1, int extLen)> CrossIndexShape = new();
        public Dictionary<string, (CatalogIndex i1, int extLen)> CommonNameShape = new();
    }

    private void BuildHdHipCrossIndicesViaTyc(bool captureSnapshot = false)
    {
        if (_hipToTyc is not { } hipToTyc || _hdToTyc is not { } hdToTyc)
        {
            throw new InvalidOperationException("HIP→TYC and HD→TYC cross-reference data must be loaded before building HD↔HIP cross-indices.");
        }

        var subSw = Stopwatch.StartNew();

        // Build TYC → HIP reverse index (single pass, no shared-dict contention).
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
        _lastInitPhaseTimings.Add(("hd-hip-cross:tycToHip", subSw.Elapsed));
        subSw.Restart();

        // Accumulate all cross-ref additions into a delta dict, then bulk-merge at
        // the end. The old incremental path called _crossIndexLookuptable.
        // AddLookupEntry ~2M times (each HD entry → 2 base adds + 2×N propagation
        // adds), and the extension-array realloc in AppendToArray makes the total
        // cost O(edges²) per key. Bulk-merging once per key collapses that to O(n).
        // Parallelise the scan: read-only access to tycToHip, _objectsByIndex,
        // and pre-existing entries in _crossIndexLookuptable. Per-thread write
        // targets (newObjects, edgeDelta) are merged serially afterwards, so
        // there's no shared-state contention during the scan itself.
        var partitions = Math.Min(Environment.ProcessorCount, 8);
        var chunkSize = (hdToTyc.Length + partitions - 1) / partitions;
        var perThread = new (List<(CatalogIndex HdIndex, CelestialObject Obj)> NewObjects,
                             Dictionary<CatalogIndex, HashSet<CatalogIndex>> EdgeDelta,
                             int MatchCount)[partitions];

        Parallel.For(0, partitions, p =>
        {
            var start = p * chunkSize;
            var end = Math.Min(start + chunkSize, hdToTyc.Length);
            var newObjs = new List<(CatalogIndex, CelestialObject)>(capacity: 16_384);
            var delta = new Dictionary<CatalogIndex, HashSet<CatalogIndex>>(1 << 14);
            var matches = 0;

            for (int i = start; i < end; i++)
            {
                var tycIndex = hdToTyc[i];
                if (tycIndex == 0 || !tycToHip.TryGetValue(tycIndex, out var hipIndex))
                {
                    continue;
                }
                matches++;
                var hdIndex = PrefixedNumericToASCIIPackedInt<CatalogIndex>((ulong)Catalog.HD, i + 1, Catalog.HD.GetNumericalIndexSize());

                if (!_objectsByIndex.ContainsKey(hdIndex)
                    && TryGetTycho2RaDec(tycIndex, out var ra, out var dec, out var vMag, out var bv)
                    && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
                {
                    var objType = GetInheritedObjectType(hipIndex);
                    var hdObj = new CelestialObject(hdIndex, objType, ra, dec, constellation, vMag, HalfUndefined, (Half)bv, EmptyNameSet);
                    newObjs.Add((hdIndex, hdObj));
                }

                AddEdge(delta, hdIndex, hipIndex);
                AddEdge(delta, hipIndex, hdIndex);

                if (_crossIndexLookuptable.TryGetValue(hipIndex, out var hipCross))
                {
                    if (!hipCross.i1.Equals(default(CatalogIndex)) && !hipCross.i1.Equals(hdIndex))
                    {
                        AddEdge(delta, hipCross.i1, hdIndex);
                        AddEdge(delta, hdIndex, hipCross.i1);
                    }
                    if (hipCross.ext is { } ext)
                    {
                        foreach (var ci in ext)
                        {
                            if (!ci.Equals(hdIndex))
                            {
                                AddEdge(delta, ci, hdIndex);
                                AddEdge(delta, hdIndex, ci);
                            }
                        }
                    }
                }
            }
            perThread[p] = (newObjs, delta, matches);
        });
        _lastInitPhaseTimings.Add(("hd-hip-cross:scan", subSw.Elapsed));
        subSw.Restart();

        // Serial merge: HD object inserts + spatial index + consolidated edge delta.
        var edgeDelta = perThread[0].EdgeDelta;
        foreach (var (hdIndex, hdObj) in perThread[0].NewObjects)
        {
            _objectsByIndex[hdIndex] = hdObj;
            AddCommonNameAndPosIndices(hdObj);
        }
        for (var p = 1; p < perThread.Length; p++)
        {
            foreach (var (hdIndex, hdObj) in perThread[p].NewObjects)
            {
                _objectsByIndex[hdIndex] = hdObj;
                AddCommonNameAndPosIndices(hdObj);
            }
            foreach (var (key, values) in perThread[p].EdgeDelta)
            {
                if (!edgeDelta.TryGetValue(key, out var existing))
                {
                    edgeDelta[key] = values;
                }
                else
                {
                    existing.UnionWith(values);
                }
            }
        }

        _lastInitPhaseTimings.Add(("hd-hip-cross:objects+union", subSw.Elapsed));
        subSw.Restart();

        // Single-pass bulk merge: one tuple build + one dict write per affected key.
        foreach (var (key, newValues) in edgeDelta)
        {
            MergeEdgesBulk(_crossIndexLookuptable, key, newValues);
        }
        _lastInitPhaseTimings.Add(("hd-hip-cross:bulk-merge", subSw.Elapsed));

        if (captureSnapshot)
        {
            // Capture the post-merge final tuple per affected key plus the new HD entries.
            // This is O(touched-keys + N), runs after the bulk merge, and is only paid in the
            // build-time precompute path — not at runtime.
            subSw.Restart();
            var hdBuilder = ImmutableArray.CreateBuilder<HdEntrySnapshot>();
            for (var p = 0; p < perThread.Length; p++)
            {
                foreach (var (hdIndex, hdObj) in perThread[p].NewObjects)
                {
                    hdBuilder.Add(new HdEntrySnapshot(
                        hdObj.Index, hdObj.ObjectType, hdObj.Constellation,
                        hdObj.RA, hdObj.Dec, hdObj.V_Mag, hdObj.BMinusV));
                }
            }

            var edgeBuilder = ImmutableArray.CreateBuilder<EdgeSnapshot>(edgeDelta.Count);
            foreach (var key in edgeDelta.Keys)
            {
                if (_crossIndexLookuptable.TryGetValue(key, out var finalTuple))
                {
                    var ext = finalTuple.ext is { } extArr
                        ? ImmutableCollectionsMarshal.AsImmutableArray((CatalogIndex[])extArr.Clone())
                        : ImmutableArray<CatalogIndex>.Empty;
                    edgeBuilder.Add(new EdgeSnapshot(key, finalTuple.i1, ext));
                }
            }

            LastHdHipCrossSnapshot = new HdHipCrossSnapshot(hdBuilder.DrainToImmutable(), edgeBuilder.DrainToImmutable());
            _lastInitPhaseTimings.Add(("hd-hip-cross:capture", subSw.Elapsed));
        }
    }

    /// <summary>
    /// Fast-path replacement for <see cref="BuildHdHipCrossIndicesViaTyc"/>: applies a
    /// precomputed snapshot directly to <see cref="_objectsByIndex"/>, <see cref="_raDecIndex"/>,
    /// and <see cref="_crossIndexLookuptable"/>. Caller is responsible for hash-verifying the
    /// snapshot against the embedded catalog inputs before calling.
    ///
    /// <para>End state matches what <see cref="BuildHdHipCrossIndicesViaTyc"/> would have produced,
    /// modulo the determinism guarantees described on <see cref="HdHipCrossSnapshot"/>.</para>
    /// </summary>
    internal void ApplyHdHipCrossSnapshot(HdHipCrossSnapshot snapshot)
    {
        foreach (var hd in snapshot.HdEntries)
        {
            var obj = new CelestialObject(hd.Index, hd.ObjType, hd.Ra, hd.Dec, hd.Constellation, hd.VMag, HalfUndefined, hd.BvColor, EmptyNameSet);
            _objectsByIndex[hd.Index] = obj;
            AddCommonNameAndPosIndices(obj);
        }

        foreach (var edge in snapshot.Edges)
        {
            // Empty Ext serialises as null in the live dict shape: the (i1, null) form is the
            // single-entry sentinel in _crossIndexLookuptable. Only allocate an array when there
            // are actual ext entries.
            var extArr = edge.Ext.IsDefaultOrEmpty ? null : edge.Ext.ToArray();
            _crossIndexLookuptable[edge.Key] = (edge.V1, extArr);
        }
    }

    /// <summary>
    /// Looks for an embedded <c>hd_hip_cross.bin.gz</c> resource, hash-verifies it against
    /// the embedded catalog inputs, and applies it on hit. Returns false if the resource is
    /// missing, malformed, or stale — caller is expected to fall back to the live compute
    /// path. Per-path timings are added to <see cref="_lastInitPhaseTimings"/> so cold-start
    /// telemetry shows which branch ran.
    /// </summary>
    private async Task<bool> TryApplyHdHipCrossSnapshotFromEmbeddedAsync(Assembly assembly, string[] manifestNames)
    {
        var subSw = Stopwatch.StartNew();
        var snapshotResource = manifestNames.FirstOrDefault(n => n.EndsWith(".hd_hip_cross.bin.gz", StringComparison.Ordinal));
        if (snapshotResource is null)
        {
            _lastInitPhaseTimings.Add(("hd-hip-cross-snapshot:missing", subSw.Elapsed));
            return false;
        }

        // A Lightweight build (-p:Lightweight=true, e.g. the browser/WASM app) strips tyc2.bin.lz,
        // which is one of the hash-guard's inputs - verification is then IMPOSSIBLE, not merely
        // stale (HdHipCrossInputHasher.Compute throws on the missing input). The guard protects a
        // dev-time invariant (rebake the snapshot when inputs change) that every full build + CI
        // still enforce, and the lightweight artifact embeds the very same snapshot bytes, so
        // apply it unverified instead of failing init.
        var canVerify = manifestNames.Any(n => n.EndsWith(".tyc2.bin.lz", StringComparison.Ordinal));

        // Compute the input hash in parallel with snapshot read+decode: both touch ~22 MB of
        // embedded resource data and SHA-256 is CPU-bound enough to saturate a core. On a
        // representative cold start the snapshot read takes ~50 ms and the hash ~50 ms;
        // overlapping them shaves ~30-40 ms off the apply path.
        var hashTask = canVerify ? Task.Run(() => HdHipCrossInputHasher.Compute(assembly, manifestNames)) : null;

        HdHipCrossSnapshot snapshot;
        byte[] storedHash;
        try
        {
            using var stream = assembly.GetManifestResourceStream(snapshotResource)
                ?? throw new InvalidOperationException("Snapshot resource stream was null.");
            snapshot = HdHipCrossSnapshotIo.Read(stream, out storedHash);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            // Treat any decode failure as "stale": fall back to live compute. Debug-time safety
            // net; in CI the snapshot is rebuilt so a malformed embedded resource is a packaging bug.
            _lastInitPhaseTimings.Add(("hd-hip-cross-snapshot:malformed", subSw.Elapsed));
            if (hashTask is not null)
            {
                await hashTask;  // observe to avoid unobserved-task warnings
            }
            return false;
        }
        var readElapsed = subSw.Elapsed;
        _lastInitPhaseTimings.Add(("hd-hip-cross-snapshot:read", readElapsed));

        if (hashTask is not null)
        {
            var expectedHash = await hashTask;
            _lastInitPhaseTimings.Add(("hd-hip-cross-snapshot:hash", subSw.Elapsed - readElapsed));
            if (!storedHash.AsSpan().SequenceEqual(expectedHash))
            {
                _lastInitPhaseTimings.Add(("hd-hip-cross-snapshot:stale", subSw.Elapsed));
                return false;
            }
        }
        else
        {
            _lastInitPhaseTimings.Add(("hd-hip-cross-snapshot:unverified-lightweight", subSw.Elapsed));
        }

        var beforeApply = subSw.Elapsed;
        ApplyHdHipCrossSnapshot(snapshot);
        _lastInitPhaseTimings.Add(("hd-hip-cross-snapshot:apply", subSw.Elapsed - beforeApply));
        _lastInitPhaseTimings.Add(("hd-hip-cross-snapshot:applied", subSw.Elapsed));
        return true;
    }

    /// <summary>
    /// Snapshot the post-NGC, pre-SIMBAD shape of <see cref="_objectsByIndex"/> +
    /// <see cref="_crossIndexLookuptable"/>. Used by the capture path
    /// (<see cref="ForceLiveSimbadMergeWithCapture"/>) to compute the SIMBAD-only delta
    /// after the live merge runs. Cheap: ~13 K + ~5 K dict scans, no value cloning.
    /// </summary>
    private PreSimbadCaptureState CapturePreSimbadState()
    {
        var state = new PreSimbadCaptureState
        {
            ObjectsCommonNamesCount = new Dictionary<CatalogIndex, int>(_objectsByIndex.Count),
            CrossIndexShape = new Dictionary<CatalogIndex, (CatalogIndex, int)>(_crossIndexLookuptable.Count),
            CommonNameShape = new Dictionary<string, (CatalogIndex, int)>(_objectsByCommonName.Count),
        };
        foreach (var (key, obj) in _objectsByIndex)
        {
            state.ObjectsCommonNamesCount[key] = obj.CommonNames.Count;
        }
        foreach (var (key, tuple) in _crossIndexLookuptable)
        {
            state.CrossIndexShape[key] = (tuple.i1, tuple.ext?.Length ?? 0);
        }
        foreach (var (name, tuple) in _objectsByCommonName)
        {
            state.CommonNameShape[name] = (tuple.i1, tuple.ext?.Length ?? 0);
        }
        return state;
    }

    /// <summary>
    /// Diffs the post-SIMBAD-merge state against <paramref name="pre"/> and emits a
    /// <see cref="SimbadMergeSnapshot"/> containing every key the SIMBAD merge added
    /// or modified. Diff signal: CommonNames count for objects (SIMBAD only adds names),
    /// (i1, ext.Length) for cross-ref tuples (AddLookupEntry never mutates i1 once set
    /// and only appends to ext).
    /// </summary>
    private SimbadMergeSnapshot EmitSimbadMergeSnapshot(PreSimbadCaptureState pre)
    {
        var objBuilder = ImmutableArray.CreateBuilder<SimbadObjectSnapshot>();
        foreach (var (key, obj) in _objectsByIndex)
        {
            var preCount = pre.ObjectsCommonNamesCount.TryGetValue(key, out var c) ? c : -1;
            if (preCount == obj.CommonNames.Count)
            {
                continue;
            }
            ImmutableArray<string> names;
            if (obj.CommonNames.Count == 0)
            {
                names = ImmutableArray<string>.Empty;
            }
            else
            {
                var nb = ImmutableArray.CreateBuilder<string>(obj.CommonNames.Count);
                foreach (var n in obj.CommonNames)
                {
                    nb.Add(n);
                }
                names = nb.MoveToImmutable();
            }
            objBuilder.Add(new SimbadObjectSnapshot(
                obj.Index, obj.ObjectType, obj.Constellation,
                obj.RA, obj.Dec, obj.V_Mag, obj.SurfaceBrightness, obj.BMinusV,
                names));
        }

        var edgeBuilder = ImmutableArray.CreateBuilder<EdgeSnapshot>();
        foreach (var (key, tuple) in _crossIndexLookuptable)
        {
            var nowExtLen = tuple.ext?.Length ?? 0;
            if (pre.CrossIndexShape.TryGetValue(key, out var preShape)
                && preShape.i1.Equals(tuple.i1)
                && preShape.extLen == nowExtLen)
            {
                continue;
            }
            var ext = tuple.ext is { } extArr
                ? ImmutableCollectionsMarshal.AsImmutableArray((CatalogIndex[])extArr.Clone())
                : ImmutableArray<CatalogIndex>.Empty;
            edgeBuilder.Add(new EdgeSnapshot(key, tuple.i1, ext));
        }

        var nmBuilder = ImmutableArray.CreateBuilder<NameMappingSnapshot>();
        foreach (var (name, tuple) in _objectsByCommonName)
        {
            var nowExtLen = tuple.ext?.Length ?? 0;
            if (pre.CommonNameShape.TryGetValue(name, out var preShape)
                && preShape.i1.Equals(tuple.i1)
                && preShape.extLen == nowExtLen)
            {
                continue;
            }
            var ext = tuple.ext is { } extArr
                ? ImmutableCollectionsMarshal.AsImmutableArray((CatalogIndex[])extArr.Clone())
                : ImmutableArray<CatalogIndex>.Empty;
            nmBuilder.Add(new NameMappingSnapshot(name, tuple.i1, ext));
        }

        return new SimbadMergeSnapshot(objBuilder.DrainToImmutable(), edgeBuilder.DrainToImmutable(), nmBuilder.DrainToImmutable());
    }

    /// <summary>
    /// Fast-path replacement for the SIMBAD merge loop: applies a precomputed snapshot
    /// directly to <see cref="_objectsByIndex"/>, <see cref="_crossIndexLookuptable"/>,
    /// <see cref="_objectsByCommonName"/>, and <see cref="_raDecIndex"/>. Caller is
    /// responsible for hash-verifying the snapshot against the embedded catalog inputs
    /// before calling.
    /// </summary>
    internal void ApplySimbadMergeSnapshot(SimbadMergeSnapshot snapshot)
    {
        foreach (var obj in snapshot.Objects)
        {
            IReadOnlySet<string> commonNames = obj.CommonNames.IsDefaultOrEmpty
                ? EmptyNameSet
                : new HashSet<string>(obj.CommonNames);

            var ce = new CelestialObject(
                obj.Index, obj.ObjType, obj.Ra, obj.Dec, obj.Constellation,
                obj.VMag, obj.SurfaceBrightness, obj.BvColor, commonNames);
            _objectsByIndex[obj.Index] = ce;

            // _raDecIndex.Add is idempotent (AddElementIfNotExist), so calling it for keys
            // that already exist (NGC entries that SIMBAD only updated common names on) is
            // safe — no duplicates. The NaN guard mirrors the live PopulateSimbadStarEntries
            // / MergeSimbadRecords code paths that gate on ConstellationBoundary.TryFindConstellation.
            if (obj.ObjType is not ObjectType.Duplicate
                && !double.IsNaN(obj.Ra) && !double.IsNaN(obj.Dec))
            {
                _raDecIndex.Add(ce);
            }
        }

        foreach (var edge in snapshot.Edges)
        {
            // Empty Ext serialises as null in the live dict shape: the (i1, null) form is the
            // single-entry sentinel in _crossIndexLookuptable.
            var extArr = edge.Ext.IsDefaultOrEmpty ? null : edge.Ext.ToArray();
            _crossIndexLookuptable[edge.Key] = (edge.V1, extArr);
        }

        // _objectsByCommonName mappings: dict-overwrite with the captured (v1, ext[]).
        // The captured value is the FULL post-SIMBAD-merge tuple, which already includes any
        // pre-SIMBAD entries (added by NGC merge or predefined), so overwrite is correct.
        foreach (var nm in snapshot.NameMappings)
        {
            var extArr = nm.Ext.IsDefaultOrEmpty ? null : nm.Ext.ToArray();
            _objectsByCommonName[nm.Name] = (nm.V1, extArr);
        }
    }

    /// <summary>
    /// Looks for an embedded <c>simbad_merge.bin.gz</c> resource, hash-verifies it against
    /// the embedded SIMBAD/NGC catalog inputs, and applies it on hit. Returns false if the
    /// resource is malformed or stale — caller is expected to fall back to the live merge
    /// path. Caller passes the resource manifest name discovered up-front so the apply path
    /// only runs when there is a snapshot to try.
    /// </summary>
    private async Task<bool> TryApplySimbadMergeSnapshotFromEmbeddedAsync(Assembly assembly, string[] manifestNames, string snapshotResource)
    {
        var subSw = Stopwatch.StartNew();

        // Hash the SIMBAD/NGC inputs (~1 MB) in parallel with the snapshot read+decode.
        // SIMBAD inputs are small but SHA-256 still benefits from running off-thread.
        var hashTask = Task.Run(() => SimbadMergeInputHasher.Compute(assembly, manifestNames));

        SimbadMergeSnapshot snapshot;
        byte[] storedHash;
        try
        {
            using var stream = assembly.GetManifestResourceStream(snapshotResource)
                ?? throw new InvalidOperationException("Simbad snapshot resource stream was null.");
            snapshot = SimbadMergeSnapshotIo.Read(stream, out storedHash);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            _lastInitPhaseTimings.Add(("simbad-snapshot:malformed", subSw.Elapsed));
            await hashTask;
            return false;
        }
        var readElapsed = subSw.Elapsed;
        _lastInitPhaseTimings.Add(("simbad-snapshot:read", readElapsed));

        var expectedHash = await hashTask;
        _lastInitPhaseTimings.Add(("simbad-snapshot:hash", subSw.Elapsed - readElapsed));
        if (!storedHash.AsSpan().SequenceEqual(expectedHash))
        {
            _lastInitPhaseTimings.Add(("simbad-snapshot:stale", subSw.Elapsed));
            return false;
        }

        var beforeApply = subSw.Elapsed;
        ApplySimbadMergeSnapshot(snapshot);
        _lastInitPhaseTimings.Add(("simbad-snapshot:apply", subSw.Elapsed - beforeApply));
        _lastInitPhaseTimings.Add(("simbad-snapshot:applied", subSw.Elapsed));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddEdge(Dictionary<CatalogIndex, HashSet<CatalogIndex>> edges, CatalogIndex from, CatalogIndex to)
    {
        if (!edges.TryGetValue(from, out var targets))
        {
            edges[from] = targets = new HashSet<CatalogIndex>();
        }
        targets.Add(to);
    }

    /// <summary>
    /// Unions <paramref name="newValues"/> into the (v1, ext[]) tuple at
    /// <paramref name="key"/>, materialising the final ext array once instead of
    /// appending item-by-item (which would re-allocate the ext array N times).
    /// Preserves the existing v1 anchor when the key already exists — callers of
    /// TryLookupByIndex walk v1 first, and several tests assert that the FIRST
    /// inserted cross-ref (e.g. HIP for a vdB entry) stays the primary target.
    /// </summary>
    private static void MergeEdgesBulk(
        Dictionary<CatalogIndex, (CatalogIndex i1, CatalogIndex[]? ext)> dict,
        CatalogIndex key,
        HashSet<CatalogIndex> newValues)
    {
        if (newValues.Count == 0)
        {
            return;
        }

        if (dict.TryGetValue(key, out var existing))
        {
            // Build the new ext[] = union(newValues, existing.ext), minus i1.
            var extSet = new HashSet<CatalogIndex>(newValues);
            extSet.Remove(existing.i1);
            if (existing.ext is { } existingExt)
            {
                foreach (var v in existingExt) extSet.Add(v);
            }

            if (extSet.Count == 0)
            {
                // All newValues were duplicates of existing.i1 — nothing to do.
                return;
            }

            var ext = new CatalogIndex[extSet.Count];
            var idx = 0;
            foreach (var v in extSet) ext[idx++] = v;
            dict[key] = (existing.i1, ext);
        }
        else
        {
            using var enumerator = newValues.GetEnumerator();
            enumerator.MoveNext();
            var v1 = enumerator.Current;
            if (newValues.Count == 1)
            {
                dict[key] = (v1, null);
                return;
            }
            var ext = new CatalogIndex[newValues.Count - 1];
            var idx = 0;
            while (enumerator.MoveNext())
            {
                ext[idx++] = enumerator.Current;
            }
            dict[key] = (v1, ext);
        }
    }

    /// <summary>
    /// Gets the most specific object type from an index or its cross-references.
    /// Returns <see cref="ObjectType.Star"/> if no more specific type is found.
    /// </summary>
    private ObjectType GetInheritedObjectType(CatalogIndex index)
    {
        // Check the index itself first
        if (_objectsByIndex.TryGetValue(index, out var obj) && obj.ObjectType is not ObjectType.Star)
        {
            return obj.ObjectType;
        }

        // Check cross-references for a more specific type
        if (_crossIndexLookuptable.TryGetLookupEntries(index, out var crossRefs))
        {
            foreach (var crossRef in crossRefs)
            {
                if (_objectsByIndex.TryGetValue(crossRef, out var crossObj) && crossObj.ObjectType is not ObjectType.Star)
                {
                    return crossObj.ObjectType;
                }
            }
        }

        return ObjectType.Star;
    }

    /// <summary>
    /// V magnitude brighter (smaller) than this is in the Tycho-2 saturation regime:
    /// the brightest stars (the V &lt;= 2.5 Tycho-2 Supplement-1 set -- Antares, Sirius,
    /// Vega, ...) saturate the detector, so their VT/BT photometry and the linear
    /// VT->V Johnson transform are unreliable. For these we prefer a curated
    /// cross-reference (SIMBAD / HR) literature V when one exists. Set above the
    /// faintest supplement star (Lesath ~2.4) and well below normal main-catalog
    /// variables like R Leporis (~8.3) whose Tycho snapshot value we keep.
    /// </summary>
    private const float Tycho2SaturationVMag = 3.0f;

    /// <summary>
    /// True when a Tycho-2-derived V should defer to a curated cross-reference: the
    /// star has no Tycho V at all (NaN) or is bright enough to be in the saturation
    /// regime (see <see cref="Tycho2SaturationVMag"/>).
    /// </summary>
    private static bool PreferCrossRefMagnitude(Half tychoV)
        => Half.IsNaN(tychoV) || (float)tychoV <= Tycho2SaturationVMag;

    /// <summary>
    /// Walks <paramref name="index"/>'s own object and then its cross-references for a
    /// curated (SIMBAD / HR / HD) V magnitude. Mirrors <see cref="GetInheritedObjectType"/>.
    /// Used to override an unreliable saturated Tycho-2 magnitude for the brightest stars
    /// (see <see cref="Tycho2SaturationVMag"/>) -- e.g. Antares, whose saturated Tycho V
    /// (~1.10) is not the Johnson V (0.91) a red supergiant actually has. Returns
    /// <c>false</c> when nothing carries a non-NaN V, so the Tycho value is kept for
    /// stars SIMBAD lacks.
    /// </summary>
    private bool TryGetCrossRefMagnitude(CatalogIndex index, out Half vMag, out Half bMinusV)
    {
        vMag = HalfUndefined;
        bMinusV = HalfUndefined;

        // The index's own SIMBAD-loaded object first -- for a HIP star this is the
        // curated literature V (e.g. HIP 80763 = 0.91), which is exactly the value
        // TryLookupHIP returned before Supplement-1 gave Antares a Tycho entry.
        if (_objectsByIndex.TryGetValue(index, out var self) && !Half.IsNaN(self.V_Mag))
        {
            vMag = self.V_Mag;
            bMinusV = self.BMinusV;
            return true;
        }

        if (_crossIndexLookuptable.TryGetLookupEntries(index, out var crossRefs))
        {
            foreach (var crossRef in crossRefs)
            {
                if (_objectsByIndex.TryGetValue(crossRef, out var crossObj) && !Half.IsNaN(crossObj.V_Mag))
                {
                    vMag = crossObj.V_Mag;
                    bMinusV = crossObj.BMinusV;
                    return true;
                }
            }
        }
        return false;
    }

    private void PopulateSimbadStarEntries(Dictionary<Catalog, CatalogIndex[]> relevantIds, Catalog catalog, double raInH, double dec, Half vMag, Half bMinusV, ObjectType objType)
    {
        if (relevantIds.TryGetValue(catalog, out var ids))
        {
            foreach (var id in ids)
            {
                if (_objectsByIndex.TryGetValue(id, out var existing))
                {
                    // Update existing entry if SIMBAD provides a more specific object type
                    // or better magnitude/color data (e.g., CStar instead of generic Star from Tycho-2)
                    var needsUpdate = existing.ObjectType is ObjectType.Star && objType is not ObjectType.Star;
                    var newObjType = needsUpdate ? objType : existing.ObjectType;
                    var newVMag = !Half.IsNaN(vMag) ? vMag : existing.V_Mag;
                    var newBMinusV = !Half.IsNaN(bMinusV) ? bMinusV : existing.BMinusV;

                    if (needsUpdate || newVMag != existing.V_Mag || newBMinusV != existing.BMinusV)
                    {
                        var updated = new CelestialObject(existing.Index, newObjType, existing.RA, existing.Dec, existing.Constellation, newVMag, existing.SurfaceBrightness, newBMinusV, existing.CommonNames);
                        _objectsByIndex[id] = updated;
                    }
                }
                else if (ConstellationBoundary.TryFindConstellation(raInH, dec, out var constellation))
                {
                    var obj = new CelestialObject(id, objType, raInH, dec, constellation, vMag, HalfUndefined, bMinusV, EmptyNameSet);
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

    private bool TryGetTycho2RaDec(CatalogIndex tycIndex, out double ra, out double dec, out Half vMag, out float bMinusV)
    {
        ra = dec = 0;
        vMag = HalfUndefined;
        bMinusV = 0.65f;
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
        return TryGetTycho2RaDec(tyc1, (ushort)tyc2, tyc3, out ra, out dec, out vMag, out bMinusV);
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
    /// <inheritdoc/>
    public bool TryGetTycho2Star(CatalogIndex index, out Tycho2StarLite star)
    {
        star = default;
        if (_tycho2Data is null)
        {
            return false;
        }

        var (cat, value, msbSet) = index.ToCatalogAndValue();
        if (cat is not Catalog.Tycho2 || !msbSet)
        {
            return false;
        }

        var (tyc1, tyc2, tyc3) = DecodeTyc2CatalogIndex(value);
        return TryGetTycho2StarByTycId(tyc1, (ushort)tyc2, tyc3, out star);
    }

    /// <inheritdoc/>
    public int FindTycho2ByCanonicalPrefix(ReadOnlySpan<char> query, Span<Tycho2PrefixMatch> destination)
    {
        if (_tycho2Data is null || destination.IsEmpty || query.IsEmpty)
        {
            return 0;
        }

        // Parse the dash-segmented query. The segment BEFORE the last dash is
        // always exact (must parse to an integer in tyc range); the TRAILING
        // segment is a string-prefix on the unpadded decimal form, with empty
        // meaning wildcard so "425-" matches every star in tyc1=425.
        ushort? tyc1Exact = null;
        ReadOnlySpan<char> tyc1Prefix = default;
        ushort? tyc2Exact = null;
        ReadOnlySpan<char> tyc2Prefix = default;
        ReadOnlySpan<char> tyc3Prefix = default;
        var hasTyc2Filter = false;
        var hasTyc3Filter = false;

        var dash1 = query.IndexOf('-');
        if (dash1 < 0)
        {
            // 1 segment -> tyc1 string-prefix on the whole query.
            if (!IsAllDigits(query)) return 0;
            tyc1Prefix = query;
        }
        else
        {
            var seg0 = query[..dash1];
            if (!ushort.TryParse(seg0, NumberStyles.None, CultureInfo.InvariantCulture, out var t1)) return 0;
            tyc1Exact = t1;

            var rest1 = query[(dash1 + 1)..];
            var dash2 = rest1.IndexOf('-');
            if (dash2 < 0)
            {
                // 2 segments -> tyc2 prefix; "" is wildcard for "TYC 425-".
                if (!IsAllDigits(rest1)) return 0;
                tyc2Prefix = rest1;
                hasTyc2Filter = true;
            }
            else
            {
                var seg1 = rest1[..dash2];
                if (!ushort.TryParse(seg1, NumberStyles.None, CultureInfo.InvariantCulture, out var t2)) return 0;
                tyc2Exact = t2;

                var rest2 = rest1[(dash2 + 1)..];
                if (rest2.IndexOf('-') >= 0) return 0;  // 4+ segments invalid
                if (!IsAllDigits(rest2)) return 0;
                tyc3Prefix = rest2;
                hasTyc3Filter = true;
            }
        }

        int written = 0;
        var data = _tycho2Data.AsSpan();
        const int entrySize = 17;
        Span<char> tycBuf = stackalloc char[5];  // 5 digits covers ushort.MaxValue

        // Walk streams tyc1=1..streamCount ascending. With tyc1Exact set this
        // visits exactly one stream; with a tyc1 prefix the natural ascending
        // order matches string-prefix iteration (e.g. "1" hits 1, 10..19,
        // 100..199, 1000..1999 in that order).
        for (var tyc1 = 1; tyc1 <= _tycho2StreamCount; tyc1++)
        {
            if (tyc1Exact is { } e1)
            {
                if (tyc1 != e1) continue;
            }
            else
            {
                // tyc1 prefix mode -- format unpadded, check StartsWith.
                if (!((ushort)tyc1).TryFormat(tycBuf, out var len1, default, CultureInfo.InvariantCulture)) continue;
                if (!tycBuf[..len1].StartsWith(tyc1Prefix, StringComparison.Ordinal)) continue;
            }

            var gscIdx = tyc1 - 1;
            var startOffset = BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 1) * 4)..]);
            var endOffset = gscIdx + 1 < _tycho2StreamCount
                ? BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 2) * 4)..])
                : _tycho2Data.Length;
            var entryCount = (endOffset - startOffset) / entrySize;

            for (var i = 0; i < entryCount; i++)
            {
                var entry = data[(startOffset + i * entrySize)..];
                var tyc2 = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                var tyc3 = entry[2];

                if (tyc2Exact is { } e2)
                {
                    // Records sorted by (tyc2, tyc3): once tyc2 > exact we're past
                    // the only possible match in this stream, stop scanning.
                    if (tyc2 < e2) continue;
                    if (tyc2 > e2) break;
                }
                else if (hasTyc2Filter)
                {
                    if (!tyc2.TryFormat(tycBuf, out var len2, default, CultureInfo.InvariantCulture)) continue;
                    if (!tycBuf[..len2].StartsWith(tyc2Prefix, StringComparison.Ordinal)) continue;
                }

                if (hasTyc3Filter)
                {
                    if (!tyc3.TryFormat(tycBuf, out var len3, default, CultureInfo.InvariantCulture)) continue;
                    if (!tycBuf[..len3].StartsWith(tyc3Prefix, StringComparison.Ordinal)) continue;
                }

                // Decode V magnitude only -- B-V isn't surfaced in the search row.
                var vtDecimag = entry[11];
                var btDecimag = entry[12];
                float vMag;
                if (vtDecimag == 0xFF)
                {
                    vMag = float.NaN;
                }
                else
                {
                    var vt = (vtDecimag - 20) / 10.0f;
                    vMag = btDecimag != 0xFF
                        ? vt - 0.090f * ((btDecimag - 20) / 10.0f - vt)
                        : vt;
                }

                destination[written++] = new Tycho2PrefixMatch((ushort)tyc1, tyc2, tyc3, vMag);

                if (written >= destination.Length) return written;
            }
        }

        return written;
    }

    /// <summary>
    /// True when <paramref name="s"/> is empty or contains only ASCII digits.
    /// Empty is intentionally accepted so "TYC 425-" can be parsed as
    /// tyc1=exact, tyc2=wildcard.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is < '0' or > '9') return false;
        }
        return true;
    }

    /// <summary>
    /// Single-walk decode by raw (tyc1, tyc2, tyc3). Unified producer for
    /// both the public <see cref="TryGetTycho2Star"/> and the internal
    /// <see cref="TryGetTycho2RaDec"/> shim that <see cref="TryLookupTycho2StarFromBinaryData"/>
    /// uses to populate a <see cref="CelestialObject"/>. Walking the per-region
    /// table once per lookup and producing every field downstream consumers
    /// might want keeps the byte[]-decode path single-source-of-truth.
    /// </summary>
    private bool TryGetTycho2StarByTycId(ushort tyc1, ushort tyc2, byte tyc3, out Tycho2StarLite star)
    {
        star = default;
        if (_tycho2Data is null || tyc1 == 0 || tyc1 > _tycho2StreamCount)
        {
            return false;
        }

        const int entrySize = 17;
        var data = _tycho2Data.AsSpan();

        var gscIdx = tyc1 - 1;
        var startOffset = BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 1) * 4)..]);
        var endOffset = gscIdx + 1 < _tycho2StreamCount
            ? BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 2) * 4)..])
            : _tycho2Data.Length;
        var entryCount = (endOffset - startOffset) / entrySize;

        // Entries within a tyc1 (GSC region) partition are stored ascending by (tyc2, tyc3) -- the
        // baker sorts each region (see Get-Tycho2Catalogs.ps1; the prefix-search early-exit in
        // SearchTycho2ByPrefix relies on the same invariant). A region can hold thousands of
        // entries and this lookup is hit per-star by the sky-map figure-star seed, plate-solve
        // annotation, and SPCC matching, so binary-search it: O(log n) instead of O(n) per call.
        var lo = 0;
        var hi = entryCount - 1;
        while (lo <= hi)
        {
            var i = lo + ((hi - lo) >> 1);
            var entry = data[(startOffset + i * entrySize)..];
            var entryTyc2 = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            var entryTyc3 = entry[2];

            // Order key is (tyc2, tyc3): compare tyc2 first, tyc3 only on a tie.
            var cmp = entryTyc2 != tyc2 ? entryTyc2.CompareTo(tyc2) : entryTyc3.CompareTo(tyc3);
            if (cmp < 0)
            {
                lo = i + 1;
            }
            else if (cmp > 0)
            {
                hi = i - 1;
            }
            else
            {
                var raHours = BinaryPrimitives.ReadSingleLittleEndian(entry[3..]);
                var decDeg  = BinaryPrimitives.ReadSingleLittleEndian(entry[7..]);
                var vtDecimag = entry[11];
                var btDecimag = entry[12];

                float vMag, bMinusV = 0.65f;
                if (vtDecimag == 0xFF)
                {
                    vMag = float.NaN;
                }
                else
                {
                    var vt = (vtDecimag - 20) / 10.0f;
                    if (btDecimag != 0xFF)
                    {
                        var bt = (btDecimag - 20) / 10.0f;
                        vMag = vt - 0.090f * (bt - vt);
                        bMinusV = 0.850f * (bt - vt);
                    }
                    else
                    {
                        vMag = vt;
                    }
                }

                // pm: int16 inline + int32 sidecar on saturation rails.
                var rawPmRa  = BinaryPrimitives.ReadInt16LittleEndian(entry[13..]);
                var rawPmDec = BinaryPrimitives.ReadInt16LittleEndian(entry[15..]);
                int pmRaTenths, pmDecTenths;
                if (rawPmRa == short.MaxValue || rawPmRa == -short.MaxValue
                 || rawPmDec == short.MaxValue || rawPmDec == -short.MaxValue)
                {
                    if (_tycho2PmSidecar is { } sidecar
                        && sidecar.TryGetValue(PackTycKey(tyc1, tyc2, tyc3), out var exact))
                    {
                        (pmRaTenths, pmDecTenths) = exact;
                    }
                    else
                    {
                        // Defensive fallback: rail without a sidecar entry
                        // shouldn't happen because the PS1 emits a sidecar row
                        // whenever it clips at bake time.
                        pmRaTenths = rawPmRa;
                        pmDecTenths = rawPmDec;
                    }
                }
                else
                {
                    pmRaTenths = rawPmRa;
                    pmDecTenths = rawPmDec;
                }

                star = new Tycho2StarLite(raHours, decDeg, vMag, bMinusV, pmRaTenths, pmDecTenths);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Legacy shim used by <see cref="TryLookupTycho2StarFromBinaryData"/> to
    /// populate a <see cref="CelestialObject"/>. Delegates to the unified
    /// decode and discards pm. Kept so the CelestialObject construction path
    /// doesn't need to know about the new Tycho2StarLite return shape.
    /// </summary>
    private bool TryGetTycho2RaDec(ushort tyc1, ushort tyc2, byte tyc3, out double ra, out double dec, out Half vMag, out float bMinusV)
    {
        if (TryGetTycho2StarByTycId(tyc1, tyc2, tyc3, out var star))
        {
            ra = star.RaHours;
            dec = star.DecDeg;
            vMag = float.IsNaN(star.VMag) ? HalfUndefined : (Half)star.VMag;
            bMinusV = star.BMinusV;
            return true;
        }
        ra = dec = 0;
        vMag = HalfUndefined;
        bMinusV = 0.65f;
        return false;
    }


    /// <inheritdoc/>
    public int HipStarCount => _hipToTyc?.Length ?? 0;

    /// <inheritdoc/>
    public int Tycho2StarCount
    {
        get
        {
            // The lazy is populated by CopyTycho2Stars on its first call; if a
            // caller queries the count BEFORE iterating, the cached field is
            // still 0 even though _tycho2Data is fully loaded. Trigger the
            // count walk on read so the property doc ("populated lazily on
            // first access") is true regardless of call order.
            EnsureTycho2StarCount();
            return _tycho2StarCount;
        }
    }

    /// <summary>
    /// Cached total star count across all Tycho-2 streams. Populated lazily on
    /// first access (via <see cref="Tycho2StarCount"/> or
    /// <see cref="CopyTycho2Stars"/>) once the binary data is loaded.
    /// </summary>
    private int _tycho2StarCount;

    /// <summary>
    /// Ensure <see cref="_tycho2StarCount"/> reflects the loaded binary. Counts
    /// 15-byte records in each per-stream range once and caches the result.
    /// </summary>
    private void EnsureTycho2StarCount()
    {
        if (_tycho2StarCount > 0 || _tycho2Data is null || _tycho2StreamCount == 0)
        {
            return;
        }

        const int entrySize = 17;
        var data = _tycho2Data.AsSpan();
        var total = 0;
        for (int gscIdx = 0; gscIdx < _tycho2StreamCount; gscIdx++)
        {
            var startOffset = BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 1) * 4)..]);
            var endOffset = gscIdx + 1 < _tycho2StreamCount
                ? BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 2) * 4)..])
                : _tycho2Data.Length;
            total += (endOffset - startOffset) / entrySize;
        }
        _tycho2StarCount = total;
    }

    /// <inheritdoc/>
    public int CopyTycho2Stars(Span<Tycho2StarLite> destination, int startIndex = 0)
    {
        EnsureTycho2StarCount();
        if (_tycho2Data is null || destination.IsEmpty || startIndex < 0 || startIndex >= _tycho2StarCount)
        {
            return 0;
        }

        const int entrySize = 17;
        var data = _tycho2Data.AsSpan();

        // Walk streams in order, skipping the first `startIndex` records and then
        // writing until the destination fills or the catalog is exhausted.
        var skip = startIndex;
        var written = 0;
        for (int gscIdx = 0; gscIdx < _tycho2StreamCount && written < destination.Length; gscIdx++)
        {
            var startOffset = BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 1) * 4)..]);
            var endOffset = gscIdx + 1 < _tycho2StreamCount
                ? BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 2) * 4)..])
                : _tycho2Data.Length;
            var entryCount = (endOffset - startOffset) / entrySize;

            if (skip >= entryCount)
            {
                skip -= entryCount;
                continue;
            }

            var streamStart = skip;
            skip = 0;

            for (int i = streamStart; i < entryCount && written < destination.Length; i++)
            {
                var entry = data[(startOffset + i * entrySize)..];
                // Layout: tyc2 (u16) | tyc3 (u8) | RA f32 | Dec f32 | VT decimag (u8)
                //       | BT decimag (u8) | pmRA int16 (mas/yr * 10) | pmDec int16 (mas/yr * 10).
                // Rail value +/-32767 means the star saturated the inline range; the
                // sidecar carries the exact int32 (mas/yr * 10) for those (~11 stars
                // catalog-wide, e.g. Barnard's, Kapteyn's, eps Indi, mu Cas). 0 in the
                // inline field is the "no useful pm" marker (missing OR exactly zero --
                // indistinguishable, but no downstream consumer cares: both yield zero
                // drift under propagation).
                var tyc2 = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                var tyc3 = entry[2];
                var ra  = BinaryPrimitives.ReadSingleLittleEndian(entry[3..]);
                var dec = BinaryPrimitives.ReadSingleLittleEndian(entry[7..]);
                var vtDecimag = entry[11];
                var btDecimag = entry[12];
                var rawPmRa  = BinaryPrimitives.ReadInt16LittleEndian(entry[13..]);
                var rawPmDec = BinaryPrimitives.ReadInt16LittleEndian(entry[15..]);
                int pmRaTenths, pmDecTenths;
                if (rawPmRa == short.MaxValue || rawPmRa == -short.MaxValue
                 || rawPmDec == short.MaxValue || rawPmDec == -short.MaxValue)
                {
                    if (_tycho2PmSidecar is { } sidecar
                        && sidecar.TryGetValue(PackTycKey((ushort)(gscIdx + 1), tyc2, tyc3), out var exact))
                    {
                        (pmRaTenths, pmDecTenths) = exact;
                    }
                    else
                    {
                        // Defensive fallback: shouldn't trigger in practice since every
                        // rail-saturated star was written to the sidecar at bake time.
                        pmRaTenths  = rawPmRa;
                        pmDecTenths = rawPmDec;
                    }
                }
                else
                {
                    pmRaTenths  = rawPmRa;
                    pmDecTenths = rawPmDec;
                }

                float vMag;
                float bv = 0.65f; // solar-type default when blue channel is missing
                if (vtDecimag == 0xFF)
                {
                    // No VT — skip silently by pushing NaN; callers filter on NaN.
                    vMag = float.NaN;
                }
                else
                {
                    var vt = (vtDecimag - 20) / 10.0f;
                    if (btDecimag != 0xFF)
                    {
                        var bt = (btDecimag - 20) / 10.0f;
                        // Johnson V = VT − 0.090 × (BT − VT); B-V = 0.850 × (BT − VT)
                        vMag = vt - 0.090f * (bt - vt);
                        bv = 0.850f * (bt - vt);
                    }
                    else
                    {
                        vMag = vt;
                    }
                }

                destination[written++] = new Tycho2StarLite(ra, dec, vMag, bv, pmRaTenths, pmDecTenths);
            }
        }

        return written;
    }

    /// <inheritdoc/>
    public bool TryLookupHIP(int hipNumber, out double ra, out double dec, out float vMag, out float bv)
    {
        ra = 0;
        dec = 0;
        vMag = float.NaN;
        bv = float.NaN;

        if (TryLookupHIPCore(hipNumber, out var obj))
        {
            ra = obj.RA;
            dec = obj.Dec;
            vMag = (float)obj.V_Mag;
            bv = (float)obj.BMinusV;

            // Fallback for bright stars where SIMBAD has null VMag and no Tycho-2 entry
            if (Half.IsNaN(obj.V_Mag) && HipMagnitudeFallback.TryGetValue(hipNumber, out var fallback))
            {
                vMag = (float)fallback.VMag;
                bv = (float)fallback.BMinusV;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Tier-1-only HIP lookup for bulk star-dot seeding (overrides the
    /// <see cref="ICelestialObjectDB.TryGetHipStarLite"/> default). Reads RA/Dec/photometry
    /// straight from the Tycho-2 array and deliberately skips the per-star
    /// <see cref="ConstellationBoundary.TryFindConstellation"/> polygon test, cross-reference
    /// magnitude refinement, and object-type inheritance that <see cref="TryLookupHIP"/> runs --
    /// none of which a plotted dot needs, and which were ~all of the sky-map HIP seed's cost.
    /// HIP stars with no Tycho-2 entry (the Tier 2-4 fallbacks) are simply omitted from the seed;
    /// the async full Tycho-2 build renders the complete catalogue a beat later. Uses raw Tycho-2
    /// photometry, matching the bulk Tycho-2 vertex path (<c>SkyMapState.FillTycho2StarVertices</c>).
    /// </summary>
    public bool TryGetHipStarLite(int hipNumber, out double ra, out double dec, out float vMag, out float bv)
    {
        ra = 0;
        dec = 0;
        vMag = float.NaN;
        bv = float.NaN;

        if (_hipToTyc is { } hipToTyc && hipNumber > 0 && hipNumber <= hipToTyc.Length)
        {
            var tycIndex = hipToTyc[hipNumber - 1];
            if (tycIndex != 0 && TryGetTycho2RaDec(tycIndex, out ra, out dec, out var vMagHalf, out bv))
            {
                vMag = (float)vMagHalf;
                return true;
            }
        }

        // Tier 2 (a Lightweight build ships no Tycho-2, so the O(1) path above misses for
        // EVERY star): the same bright-star fallback chain TryLookupHIP uses (cross-reference
        // -> HR/HD). On a full build this only runs for the handful of HIP stars without a
        // Tycho-2 counterpart; on Lightweight it resolves the whole ~1000-star figure seed --
        // without it the browser sky map renders zero stars.
        return TryLookupHIP(hipNumber, out ra, out dec, out vMag, out bv);
    }

    /// <summary>
    /// Core HIP resolution with 3-tier fallback:
    /// 1. Tycho-2 array (O(1), covers ~99% of HIP stars)
    /// 2. Cross-reference table (HIP → HR/HD for bright stars loaded via SIMBAD)
    /// Used by both <see cref="TryLookupHIP"/> and <see cref="TryLookupByIndexDirect"/>.
    /// </summary>
    private bool TryLookupHIPCore(CatalogIndex hipIndex, ulong hipValue, out CelestialObject celestialObject)
    {
        // Tier 1: direct Tycho-2 array
        if (TryLookupHIPFromTycho2(hipIndex, hipValue, out celestialObject))
        {
            return true;
        }

        // Tier 2: cross-reference → HR/HD
        if (_crossIndexLookuptable.TryGetLookupEntries(hipIndex, out var crossRefs))
        {
            foreach (var crossRef in crossRefs)
            {
                if (_objectsByIndex.TryGetValue(crossRef, out celestialObject))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryLookupHIPCore(int hipNumber, out CelestialObject celestialObject)
    {
        celestialObject = default;

        // Build HIP CatalogIndex for this number
        var hipIdx = PrefixedNumericToASCIIPackedInt<CatalogIndex>((ulong)Catalog.HIP, hipNumber, Catalog.HIP.GetNumericalIndexSize());
        if (hipIdx == default)
        {
            return false;
        }

        // Tier 1: Tycho-2 array (fastest, covers most HIP stars)
        if (_hipToTyc is not null && hipNumber > 0 && hipNumber <= _hipToTyc.Length)
        {
            var tycIndex = _hipToTyc[hipNumber - 1];
            if (tycIndex != 0 && TryGetTycho2RaDec(tycIndex, out var ra, out var dec, out var vMag, out var bv)
                && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
            {
                // Try to inherit a more specific object type from cross-references (e.g., HR with CStar type)
                var objType = GetInheritedObjectType(hipIdx);
                // Saturated Tycho photometry for the brightest stars is unreliable
                // (Antares -> 1.10, not its Johnson V 0.91). Prefer a curated cross-ref V.
                if (PreferCrossRefMagnitude(vMag) && TryGetCrossRefMagnitude(hipIdx, out var xrefVMag, out var xrefBv))
                {
                    vMag = xrefVMag;
                    bv = (float)xrefBv;
                }
                celestialObject = new CelestialObject(hipIdx, objType, ra, dec, constellation, vMag, HalfUndefined, (Half)bv, EmptyNameSet);
                return true;
            }
        }

        // Tier 2: already loaded in _objectsByIndex as HIP entry
        if (_objectsByIndex.TryGetValue(hipIdx, out celestialObject))
        {
            return true;
        }

        // Tier 3: cross-reference HIP → HR/HD
        if (_crossIndexLookuptable.TryGetLookupEntries(hipIdx, out var crossRefs))
        {
            foreach (var crossRef in crossRefs)
            {
                if (_objectsByIndex.TryGetValue(crossRef, out celestialObject))
                {
                    return true;
                }
            }
        }

        // Tier 4: bright double star components not in Tycho-2.
        // Stellarium's constellation figures reference HIP numbers that map to
        // a different component than what our Tycho-2 data resolves. Look up
        // the corresponding HR entry which has full data (name, magnitude, color).
        if (HipToHrFallback.TryGetValue(hipNumber, out var hrName)
            && CatalogUtils.TryGetCleanedUpCatalogName(hrName, out var hrIdx)
            && TryLookupByIndex(hrIdx, out celestialObject))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// HIP → HR fallback for bright double stars missing from Tycho-2.
    /// These 5 stars have no Tycho-2 entry because they are unresolved double
    /// star components, but their HR entries have full catalog data.
    /// Uses the same <see cref="CatalogUtils.TryGetCleanedUpCatalogName"/> path as
    /// <see cref="ConstellationEx"/> to ensure key format matches <see cref="_objectsByIndex"/>.
    /// </summary>
    /// <summary>
    /// HIP → HR name fallback for bright double stars missing from Tycho-2.
    /// These 5 stars have no Tycho-2 entry because they are unresolved double
    /// star components, but their HR entries have full catalog data (name, mag, color).
    /// </summary>
    private static readonly Dictionary<int, string> HipToHrFallback = new()
    {
        [60718] = "HR 4730",  // α1 Cru (Acrux) — brightest star in Crux
        [65378] = "HR 5054",  // ζ UMa A (Mizar) — Big Dipper handle
        [26727] = "HR 1931",  // η Ori — Orion
        [36850] = "HR 2650",  // ζ Gem (Mekbuda) — Gemini, Cepheid variable
        [50583] = "HR 4359",  // θ Leo (Chertan) — Leo
    };

    /// <summary>
    /// Hardcoded V magnitude and B-V for bright stars where SIMBAD has null VMag
    /// AND no Tycho-2 entry exists (too bright / unresolved binary). Values from
    /// the Bright Star Catalogue (Yale).
    /// </summary>
    private static readonly Dictionary<int, (Half VMag, Half BMinusV)> HipMagnitudeFallback = new()
    {
        [42913] = ((Half)1.96f, (Half)0.04f),  // δ¹ Vel (Alsephina) — eclipsing binary, SIMBAD VMag null
    };

    private bool TryLookupHIPFromTycho2(CatalogIndex hipIndex, ulong hipValue, out CelestialObject celestialObject)
    {
        celestialObject = default;
        var hipNumber = (int)EnumValueToNumeric(hipValue);

        if (_hipToTyc is not null && hipNumber > 0 && hipNumber <= _hipToTyc.Length)
        {
            var tycIndex = _hipToTyc[hipNumber - 1];
            if (tycIndex != 0 && TryGetTycho2RaDec(tycIndex, out var ra, out var dec, out var vMag, out var bv)
                && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
            {
                // Try to inherit a more specific object type from cross-references (e.g., HR with CStar type)
                var objType = GetInheritedObjectType(hipIndex);
                // See TryLookupHIPCore(int): prefer a curated cross-ref V over unreliable
                // saturated Tycho photometry for the brightest stars.
                if (PreferCrossRefMagnitude(vMag) && TryGetCrossRefMagnitude(hipIndex, out var xrefVMag, out var xrefBv))
                {
                    vMag = xrefVMag;
                    bv = (float)xrefBv;
                }
                celestialObject = new CelestialObject(hipIndex, objType, ra, dec, constellation, vMag, HalfUndefined, (Half)bv, EmptyNameSet);
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
            if (tycIndex != 0 && TryGetTycho2RaDec(tycIndex, out var ra, out var dec, out var vMag, out var bv)
                && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
            {
                // Try to inherit a more specific object type from cross-references (e.g., HR with CStar type)
                var objType = GetInheritedObjectType(hdIndex);
                // See TryLookupHIPCore(int): prefer a curated cross-ref V over unreliable
                // saturated Tycho photometry for the brightest stars.
                if (PreferCrossRefMagnitude(vMag) && TryGetCrossRefMagnitude(hdIndex, out var xrefVMag, out var xrefBv))
                {
                    vMag = xrefVMag;
                    bv = (float)xrefBv;
                }
                celestialObject = new CelestialObject(hdIndex, objType, ra, dec, constellation, vMag, HalfUndefined, (Half)bv, EmptyNameSet);
                return true;
            }
        }

        return false;
    }

    private bool TryLookupTycho2StarFromBinaryData(CatalogIndex tycIndex, ulong decodedValue, out CelestialObject celestialObject)
    {
        celestialObject = default;
        var (tyc1, tyc2, tyc3) = DecodeTyc2CatalogIndex(decodedValue);

        if (TryGetTycho2RaDec(tyc1, (ushort)tyc2, (byte)tyc3, out var ra, out var dec, out var vMag, out var bv)
            && ConstellationBoundary.TryFindConstellation(ra, dec, out var constellation))
        {
            celestialObject = new CelestialObject(tycIndex, ObjectType.Star, ra, dec, constellation, vMag, HalfUndefined, (Half)bv, EmptyNameSet);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Stage 1 of NGC loading: decompress the embedded .{name}.gs.gz (preferred)
    /// or legacy .{name}.csv.lz resource. Stateless and CPU-bound, safe to run on
    /// the thread pool in parallel with other work (predefined objects, HR SIMBAD
    /// parse, Tycho2).
    /// </summary>
    private static async Task<(byte[]? Bytes, bool IsGs)> DecompressNgcAsync(Assembly assembly, string[] manifestNames, string ngcName, CancellationToken cancellationToken)
    {
        // Prefer the ASCII-separated *.gs.gz produced by tools/preprocess-catalog.ps1;
        // fall back to *.csv.lz for catalogs that have not been migrated yet.
        var gsManifest = manifestNames.FirstOrDefault(p => p.EndsWith("." + ngcName + ".gs.gz"));
        if (gsManifest is not null && assembly.GetManifestResourceStream(gsManifest) is { } gsStream)
        {
            using (gsStream)
            {
                return (await Task.Run(() => DecompressGzipToBytes(gsStream), cancellationToken), true);
            }
        }

        var csvManifest = manifestNames.FirstOrDefault(p => p.EndsWith("." + ngcName + ".csv.lz"));
        if (csvManifest is null || assembly.GetManifestResourceStream(csvManifest) is not Stream csvStream)
        {
            return (null, false);
        }

        using (csvStream)
        {
            return (await Task.Run(() => LzipDecoder.Decompress(csvStream), cancellationToken), false);
        }
    }

    /// <summary>
    /// Read a gzip-compressed input stream fully into a managed byte[] using the
    /// BCL GZipStream decoder. Used for the *.gs.gz catalog payloads. Caller owns
    /// <paramref name="src"/>; <c>leaveOpen: true</c> avoids double-disposing
    /// when the call site already wraps it in a <c>using</c>.
    /// </summary>
    private static byte[] DecompressGzipToBytes(Stream src)
    {
        using var gz = new GZipStream(src, CompressionMode.Decompress, leaveOpen: true);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Stage 2 of NGC CSV loading: run the CSV reader over pre-decompressed bytes
    /// and merge into the shared dicts. Must stay on the main thread because it
    /// mutates _objectsByIndex / _crossIndexLookuptable. Used only when the
    /// embedded resource is the legacy <c>*.csv.lz</c> form; the new
    /// <c>*.gs.gz</c> path goes through <see cref="MergeNgcGsData"/>.
    /// </summary>
    private (int Processed, int Failed) MergeLzCsvData(byte[]? decompressed, CancellationToken cancellationToken)
    {
        int processed = 0;
        int failed = 0;
        if (decompressed is null)
        {
            return (processed, failed);
        }

        var csvText = new UTF8Encoding(false).GetString(decompressed);
        var csvReader = new CsvFieldReader(csvText, ';');

        while (!cancellationToken.IsCancellationRequested && csvReader.Read())
        {
            if (csvReader.TryGetFieldString("Name", out var entryName)
                && csvReader.TryGetField("Type", out var objectTypeAbbr)
                && objectTypeAbbr is { Length: > 0 }
                && csvReader.TryGetField("RA", out var raHMS)
                && csvReader.TryGetField("Dec", out var decDMS)
                && csvReader.TryGetFieldString("Const", out var constAbbr))
            {
                csvReader.TryGetFieldString("V-Mag", out var vmagStr);
                csvReader.TryGetFieldString("SurfBr", out var surfBrStr);
                csvReader.TryGetFieldString("MajAx", out var majAxStr);
                csvReader.TryGetFieldString("MinAx", out var minAxStr);
                csvReader.TryGetFieldString("PosAng", out var posAngStr);
                csvReader.TryGetFieldString("M", out var messierSuffix);
                csvReader.TryGetFieldString("NGC", out var ngcSuffix);
                csvReader.TryGetFieldString("IC", out var icSuffix);
                csvReader.TryGetFieldString("Common names", out var commonNamesRaw);
                csvReader.TryGetFieldString("Identifiers", out var identifiersRaw);

                // CSV stores Common names / Identifiers as comma-separated, trim-on-read.
                var commonNames = string.IsNullOrWhiteSpace(commonNamesRaw)
                    ? null
                    : commonNamesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var identifiers = string.IsNullOrEmpty(identifiersRaw)
                    ? null
                    : identifiersRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (MergeNgcRow(entryName!, objectTypeAbbr.ToString(), raHMS.ToString(), decDMS.ToString(), constAbbr!,
                        vmagStr ?? "", surfBrStr ?? "", majAxStr ?? "", minAxStr ?? "", posAngStr ?? "",
                        messierSuffix ?? "", ngcSuffix ?? "", icSuffix ?? "",
                        commonNames, identifiers))
                {
                    processed++;
                }
                else
                {
                    failed++;
                }
            }
            else
            {
                failed++;
            }
        }

        return (processed, failed);
    }

    /// <summary>
    /// Stage 2 of NGC loading from the ASCII-separated <c>*.gs.gz</c> format.
    /// Field layout (matches <c>tools/preprocess-catalog.ps1</c> Encode-Ngc):
    /// Name | Type | RA | Dec | Const | VMag | SurfBr | MajAx | MinAx | PosAng |
    /// M | NGC | IC | CommonNames(US-joined) | Identifiers(US-joined).
    /// </summary>
    private (int Processed, int Failed) MergeNgcGsData(byte[]? decompressed, CancellationToken cancellationToken)
    {
        int processed = 0;
        int failed = 0;
        if (decompressed is null)
        {
            return (processed, failed);
        }

        foreach (var recMem in IO.AsciiRecordReader.EnumerateRecords(decompressed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rec = recMem.Span;
            if (rec.IsEmpty) continue;

            var name      = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var type      = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var ra        = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var dec       = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var constAbbr = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var vmag      = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var surfBr    = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var majAx     = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var minAx     = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var posAng    = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var mSuf      = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var ngcSuf    = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var icSuf     = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var commons   = IO.AsciiRecordReader.ReadStringArray(IO.AsciiRecordReader.TakeField(ref rec));
            // Identifiers is the last field; TakeField returns the whole remainder.
            var idents    = IO.AsciiRecordReader.ReadStringArray(IO.AsciiRecordReader.TakeField(ref rec));

            // Required-field validation matches the CSV path: Name and Type must be
            // non-empty; RA / Dec / Const may be blank (HMSToHours/DMSToDegree handle
            // empty strings and a few NGC "NonEx" rows legitimately have no coords).
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(type))
            {
                failed++;
                continue;
            }

            // Empty array on missing optional collections (matches CSV path's null-handling).
            if (MergeNgcRow(name, type, ra, dec, constAbbr,
                    vmag, surfBr, majAx, minAx, posAng,
                    mSuf, ngcSuf, icSuf,
                    commons.Length == 0 ? null : commons,
                    idents.Length == 0 ? null : idents))
            {
                processed++;
            }
            else
            {
                failed++;
            }
        }

        return (processed, failed);
    }

    /// <summary>
    /// Single-source-of-truth per-row merge for NGC (CSV + gs paths). All fields
    /// are pre-resolved as strings; numeric fields use empty-string for missing.
    /// <paramref name="commonNames"/> / <paramref name="identifiers"/> are
    /// already comma-split (CSV path) or US-split (gs path) and trim-cleaned.
    /// Returns true on a row that successfully merged, false on validation fail.
    /// </summary>
    private bool MergeNgcRow(
        string entryName, string objectTypeAbbr, string raHMS, string decDMS, string constAbbr,
        string vmagStr, string surfBrStr, string majAxStr, string minAxStr, string posAngStr,
        string messierSuffix, string ngcSuffix, string icSuffix,
        string[]? commonNamesArr, string[]? identifiers)
    {
        if (!TryGetCleanedUpCatalogName(entryName, out var indexEntry))
        {
            return false;
        }

        var objectType = AbbreviationToEnumMember<OpenNGCObjectType>(objectTypeAbbr).ToObjectType();
        var @const = AbbreviationToEnumMember<Constellation>(constAbbr);

        var vmag = Half.TryParse(vmagStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var vmagFloat)
            ? vmagFloat : HalfUndefined;
        var surfaceBrightness = Half.TryParse(surfBrStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sbFloat)
            ? sbFloat : HalfUndefined;

        IReadOnlySet<string> commonNames = commonNamesArr is { Length: > 0 }
            ? new HashSet<string>(commonNamesArr)
            : EmptyNameSet;

        if (Half.TryParse(majAxStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var majAx)
            && Half.TryParse(minAxStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var minAx))
        {
            var posAng = Half.TryParse(posAngStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var posAngFloat)
                ? posAngFloat : HalfUndefined;
            _shapesByIndex[indexEntry] = new CelestialObjectShape(majAx, minAx, posAng);
        }

        var ra = HMSToHours(raHMS);
        var dec = DMSToDegree(decDMS);
        var obj = _objectsByIndex[indexEntry] = new CelestialObject(
            indexEntry, objectType, ra, dec, @const, vmag, surfaceBrightness, HalfUndefined, commonNames);

        if (objectType == ObjectType.Duplicate)
        {
            // Duplicates are stored in _objectsByIndex for cross-reference resolution
            // but NOT added to the spatial grid — only the primary entry belongs there.
            AddCommonNameIndex(obj.Index, obj.CommonNames);
            // when the entry is a duplicate, use the cross lookup table to list the entries it duplicates
            if (ngcSuffix.Length > 0 && TryGetCleanedUpCatalogName("NGC" + ngcSuffix, out var ngcIndexEntry))
            {
                _crossIndexLookuptable.AddLookupEntry(indexEntry, ngcIndexEntry);
            }
            if (messierSuffix.Length > 0 && TryGetCleanedUpCatalogName("M" + messierSuffix, out var messierIndexEntry))
            {
                _crossIndexLookuptable.AddLookupEntry(indexEntry, messierIndexEntry);
            }
            if (icSuffix.Length > 0 && TryGetCleanedUpCatalogName("IC" + icSuffix, out var icIndexEntry))
            {
                _crossIndexLookuptable.AddLookupEntry(indexEntry, icIndexEntry);
            }
        }
        else
        {
            AddCommonNameAndPosIndices(obj);

            if (icSuffix.Length > 0 && TryGetCleanedUpCatalogName("IC" + icSuffix, out var icIndexEntry) && indexEntry != icIndexEntry)
            {
                _crossIndexLookuptable.AddLookupEntry(icIndexEntry, indexEntry);
                _crossIndexLookuptable.AddLookupEntry(indexEntry, icIndexEntry);
            }
            if (messierSuffix.Length > 0 && TryGetCleanedUpCatalogName("M" + messierSuffix, out var messierIndexEntry) && indexEntry != messierIndexEntry)
            {
                // Adds Messier to NGC/IC entry lookup, but only if its not a duplicate
                _crossIndexLookuptable.AddLookupEntry(messierIndexEntry, indexEntry);
                _crossIndexLookuptable.AddLookupEntry(indexEntry, messierIndexEntry);
                AddCommonNameIndex(messierIndexEntry, commonNames);
            }

            if (identifiers is { Length: > 0 })
            {
                foreach (var identifier in identifiers)
                {
                    if (identifier.Length >= 2
                        && identifier[0] is 'C' or 'M' or 'U' or 'S'
                        && (identifier[1] is 'G' or 'H' or 'e' or 'l' or 'r' or ' ' || char.IsDigit(identifier[1]))
                        && TryGetCleanedUpCatalogName(identifier, out var crossCatIdx)
                        && IsCrossCat(crossCatIdx.ToCatalog()))
                    {
                        _crossIndexLookuptable.AddLookupEntry(crossCatIdx, indexEntry);
                        _crossIndexLookuptable.AddLookupEntry(indexEntry, crossCatIdx);
                    }
                }
            }
        }

        return true;
    }

    static readonly Regex ClusterMemberPattern = ClusterMemberPatternGen();

    /// <summary>
    /// Stage 1 of SIMBAD loading: decompress an embedded SIMBAD catalog and
    /// parse it into a record list. Stateless per file, so many files can run
    /// in parallel on the thread pool without touching the shared dicts.
    /// </summary>
    /// <remarks>
    /// Prefers the ASCII-separated <c>.gs.gz</c> format (produced by
    /// <c>tools/preprocess-catalog.ps1</c>) when an embedded resource matches;
    /// falls back to the legacy <c>.json.lz</c> path for catalogs that have not
    /// yet been migrated. See <c>docs/plans/catalog-binary-format.md</c>.
    /// </remarks>
    private static async Task<List<SimbadCatalogDto>?> ParseSimbadFileAsync(Assembly assembly, string[] manifestNames, string jsonName, CancellationToken cancellationToken)
    {
        var gsManifest = manifestNames.FirstOrDefault(p => p.EndsWith("." + jsonName + ".gs.gz"));
        if (gsManifest is not null && assembly.GetManifestResourceStream(gsManifest) is { } gsStream)
        {
            return await ParseSimbadGsAsync(gsStream, cancellationToken);
        }

        var manifestFileName = manifestNames.FirstOrDefault(p => p.EndsWith("." + jsonName + ".json.lz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return null;
        }

        // Eager byte-array decompression (instead of LzipDecoder.DecompressToStream's
        // lazy wrapper) so the thread-pool task genuinely finishes its decompress work
        // before handing off; that way when we await the task in the merge loop, all
        // CPU-bound work is done and the JSON parse stream reads from memory.
        byte[] decompressed;
        using (stream)
        {
            decompressed = await Task.Run(() => LzipDecoder.Decompress(stream), cancellationToken);
        }

        using var ms = new MemoryStream(decompressed);
        var records = new List<SimbadCatalogDto>(capacity: 4096);
        await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable(ms, SimbadCatalogDtoJsonSerializerContext.Default.SimbadCatalogDto, cancellationToken))
        {
            if (record is not null)
            {
                records.Add(record);
            }
        }
        return records;
    }

    /// <summary>
    /// Decode a SIMBAD <c>.gs.gz</c> stream into <see cref="SimbadCatalogDto"/>
    /// records. Field order is fixed: MainId | ObjType | Ra | Dec | VMag | BMinusV | Ids.
    /// </summary>
    private static async Task<List<SimbadCatalogDto>> ParseSimbadGsAsync(Stream gsStream, CancellationToken cancellationToken)
    {
        byte[] decompressed;
        using (gsStream)
        {
            decompressed = await Task.Run(() => DecompressGzipToBytes(gsStream), cancellationToken);
        }

        var records = new List<SimbadCatalogDto>(capacity: 4096);
        foreach (var recMem in IO.AsciiRecordReader.EnumerateRecords(decompressed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rec = recMem.Span;
            if (rec.IsEmpty) continue;

            var mainId  = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var objType = IO.AsciiRecordReader.ReadString(IO.AsciiRecordReader.TakeField(ref rec));
            var ra      = IO.AsciiRecordReader.ReadDouble(IO.AsciiRecordReader.TakeField(ref rec));
            var dec     = IO.AsciiRecordReader.ReadDouble(IO.AsciiRecordReader.TakeField(ref rec));
            var vmag    = IO.AsciiRecordReader.ReadNullableDouble(IO.AsciiRecordReader.TakeField(ref rec));
            var bmv     = IO.AsciiRecordReader.ReadNullableDouble(IO.AsciiRecordReader.TakeField(ref rec));
            // Last field is Ids — TakeField returns the whole remainder when no trailing RS.
            var ids     = IO.AsciiRecordReader.ReadStringArray(IO.AsciiRecordReader.TakeField(ref rec));

            records.Add(new SimbadCatalogDto(mainId, ids, objType, ra, dec, vmag, bmv));
        }
        return records;
    }

    /// <summary>
    /// Stage 2 of SIMBAD loading: merge a pre-parsed record list into the shared
    /// dictionaries. Must be called serially (in the original file order) because
    /// it reads from and mutates the shared <c>_objectsByIndex</c> +
    /// <c>_crossIndexLookuptable</c>, and because <paramref name="mainCatalogs"/>
    /// grows across iterations.
    /// </summary>
    private (int Processed, int Failed) MergeSimbadRecords(IReadOnlyList<SimbadCatalogDto> records, Catalog catToAdd, HashSet<Catalog> mainCatalogs)
    {
        const string NAME_CAT_PREFIX = "NAME ";
        const string NAME_IAU_CAT_PREFIX = "NAME-IAU ";
        const string STAR_CAT_PREFIX = "* ";
        const string CLUSTER_PREFIX = "Cl ";

        var processed = 0;
        var failed = 0;

        // Reuse collections across records to reduce allocations
        var catToAddIdxs = new SortedSet<CatalogIndex>();
        var relevantIds = new Dictionary<Catalog, CatalogIndex[]>();
        var commonNames = new HashSet<string>(8);
        var bestMatchKeyed = new List<(ulong SortKey, CatalogIndex Resolved)>(8);
        var bestMatches = new List<CatalogIndex>(8);

        foreach (var record in records)
        {
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
                // Populate HIP/HD entries from Simbad data with authoritative coordinates, magnitude and color
                var raInHForStars = record.Ra / 15;
                var simbadVMag = record.VMag is double v ? (Half)v : HalfUndefined;
                var simbadBv = record.BMinusV is double bv ? (Half)bv : HalfUndefined;
                var simbadObjType = AbbreviationToEnumMember<ObjectType>(record.ObjType);
                PopulateSimbadStarEntries(relevantIds, Catalog.HIP, raInHForStars, record.Dec, simbadVMag, simbadBv, simbadObjType);
                PopulateSimbadStarEntries(relevantIds, Catalog.HD, raInHForStars, record.Dec, simbadVMag, simbadBv, simbadObjType);

                // Resolve each identifier through the cross-index table (ResolveToDirectIndex) so
                // alias-only identifiers anchor on their real entry: SIMBAD ties Sh2-25 to the Lagoon
                // solely via "M 8", and M-numbers have no direct entry -- the old bare direct lookup
                // dropped them, leaving such records unlinked duplicates. Deduped because an alias and
                // its target can both appear in one record (M 8 + NGC 6523 -> the same resolved index).
                // Deliberately LINQ-free: this runs once per SIMBAD record across all catalog files, so
                // the reused keyed list + in-place Sort + linear dedup avoid the query-iterator /
                // OrderBy-buffer / Distinct-HashSet allocations. The (SortKey, Resolved) comparer is a
                // total order, so List.Sort's instability is unobservable.
                bestMatchKeyed.Clear();
                foreach (var relevantIdPerCat in relevantIds)
                {
                    var sortKey = relevantIdPerCat.Key switch
                    {
                        Catalog.NGC => 1u,
                        Catalog.IC => 2u,
                        Catalog.Messier => 3u,
                        Catalog.HIP => uint.MaxValue - 1,
                        Catalog.HD => uint.MaxValue,
                        _ => (ulong)relevantIdPerCat.Key
                    };
                    foreach (var relevantId in relevantIdPerCat.Value)
                    {
                        var resolved = ResolveToDirectIndex(relevantId);
                        if (resolved != 0)
                        {
                            bestMatchKeyed.Add((sortKey, resolved));
                        }
                    }
                }
                bestMatchKeyed.Sort(static (a, b) =>
                {
                    var c = a.SortKey.CompareTo(b.SortKey);
                    // Compare as ulong: the enum's non-generic CompareTo(object) would box.
                    return c != 0 ? c : ((ulong)a.Resolved).CompareTo((ulong)b.Resolved);
                });
                bestMatches.Clear();
                foreach (var (_, resolved) in bestMatchKeyed)
                {
                    // Tiny list (a handful of identifiers per record): linear Contains beats a HashSet.
                    if (!bestMatches.Contains(resolved))
                    {
                        bestMatches.Add(resolved);
                    }
                }

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
                                // Compare in RESOLVED form: an alias (M 8) that entered bestMatches as
                                // its direct entry (NGC 6523) is already linked -- don't re-link it raw.
                                var resolvedRelevant = ResolveToDirectIndex(relevantId);
                                if (!bestMatches.Contains(resolvedRelevant != 0 ? resolvedRelevant : relevantId))
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
                            var obj = _objectsByIndex[catToAddIdx] = new CelestialObject(catToAddIdx, objType, raInH, record.Dec, constellation, simbadVMag, HalfUndefined, simbadBv, setOrEmpty);

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
                obj.BMinusV,
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

    /// <summary>
    /// Load a per-catalog <c>*.shapes.json.lz</c> companion file if present in
    /// embedded resources, stamping each <see cref="CelestialObjectShape"/> onto
    /// the matching catalog index and fanning it out to every known cross-reference
    /// of the same physical object. Silent no-op when the file does not exist.
    /// Uses <see cref="Dictionary{TKey,TValue}.TryAdd"/> throughout so higher-
    /// quality shapes populated earlier (e.g. OpenNGC CSV for NGC/IC, or an
    /// earlier entry in the shape-source priority list) are never overwritten
    /// by the coarser circular approximation derived from VizieR area/diameter
    /// fields. See <c>Get-VizierDarkNebulaShapes.ps1</c>.
    /// </summary>
    private async Task<int> ReadCatalogObjectShapesAsync(Assembly assembly, string[] manifestNames, string jsonName, Catalog catToAdd, CancellationToken cancellationToken)
    {
        var manifestFileName = manifestNames.FirstOrDefault(p => p.EndsWith("." + jsonName + ".shapes.json.lz"));
        if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
        {
            return 0;
        }

        using (stream)
        using (var decompressed = LzipDecoder.DecompressToStream(stream))
        {
            var digits = catToAdd.GetNumericalIndexSize();
            var applied = 0;
            await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable(decompressed, CatalogObjectShapeJsonSerializerContext.Default.CatalogObjectShape, cancellationToken))
            {
                if (record is null) continue;
                var idx = PrefixedNumericToASCIIPackedInt<CatalogIndex>((ulong)catToAdd, record.Seq, digits);
                var shape = new CelestialObjectShape((Half)record.Maj, (Half)record.Min, (Half)record.PA);
                if (_shapesByIndex.TryAdd(idx, shape))
                {
                    applied++;
                }
                // Fan the shape out to every known cross-reference of the same
                // physical object so the overlay scan finds the same ellipse
                // regardless of which catalog variant it happened to visit first.
                if (_crossIndexLookuptable.TryGetLookupEntries(idx, out IReadOnlyList<CatalogIndex> xrefs))
                {
                    for (var i = 0; i < xrefs.Count; i++)
                    {
                        var x = xrefs[i];
                        if (x != default)
                        {
                            _shapesByIndex.TryAdd(x, shape);
                        }
                    }
                }
            }
            return applied;
        }
    }

    [GeneratedRegex(@"^[A-Za-z]+\s+\d+\s+\d+$", RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex ClusterMemberPatternGen();
}
