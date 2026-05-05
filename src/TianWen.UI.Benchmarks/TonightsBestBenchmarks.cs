using System;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class TonightsBestBenchmarks
{
    private CelestialObjectDB _db = null!;
    private Transform _viennaSummer = null!;
    private Transform _viennaWinter = null!;
    private Transform _melbourneSummer = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _db = new CelestialObjectDB();
        await _db.InitDBAsync();

        _viennaSummer = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = 48.2,
            SiteLongitude = 16.4,
            SiteElevation = 200,
            SiteTemperature = 15,
            DateTimeOffset = new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.FromHours(2))
        };

        _viennaWinter = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = 48.2,
            SiteLongitude = 16.4,
            SiteElevation = 200,
            SiteTemperature = -5,
            DateTimeOffset = new DateTimeOffset(2025, 12, 15, 20, 0, 0, TimeSpan.FromHours(1))
        };

        _melbourneSummer = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = -37.8,
            SiteLongitude = 145.0,
            SiteElevation = 30,
            SiteTemperature = 20,
            DateTimeOffset = new DateTimeOffset(2025, 1, 15, 22, 0, 0, TimeSpan.FromHours(11))
        };
    }

    [Params(10, 20)]
    public byte MinHeight { get; set; }

    [Benchmark(Baseline = true)]
    public int ViennaSummer_Top50()
    {
        return ObservationScheduler.TonightsBest(_db, _viennaSummer, MinHeight).Take(50).Count();
    }

    [Benchmark]
    public int ViennaWinter_Top50()
    {
        return ObservationScheduler.TonightsBest(_db, _viennaWinter, MinHeight).Take(50).Count();
    }

    [Benchmark]
    public int MelbourneSummer_Top50()
    {
        return ObservationScheduler.TonightsBest(_db, _melbourneSummer, MinHeight).Take(50).Count();
    }

    [Benchmark]
    public int ViennaSummer_All()
    {
        return ObservationScheduler.TonightsBest(_db, _viennaSummer, MinHeight).Count();
    }
}
