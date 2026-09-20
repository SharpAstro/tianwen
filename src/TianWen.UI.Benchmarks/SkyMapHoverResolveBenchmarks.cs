using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Measures <see cref="SkyMapSearchActions.ResolveHoverAtScreenPoint"/>, the search behind the sky
/// map's hover wash -- and, through the same <c>TryResolveHit</c>, behind every click that selects an
/// object.
///
/// <para><b>Why this exists.</b> The hover highlight shipped with a timing table quoted in four
/// places (CLAUDE.md, TODO.md, docs/todo/ui.md and the PR), taken from a throwaway probe that was
/// deleted, ran under <c>dotnet test</c> in <b>Debug</b>, and reported a single number per field of
/// view with no variance. The number drove a real design decision -- the once-per-painted-frame
/// throttle -- so it should be reproducible. This is the same correction
/// <see cref="SkyMapOverlayGatherBenchmarks"/> records for the logged 105-160 ms gather: a Debug
/// figure is not the figure the application runs at.</para>
///
/// <para><b>Why the pointing is a parameter and not an average.</b> The resolve has two paths that
/// differ by an order of magnitude, and the old number blended them. The DSO pass runs first over a
/// 3x3 window of spatial-index cells; <b>if it matches, the star pass never runs at all</b>. So a
/// pointer resting ON a catalogued object is cheap however dense the sky is, while a pointer on
/// sky with no DSO under it pays the full star walk. Only the second bounds the per-frame budget,
/// and only the second is worth quoting as a worst case.</para>
///
/// <para><b>Why the FOV ladder is the axis.</b> Not geometry: the cell window is 3x3 at every zoom.
/// It is <see cref="SkyMapState.EffectiveMagnitudeLimit"/>, which widens as you zoom in, so the star
/// pass admits a larger share of Tycho-2 from the same cells. That is why the cost runs the opposite
/// way to intuition, being worst zoomed IN.</para>
///
/// <para>Run with:
/// <c>dotnet run -c Release --project TianWen.UI.Benchmarks -- --filter *HoverResolve*</c>.
/// Quote the result with the machine it came off (this repo's dev box is win-arm64); a ms figure
/// without one is not a fact about the code.</para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SkyMapHoverResolveBenchmarks
{
    private CelestialObjectDB _db = null!;
    private DateTimeOffset _viewingUtc;

    // The same 1600x1000 viewport the gather benchmark uses, so the two are read against one shape.
    private static readonly RectF32 ContentRect = new(0f, 0f, 1600f, 1000f);

    /// <summary>
    /// Zoomed right in, zoomed out, and the two either side. The magnitude limit is what moves with
    /// it, so this is the ladder the star pass actually feels.
    /// </summary>
    [Params(1.0, 10.0, 60.0, 170.0)]
    public double FovDeg { get; set; }

    private SkyMapState _onObject = null!;
    private SkyMapState _onStarField = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _db = new CelestialObjectDB();

        // The BULK Tycho-2 load is the point here, unlike the gather benchmark where it is only
        // realism: the star pass walks the composite index, so without it this would measure a
        // catalogue that is not the one the application has resident.
        await _db.InitDBAsync(waitForTycho2BulkLoad: true);

        _viewingUtc = new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.Zero);

        // M8, the Lagoon: a large catalogued nebula, so the DSO pass matches at the view centre and
        // the star pass is skipped. The cheap path.
        _onObject = StateAt(raHours: 18.0637, decDeg: -24.3867);

        // A dense Milky Way star field in Aquila with no large catalogued nebula at the centre, so
        // the DSO pass misses and the full star walk runs over the densest sky there is. The
        // expensive path, and the one the throttle exists for.
        _onStarField = StateAt(raHours: 19.5, decDeg: 5.0);
    }

    private static SkyMapState StateAt(double raHours, double decDeg)
    {
        var state = new SkyMapState
        {
            Mode = SkyMapMode.Equatorial,
            CenterRA = raHours,
            CenterDec = decDeg,
            // Both layers on: the click gate asks them, and a resolve against a switched-off overlay
            // would measure the early-out rather than the search.
            ShowObjectOverlay = true,
            ShowDarkNebulae = true,
            LastContentRect = ContentRect,
        };

        return state;
    }

    private SkyMapHoverTarget? Resolve(SkyMapState state)
    {
        // Set per iteration because the FOV parameter drives the matrix and the magnitude limit.
        state.FieldOfViewDeg = FovDeg;
        state.CurrentViewMatrix = state.ComputeViewMatrix();

        return SkyMapSearchActions.ResolveHoverAtScreenPoint(
            state, _db, _viewingUtc,
            ContentRect.Width * 0.5f, ContentRect.Height * 0.5f,
            pinnedCatalogIndices: null);
    }

    /// <summary>Pointer on a catalogued nebula: the DSO pass matches and the star pass is skipped.</summary>
    [Benchmark(Baseline = true)]
    public SkyMapHoverTarget? Resolve_OverObject() => Resolve(_onObject);

    /// <summary>
    /// Pointer on a dense star field with no DSO under it: the full star walk runs. This is the
    /// number that bounds the per-frame budget.
    /// </summary>
    [Benchmark]
    public SkyMapHoverTarget? Resolve_OverStarField() => Resolve(_onStarField);
}
