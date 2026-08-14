using System;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Guards the two per-instant ephemeris caches on <see cref="SkyMapState"/> (planet positions, comet
/// markers) against the failure they actually suffered: a cache key that was correct only because the
/// PRODUCER happened to round its input, silently defeated the day the producer stopped.
///
/// <para>Both were keyed on exact <see cref="DateTimeOffset"/> equality, which held while
/// <c>SkyMapTab.Render</c> fed a viewingTime taken from a 1 s clock cache. It later began
/// interpolating between those syncs -- correctly, because a once-a-second step moved the view matrix
/// in visible 1 Hz jumps -- and from then on every frame carried a distinct instant and both caches
/// missed 100% of the time. Nothing failed: the markers were right, just recomputed. The comet sweep
/// is ~1,600 candidates each needing a ~3,500-term VSOP87a Earth series, so it cost 91 ms PER FRAME,
/// on every pointer move in the browser and on every frame of the desktop GPU map.</para>
///
/// <para>So the load-bearing test here is <see cref="ASecondOfLiveFramesCostsOneEphemerisSweep"/>: it
/// drives the REAL <see cref="SkyMapTab{TSurface}.Render"/> over a fake clock instead of feeding
/// hand-picked timestamps to the cache. Hand-picked timestamps would re-encode the very assumption
/// that broke -- a test that asserts what I think the producer emits cannot notice the producer
/// changing. Driving the real render couples the two sides, so the next person to touch the clock
/// derivation gets a red test rather than a silent 91 ms.</para>
/// </summary>
[Collection("Astrometry")]
public sealed class SkyMapEphemerisCacheTests
{
    // Mirrors the OverlayTestSkyMapTab harness: a SkyMapTab over the CPU surface that publishes the
    // view matrix each frame the way the real GPU pipelines do. Nothing here overrides the label
    // passes, which is the point -- DrawPlanetLabels / DrawCometLabels are shared base-class code, so
    // this exercises the same path the Vulkan desktop tab and the WebGL browser tab both run.
    private sealed class CacheTestSkyMapTab(RgbaImageRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect,
            DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site)
        {
            base.RenderSkyMap(db, contentRect, viewingTime, siteLat, siteLon, site);
            State.CurrentViewMatrix = State.ComputeViewMatrix();
        }
    }

    private const int Size = 400;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16); // ~60 fps

    private static PlannerState LiveState(ICelestialObjectDB db) => new()
    {
        ObjectDb = db,
        SiteLatitude = 48.0,
        SiteLongitude = 11.0,
        SiteTimeZone = TimeSpan.FromHours(1),
        // PlanningDate stays NULL on purpose: that is live mode, the only branch that interpolates the
        // clock, and so the only one where this regression is reachable. A planner date pins
        // viewingTime to a constant and would hit any cache key at all, including the broken one.
        Comets = new StubCometRepository(
            StubCometRepository.Comet("12P", "Pons-Brooks"),
            StubCometRepository.Comet("C/2023 A3", "Tsuchinshan-ATLAS")),
    };

    /// <summary>
    /// THE regression test. Sixty consecutive live frames spanning 944 ms must cost ONE planet
    /// reduction and ONE comet sweep between them -- not sixty of each.
    /// </summary>
    [Fact]
    public async Task ASecondOfLiveFramesCostsOneEphemerisSweep()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        using var renderer = new RgbaImageRenderer(Size, Size);
        var tab = new CacheTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var state = LiveState(db);
        var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 8, 6, 21, 0, 0, TimeSpan.Zero));
        var content = new RectF32(0, 0, Size, Size);

        for (var frame = 0; frame < 60; frame++)
        {
            tab.Render(state, content, time);
            time.Advance(FrameInterval);
        }

        // Exactly one, not "a small number": 60 frames x 16 ms = 944 ms sits inside a single refresh
        // window, so the arithmetic is deterministic and a loose bound would hide a partial regression.
        tab.State.CometCacheRebuilds.ShouldBe(1);
        tab.State.PlanetCacheRebuilds.ShouldBe(1);
    }

    /// <summary>
    /// The other half of the guard: a cache that NEVER refreshed would also pass the test above, and
    /// would freeze every planet and comet on the map at the instant the atlas was opened.
    /// </summary>
    [Fact]
    public async Task PastTheRefreshWindowTheEphemerisIsRecomputed()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        using var renderer = new RgbaImageRenderer(Size, Size);
        var tab = new CacheTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var state = LiveState(db);
        var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 8, 6, 21, 0, 0, TimeSpan.Zero));
        var content = new RectF32(0, 0, Size, Size);

        tab.Render(state, content, time);
        tab.State.CometCacheRebuilds.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(3));
        tab.Render(state, content, time);

        tab.State.CometCacheRebuilds.ShouldBe(2);
        tab.State.PlanetCacheRebuilds.ShouldBe(2);
    }

    /// <summary>
    /// Keeps the two tests above from being vacuous. They count sweeps, and a sweep over an empty
    /// candidate set is free -- so if the stub repository's comets stopped passing the candidacy gate,
    /// or the propagator stopped converging for them, the counters would still read 1 and the guard
    /// would quietly stop guarding anything.
    /// </summary>
    [Fact]
    public void TheSweptCandidateSetIsNotEmpty()
    {
        var state = new SkyMapState();
        var comets = new StubCometRepository(
            StubCometRepository.Comet("12P", "Pons-Brooks"),
            StubCometRepository.Comet("C/2023 A3", "Tsuchinshan-ATLAS"));

        var markers = state.GetCometPositionsCached(comets, new DateTimeOffset(2026, 8, 6, 21, 0, 0, TimeSpan.Zero));

        markers.Length.ShouldBe(2);
        state.CometCacheRebuilds.ShouldBe(1);
        foreach (var marker in markers)
        {
            double.IsNaN(marker.RA).ShouldBeFalse(marker.Label);
            double.IsNaN(marker.Dec).ShouldBeFalse(marker.Label);
            float.IsNaN(marker.VMag).ShouldBeFalse(marker.Label);
        }
    }

    /// <summary>
    /// A time scrub or a planner-date change jumps far past the refresh window, so it must land on the
    /// new instant immediately rather than showing up to a second late. This is the behaviour the
    /// tolerance has to preserve, and the reason it is a second rather than something generous.
    /// </summary>
    [Fact]
    public void AScrubbedInstantIsResolvedAtOnce()
    {
        var state = new SkyMapState();
        var comets = new StubCometRepository(StubCometRepository.Comet("12P", "Pons-Brooks"));
        var start = new DateTimeOffset(2026, 8, 6, 21, 0, 0, TimeSpan.Zero);

        var before = state.GetCometPositionsCached(comets, start)[0];
        // A month later: the comet has to have moved, and the planets with it.
        var after = state.GetCometPositionsCached(comets, start.AddDays(30))[0];

        state.CometCacheRebuilds.ShouldBe(2);
        after.RA.ShouldNotBe(before.RA);

        state.GetPlanetPositionsCached(start);
        state.GetPlanetPositionsCached(start.AddDays(30));
        state.PlanetCacheRebuilds.ShouldBe(2);
    }

    /// <summary>
    /// Swapping in a different repository invalidates the markers even at the same instant -- the
    /// candidate set is drawn from it, so an unchanged clock is not enough to make the cache valid.
    /// </summary>
    [Fact]
    public void ADifferentRepositoryInvalidatesTheMarkersAtTheSameInstant()
    {
        var state = new SkyMapState();
        var when = new DateTimeOffset(2026, 8, 6, 21, 0, 0, TimeSpan.Zero);

        state.GetCometPositionsCached(new StubCometRepository(StubCometRepository.Comet("12P", "Pons-Brooks")), when)
            .Length.ShouldBe(1);
        state.GetCometPositionsCached(
            new StubCometRepository(
                StubCometRepository.Comet("12P", "Pons-Brooks"),
                StubCometRepository.Comet("C/2023 A3", "Tsuchinshan-ATLAS")),
            when).Length.ShouldBe(2);

        state.CometCacheRebuilds.ShouldBe(2);
    }
}
