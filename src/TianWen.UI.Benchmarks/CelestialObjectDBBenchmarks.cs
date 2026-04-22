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
        await _db.InitDBAsync(default);

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
    /// Baseline: cost of a one-shot DB initialisation. This is not a per-op
    /// benchmark; comparing Lookup/CrossIndex/Name to this tells us how close
    /// steady-state lookups approach the theoretical per-entry insertion cost.
    /// </summary>
    [Benchmark]
    public async Task<int> InitDB()
    {
        var db = new CelestialObjectDB();
        var (processed, _) = await db.InitDBAsync(default);
        return processed;
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
