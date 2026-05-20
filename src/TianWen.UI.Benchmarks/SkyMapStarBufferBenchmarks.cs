using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Measures the CPU portion of the sky-map star buffer build -- the per-star
/// loop in <see cref="SkyMapState.FillTycho2StarVertices"/> that walks all
/// 2.5M Tycho-2 entries via <see cref="ICelestialObjectDB.CopyTycho2Stars"/>,
/// applies <see cref="TianWen.Lib.Astrometry.CoordinateUtils.PropagatePm"/>
/// when <c>dtJulianYears != 0</c>, converts each surviving star to a unit
/// vector via <see cref="SkyMapState.RaDecToUnitVec"/>, and emits the 5-float
/// vertex record. Excludes the downstream sort + magnitude-lookup + Vulkan
/// GPU upload so this can run headless without a render context.
/// <para>
/// Backstory: after Phase D landed pm-propagation here, an interactive
/// sky-map crossover from May 31 to June 1 was clocked at ~750 ms total
/// (rebuild including upload). The CPU loop is suspected to dominate; this
/// bench gives a clean isolated number so we know what we're optimising
/// against before we try anything (Parallel.For, SIMD, ...).
/// </para>
/// <para>
/// Run with: <c>dotnet run -c Release --project TianWen.UI.Benchmarks -- --filter *SkyMapStarBuffer*</c>.
/// </para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SkyMapStarBufferBenchmarks
{
    private CelestialObjectDB _db = null!;
    private float[] _scratch = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _db = new CelestialObjectDB();
        // waitForTycho2BulkLoad: true -- we need the full ~2.5M-entry binary
        // catalogue for this bench, not just the bright-star fallback.
        await _db.InitDBAsync(waitForTycho2BulkLoad: true);
        // Pre-allocated destination buffer reused across iterations. Each
        // iteration overwrites the same slots so the bench measures the
        // per-star CPU work, not allocator churn.
        _scratch = new float[_db.Tycho2StarCount * SkyMapState.FloatsPerStar];
    }

    /// <summary>
    /// dtYr = 0 means the helper skips its pm branch entirely (the
    /// <c>applyPm</c> flag short-circuits), so this measures the pure
    /// <c>RaDecToUnitVec</c> + <c>CopyTycho2Stars</c> loop -- the cost we
    /// had before Phase D wired pm in. Baseline.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int FillVertices_NoPropagation()
        => SkyMapState.FillTycho2StarVertices(_db, dtJulianYears: 0.0, _scratch);

    /// <summary>
    /// dtYr = 26.4 yr matches J2000 -&gt; mid-2026, the typical viewing
    /// epoch today. Every non-zero-pm star (~95% of Tycho-2) takes the
    /// <c>PropagatePm</c> branch. The ratio against the baseline shows the
    /// real cost of pm correction.
    /// </summary>
    [Benchmark]
    public int FillVertices_PropagatedToToday()
        => SkyMapState.FillTycho2StarVertices(_db, dtJulianYears: 26.4, _scratch);

    /// <summary>
    /// dtYr = 0.083 (about one month) -- bounds the smallest meaningful
    /// shift the month-cache emits. Useful for confirming the per-star
    /// pm cost is constant in dt (which it should be: same arithmetic
    /// regardless of dt magnitude).
    /// </summary>
    [Benchmark]
    public int FillVertices_PropagatedOneMonth()
        => SkyMapState.FillTycho2StarVertices(_db, dtJulianYears: 1.0 / 12.0, _scratch);
}
