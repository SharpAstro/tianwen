using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="CelestialObjectDB"/> — init cost, direct index lookup,
/// name resolution, and cross-index traversal. The flaky
/// <c>CelestialObjectDBBenchmarkTests.GivenInitializedDBWhenLookingUpCrossIndicesThenItIsMuchFasterThanInit</c>
/// uses Stopwatch + ratio assertions; this project is the real home for the
/// numbers. Run with: <c>dotnet run -c Release --project TianWen.UI.Benchmarks
/// -- --filter *CelestialObjectDB*</c>.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CelestialObjectDBBenchmarks
{
    private CelestialObjectDB _db = null!;
    private CatalogIndex[] _lookupIndices = null!;
    private CatalogIndex[] _crossIndices = null!;
    private string[] _commonNames = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _db = new CelestialObjectDB();
        // The lookup benchmarks below probe NGC/M/HIP/HR/vdB only — none hit the Tycho-2
        // binary directly — so the default fast-path init (Tycho-2 bulk in background) is
        // sufficient for setup. Tests that exercise CoordinateGrid / CopyTycho2Stars need
        // waitForTycho2BulkLoad: true.
        await _db.InitDBAsync();

        // Direct-lookup indices: common bright targets and stars across catalogs.
        _lookupIndices =
        [
            CatalogIndex.NGC3372, CatalogIndex.M042, CatalogIndex.Mel022,
            CatalogIndex.HIP016537, CatalogIndex.IC4703, CatalogIndex.C041,
        ];

        // Cross-index targets: entries that span multiple catalogs (NGC/M/HIP/etc)
        // so the traversal actually has edges to walk.
        _crossIndices =
        [
            CatalogIndex.NGC3372, CatalogIndex.M042, CatalogIndex.HIP016537,
            CatalogIndex.vdB0020, CatalogIndex.HR1084, CatalogIndex.Mel022,
        ];

        _commonNames =
        [
            "Pleiades", "Orion Nebula", "Carina Nebula",
            "Ran", "Electra", "Keyhole",
        ];
    }

    /// <summary>
    /// Default-path init: Tycho-2 bulk decode runs in the background and is NOT awaited
    /// before InitDBAsync returns. This is the cost the typical caller sees (Planner,
    /// session bootstrap). Compare against <see cref="InitDB_FullyLoaded"/> to see how
    /// much wall time the deferred Tycho-2 decode buys.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task<int> InitDB()
    {
        var db = new CelestialObjectDB();
        await db.InitDBAsync();
        return db.LastInitProcessed;
    }

    /// <summary>
    /// Full init: also awaits the bulk Tycho-2 decode before returning. This is what
    /// callers that touch CoordinateGrid / CopyTycho2Stars / Tycho-2 spatial queries
    /// pay if they choose to gate startup on the data instead of awaiting
    /// EnsureTycho2DataLoadedAsync at the call site.
    /// </summary>
    [Benchmark]
    public async Task<int> InitDB_FullyLoaded()
    {
        var db = new CelestialObjectDB();
        await db.InitDBAsync(waitForTycho2BulkLoad: true);
        return db.LastInitProcessed;
    }

    [Benchmark]
    public int TryLookupByIndex()
    {
        var hits = 0;
        foreach (var idx in _lookupIndices)
        {
            if (_db.TryLookupByIndex(idx, out _))
                hits++;
        }
        return hits;
    }

    [Benchmark]
    public int TryResolveCommonName()
    {
        var hits = 0;
        foreach (var name in _commonNames)
        {
            if (_db.TryResolveCommonName(name, out _))
                hits++;
        }
        return hits;
    }

    /// <summary>
    /// The original flaky test watches this one. It does a breadth-first walk over
    /// the cross-index graph — allocates a HashSet + List per call today, so the
    /// MemoryDiagnoser output tells us exactly what a typical traversal costs.
    /// </summary>
    [Benchmark]
    public int TryGetCrossIndices()
    {
        var total = 0;
        foreach (var idx in _crossIndices)
        {
            if (_db.TryGetCrossIndices(idx, out var cross))
                total += cross.Count;
        }
        return total;
    }
}
