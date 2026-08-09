using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Measures Phase A of the sky-map object overlay:
/// <see cref="OverlayEngine.GatherSkyMapOverlayCandidates"/>, the spatial-grid walk that
/// turns the current view into a candidate list. Phase B (per-frame projection + label
/// placement) is excluded; it runs on the render thread and is separately cheap.
/// <para>
/// <b>Why this exists.</b> A GUI session logged <c>skymap.gather(async) 105-160ms</c> at wide
/// FOV, which looked wrong on a 16-core desktop next to the GPU numbers in the same log (2.56M
/// stars uploaded in 6 ms). But those come from a Debug <c>dotnet run</c>, and the diagnostic's
/// stopwatch starts on the render thread <i>before</i> <c>Task.Run</c>, so it also charges
/// thread-pool dispatch to the walk. GPU speed cannot matter either way: this is a pure CPU
/// pass over the immutable catalog DB. This bench isolates the walk itself, optimised, so the
/// question is settled with a number instead of an inference.
/// </para>
/// <para>
/// The FOV ladder matters because the scan bounds widen with it, and past ~90 degrees (or with
/// a pole in view) the walk degenerates to a full-sky sweep -- so the wide cases are where cost
/// concentrates, and the pole case is the documented worst one.
/// </para>
/// <para>
/// Run with: <c>dotnet run -c Release --project TianWen.UI.Benchmarks -- --filter *OverlayGather*</c>.
/// </para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SkyMapOverlayGatherBenchmarks
{
    private CelestialObjectDB _db = null!;
    private readonly List<OverlayCandidate> _output = new(4096);

    // A 1600x1000 content rect at dpiScale 1 approximates the sky-map viewport in the session
    // that produced the log line above.
    private static readonly RectF32 ContentRect = new(0f, 0f, 1600f, 1000f);

    private Matrix4x4 _view20;
    private Matrix4x4 _view60;
    private Matrix4x4 _view94;
    private Matrix4x4 _viewPole;

    [GlobalSetup]
    public async Task Setup()
    {
        _db = new CelestialObjectDB();
        // The overlay walks DSOs, not the Tycho-2 bulk table, so the bulk load is not needed
        // for correctness here -- but it IS what the running app has resident, and skipping it
        // would flatter the numbers if anything shares a code path.
        await _db.InitDBAsync(waitForTycho2BulkLoad: true);

        // Centre on a dense region (galactic plane near Sagittarius) so the candidate counts
        // resemble the logged ones rather than an empty patch of sky.
        _view20 = ViewAt(raHours: 18.0, decDeg: -25.0);
        _view60 = _view20;
        _view94 = _view20;
        _viewPole = ViewAt(raHours: 0.0, decDeg: 90.0);
    }

    private static Matrix4x4 ViewAt(double raHours, double decDeg)
    {
        var state = new SkyMapState { CenterRA = raHours, CenterDec = decDeg };
        return state.ComputeViewMatrix();
    }

    private int Gather(in Matrix4x4 view, double fovDeg)
    {
        OverlayEngine.GatherSkyMapOverlayCandidates(
            view, fovDeg, ContentRect, dpiScale: 1f, _db, pinnedCatalogIndices: null, _output);
        return _output.Count;
    }

    /// <summary>Typical framing FOV; the log showed 10-15 ms here.</summary>
    [Benchmark(Baseline = true)]
    public int Gather_Fov20() => Gather(_view20, 20.0);

    /// <summary>The default opening FOV.</summary>
    [Benchmark]
    public int Gather_Fov60() => Gather(_view60, 60.0);

    /// <summary>Widest observed in the session; the log showed 105-160 ms here.</summary>
    [Benchmark]
    public int Gather_Fov94() => Gather(_view94, 94.0);

    /// <summary>
    /// Pole in view forces the full RA/Dec sweep regardless of FOV -- the case the source
    /// comment calls out as the worst.
    /// </summary>
    [Benchmark]
    public int Gather_PoleInView_Fov60() => Gather(_viewPole, 60.0);
}
