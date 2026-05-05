using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class RaDecIndexBenchmarks
{
    private RaDecIndex _primaryIndex = null!;
    private IRaDecIndex _compositeIndex = null!;
    private (double Ra, double Dec)[] _sampleCoords = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var db = new CelestialObjectDB();
        // CoordinateGrid composes the Tycho-2 spatial index — wait for the bulk load so
        // the benchmark exercises the full grid rather than the deep-sky-only fallback.
        await db.InitDBAsync(waitForTycho2BulkLoad: true);

        _primaryIndex = db.PrimaryRaDecIndex;
        _compositeIndex = db.CoordinateGrid;

        // Generate a representative set of coordinates covering the whole sky
        var rng = new Random(42);
        _sampleCoords = new (double, double)[1000];
        for (var i = 0; i < _sampleCoords.Length; i++)
        {
            _sampleCoords[i] = (rng.NextDouble() * 24.0, rng.NextDouble() * 180.0 - 90.0);
        }
    }

    [Benchmark(Baseline = true)]
    public int PrimaryOnly_Lookup()
    {
        var total = 0;
        foreach (var (ra, dec) in _sampleCoords)
        {
            total += _primaryIndex[ra, dec].Count;
        }
        return total;
    }

    [Benchmark]
    public int Composite_WithTycho2_Lookup()
    {
        var total = 0;
        foreach (var (ra, dec) in _sampleCoords)
        {
            total += _compositeIndex[ra, dec].Count;
        }
        return total;
    }

    [Benchmark]
    public int PrimaryOnly_Enumerate()
    {
        var total = 0;
        foreach (var (ra, dec) in _sampleCoords)
        {
            foreach (var _ in _primaryIndex[ra, dec])
            {
                total++;
            }
        }
        return total;
    }

    [Benchmark]
    public int Composite_WithTycho2_Enumerate()
    {
        var total = 0;
        foreach (var (ra, dec) in _sampleCoords)
        {
            foreach (var _ in _compositeIndex[ra, dec])
            {
                total++;
            }
        }
        return total;
    }
}
