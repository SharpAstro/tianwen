using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Cache-orchestration tests for <see cref="CometRepository"/>: first-load fetch + write, fresh-cache
/// reuse (no refetch), TTL-expiry refetch, and offline stale fallback -- all driven by a fake SBDB
/// source and the <see cref="FakeExternal"/> temp-dir + fake clock (no network, no wall-clock).
/// </summary>
public class CometRepositoryTests(ITestOutputHelper output)
{
    private sealed class FakeSbdbCometSource(IReadOnlyList<CometElements> elements, bool @throw = false) : ISbdbCometSource
    {
        public int FetchCount { get; private set; }

        public Task<IReadOnlyList<CometElements>> FetchAsync(CancellationToken cancellationToken)
        {
            FetchCount++;
            return @throw ? throw new HttpRequestException("offline") : Task.FromResult(elements);
        }
    }

    private static IReadOnlyList<CometElements> SampleComets()
    {
        Parse("12P", out var d12P);
        Parse("C/2023 A3", out var dA3);
        return
        [
            new CometElements(d12P, "Pons-Brooks", 0.7808611, 0.9545612, 74.19, 255.85, 198.98, 2460421.63, 2460211.5, 5.0, 15.0),
            new CometElements(dA3, "Tsuchinshan-ATLAS", 0.39143, 1.0000953, 139.11, 21.55, 308.49, 2460581.24, 2460448.5, 8.9, 5.5),
        ];
    }

    private static void Parse(string s, out CometDesignation d)
        => CometDesignation.TryParse(s, out d).ShouldBeTrue();

    // A fresh, isolated cache root per test -- the shared CreateTempTestOutputDir helper is stable per
    // caller within a day and is never cleaned, so a prior run's comets.json would otherwise bleed in
    // and read as "fresh" against the fixed default fake epoch.
    private FakeExternal CreateExternal(FakeTimeProviderWrapper timeProvider)
    {
        var root = new DirectoryInfo(Directory.CreateTempSubdirectory("comet-repo-test-").FullName);
        return new FakeExternal(output, timeProvider, root);
    }

    private FakeExternal CreateExternal()
        => CreateExternal(new FakeTimeProviderWrapper(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero)));

    private CometRepository NewRepository(FakeExternal external, ISbdbCometSource source, IHorizonsCometSource? horizons = null, ApparitionCacheFile? seed = null)
        => new(source, horizons ?? new NeverFetchesHorizons(), new FakeApparitionSeed(seed), external, external.TimeProvider, NullLogger<CometRepository>.Instance);

    /// <summary>The publish-time overlay a browser host is given. Null = the desktop shape (no seed).</summary>
    private sealed class FakeApparitionSeed(ApparitionCacheFile? seed) : IApparitionSeedSource
    {
        public ValueTask<ApparitionCacheFile?> TryFetchAsync(CancellationToken cancellationToken) => ValueTask.FromResult(seed);
    }

    /// <summary>The default for the bulk-cache tests: they are about SBDB and must never depend on the
    /// per-object overlay, so this one answers "no refinement available" without touching a network.</summary>
    private sealed class NeverFetchesHorizons : IHorizonsCometSource
    {
        public Task<CometElements?> TryFetchCurrentApparitionAsync(CometElements baseElements, DateTimeOffset at, CancellationToken cancellationToken)
            => Task.FromResult<CometElements?>(null);
    }

    [Fact]
    public async Task GivenNoCacheWhenEnsureLoadedThenItFetchesAndWritesCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = CreateExternal();
        var source = new FakeSbdbCometSource(SampleComets());
        var repo = NewRepository(external, source);

        await repo.EnsureLoadedAsync(ct);

        source.FetchCount.ShouldBe(1);
        repo.All.Length.ShouldBe(2);

        // Second EnsureLoadedAsync on the same instance is a no-op (already loaded).
        await repo.EnsureLoadedAsync(ct);
        source.FetchCount.ShouldBe(1);
    }

    [Fact]
    public async Task GivenFreshCacheWhenAnotherRepositoryLoadsThenItDoesNotRefetch()
    {
        var ct = TestContext.Current.CancellationToken;
        var tp = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        var external = CreateExternal(tp);

        var first = new FakeSbdbCometSource(SampleComets());
        await NewRepository(external, first).EnsureLoadedAsync(ct);
        first.FetchCount.ShouldBe(1);

        // A brand-new repository sharing the same cache dir + clock reads the fresh cache instead of fetching.
        var second = new FakeSbdbCometSource(SampleComets());
        var repo2 = NewRepository(external, second);
        await repo2.EnsureLoadedAsync(ct);

        second.FetchCount.ShouldBe(0);
        repo2.All.Length.ShouldBe(2);
    }

    [Fact]
    public async Task GivenCacheOlderThanTtlWhenLoadingThenItRefetches()
    {
        var ct = TestContext.Current.CancellationToken;
        var tp = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        var external = CreateExternal(tp);

        await NewRepository(external, new FakeSbdbCometSource(SampleComets())).EnsureLoadedAsync(ct);

        tp.Advance(TimeSpan.FromDays(8)); // past the 7-day TTL

        var refetch = new FakeSbdbCometSource(SampleComets());
        await NewRepository(external, refetch).EnsureLoadedAsync(ct);
        refetch.FetchCount.ShouldBe(1);
    }

    [Fact]
    public async Task GivenStaleCacheAndOfflineSourceWhenLoadingThenItFallsBackToStaleCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var tp = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        var external = CreateExternal(tp);

        await NewRepository(external, new FakeSbdbCometSource(SampleComets())).EnsureLoadedAsync(ct);
        tp.Advance(TimeSpan.FromDays(8));

        var offline = new FakeSbdbCometSource([], @throw: true);
        var repo = NewRepository(external, offline);
        await Should.NotThrowAsync(async () => await repo.EnsureLoadedAsync(ct));

        offline.FetchCount.ShouldBe(1);   // it tried
        repo.All.Length.ShouldBe(2);      // and served the stale cache
    }

    [Fact]
    public async Task GivenNoCacheAndOfflineSourceWhenLoadingThenItIsEmptyAndDoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = CreateExternal();
        var repo = NewRepository(external, new FakeSbdbCometSource([], @throw: true));

        await Should.NotThrowAsync(async () => await repo.EnsureLoadedAsync(ct));
        repo.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task GivenLoadedCometsWhenTryGetPositionThenResolvesKnownAndRejectsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = CreateExternal();
        var repo = NewRepository(external, new FakeSbdbCometSource(SampleComets()));
        await repo.EnsureLoadedAsync(ct);

        Parse("12P", out var d12P);
        d12P.TryToCatalogIndex(out var idx).ShouldBeTrue();

        var time = new DateTimeOffset(2023, 9, 24, 0, 0, 0, TimeSpan.Zero);
        repo.TryGetPosition(idx, time, out var ra, out var dec, out var mag).ShouldBeTrue();
        ra.ShouldBeInRange(0.0, 24.0);
        dec.ShouldBeInRange(-90.0, 90.0);
        double.IsNaN(mag).ShouldBeFalse();

        Parse("99P", out var unknown);
        unknown.TryToCatalogIndex(out var unknownIdx).ShouldBeTrue();
        repo.TryGetPosition(unknownIdx, time, out _, out _, out _).ShouldBeFalse();
    }

    // ---- Current-apparition overlay (the 9.3-degree fix) --------------------------------------

    /// <summary>Hands back a canned refined set and counts calls, so single-flight and the
    /// only-when-stale gate are observable without a network.</summary>
    private sealed class FakeHorizons(CometElements? refined) : IHorizonsCometSource
    {
        public int FetchCount;

        public Task<CometElements?> TryFetchCurrentApparitionAsync(CometElements baseElements, DateTimeOffset at, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FetchCount);
            return Task.FromResult(refined);
        }
    }

    private sealed class ThrowingHorizons : IHorizonsCometSource
    {
        public int FetchCount;

        public Task<CometElements?> TryFetchCurrentApparitionAsync(CometElements baseElements, DateTimeOffset at, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FetchCount);
            throw new HttpRequestException("offline");
        }
    }

    // A stale periodic comet: epoch two revolutions back, which is the shape that puts a marker degrees
    // off and the only shape worth a network round-trip.
    private static CometElements StaleComet()
    {
        Parse("10P", out var d);
        return new CometElements(d, "Tempel", 1.4174, 0.53738, 12.03, 117.8, 195.53,
            PerihelionJdTt: 2457340.741, EpochJdTt: 2457650.5, AbsoluteMagnitudeM1: 13.7, SlopeK1: 6.5);
    }

    private static CometElements RefreshedComet()
        => StaleComet() with { PerihelionJdTt = 2461254.615, EpochJdTt = 2461258.5 };

    private static async Task<CometRepository> LoadedWithStaleCometAsync(FakeExternal external, IHorizonsCometSource horizons, ApparitionCacheFile? seed, CancellationToken ct)
    {
        var repo = new CometRepository(new FakeSbdbCometSource([StaleComet()]), horizons, new FakeApparitionSeed(seed), external, external.TimeProvider, NullLogger<CometRepository>.Instance);
        await repo.EnsureLoadedAsync(ct);
        return repo;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        // The upgrade is fire-and-forget by design (it is called from render paths that cannot await),
        // so the test polls for the published swap rather than awaiting a Task it is not given.
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        return condition();
    }

    [Fact]
    public async Task GivenAStaleCometWhenRequestedThenItsElementsAreUpgraded()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = CreateExternal(new FakeTimeProviderWrapper(new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero)));
        var horizons = new FakeHorizons(RefreshedComet());
        var repo = await LoadedWithStaleCometAsync(external, horizons, seed: null, ct);
        var index = StaleComet().CatalogIndex.ShouldNotBeNull();

        // Before: the bulk record, whose perihelion is the previous apparition.
        repo.TryGet(index, out var before).ShouldBeTrue();
        before.PerihelionJdTt.ShouldBe(2457340.741, tolerance: 0.001);

        repo.RequestCurrentApparition(index);

        (await WaitForAsync(() => repo.TryGet(index, out var e) && e.EpochJdTt > 2461000)).ShouldBeTrue();
        repo.TryGet(index, out var after).ShouldBeTrue();
        after.PerihelionJdTt.ShouldBe(2461254.615, tolerance: 0.001);
        horizons.FetchCount.ShouldBe(1);
    }

    [Fact]
    public async Task ASecondRequestForAnAlreadyUpgradedCometFetchesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = CreateExternal(new FakeTimeProviderWrapper(new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero)));
        var horizons = new FakeHorizons(RefreshedComet());
        var repo = await LoadedWithStaleCometAsync(external, horizons, seed: null, ct);
        var index = StaleComet().CatalogIndex.ShouldNotBeNull();

        repo.RequestCurrentApparition(index);
        (await WaitForAsync(() => horizons.FetchCount == 1)).ShouldBeTrue();

        // This is called per drawn marker per frame, so "already upgraded" has to be free.
        for (var i = 0; i < 50; i++)
        {
            repo.RequestCurrentApparition(index);
        }

        horizons.FetchCount.ShouldBe(1);
    }

    [Fact]
    public async Task AFreshCometIsNeverFetched()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = CreateExternal(new FakeTimeProviderWrapper(new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero)));
        var horizons = new FakeHorizons(RefreshedComet());
        // Epoch inside the current apparition: nothing to gain, so no round-trip.
        var fresh = StaleComet() with { EpochJdTt = 2461200.0, PerihelionJdTt = 2461254.615 };
        var repo = new CometRepository(new FakeSbdbCometSource([fresh]), horizons, new FakeApparitionSeed(null), external, external.TimeProvider, NullLogger<CometRepository>.Instance);
        await repo.EnsureLoadedAsync(ct);

        repo.RequestCurrentApparition(fresh.CatalogIndex.ShouldNotBeNull());
        await Task.Delay(100, ct);

        horizons.FetchCount.ShouldBe(0);
    }

    [Fact]
    public async Task WhenHorizonsIsUnreachableTheBulkElementsStayInUse()
    {
        // Offline must degrade to "old but self-consistent", never to a missing comet -- and the flag
        // must keep saying the position is approximate so the UI keeps showing its marker.
        var ct = TestContext.Current.CancellationToken;
        var external = CreateExternal(new FakeTimeProviderWrapper(new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero)));
        var repo = await LoadedWithStaleCometAsync(external, new ThrowingHorizons(), seed: null, ct);
        var index = StaleComet().CatalogIndex.ShouldNotBeNull();

        repo.RequestCurrentApparition(index);
        await Task.Delay(100, ct);

        repo.TryGet(index, out var elements).ShouldBeTrue();
        elements.PerihelionJdTt.ShouldBe(2457340.741, tolerance: 0.001);
        elements.IsElementSetStale(2461258.5).ShouldBeTrue();
    }

    /// <summary>
    /// A publish-time seed with an EXPIRED entry, so the TTL check cannot be what stops the fetch --
    /// only the seal can. This is the browser's configuration: Horizons sends no CORS headers, so a
    /// request from that host can never succeed and must not be made at all.
    /// </summary>
    [Fact]
    public async Task ASealedSeedMeansHorizonsIsNeverAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        var external = CreateExternal(new FakeTimeProviderWrapper(now));
        var horizons = new FakeHorizons(RefreshedComet());
        var seed = new ApparitionCacheFile([new ApparitionEntry(now - TimeSpan.FromDays(30), RefreshedComet())]) { NoRemoteRefresh = true };
        var repo = await LoadedWithStaleCometAsync(external, horizons, seed, ct);
        var index = StaleComet().CatalogIndex.ShouldNotBeNull();

        // Called per drawn marker per frame, which is exactly how the unsealed build reached 45
        // requests for one comet in a single session.
        for (var i = 0; i < 50; i++)
        {
            repo.RequestCurrentApparition(index);
        }
        await Task.Delay(100, ct);

        horizons.FetchCount.ShouldBe(0);

        // ...and the seeded elements are in use, so sealing costs the atlas nothing.
        repo.TryGet(index, out var elements).ShouldBeTrue();
        elements.PerihelionJdTt.ShouldBe(2461254.615, tolerance: 0.001);
    }

    /// <summary>
    /// The counterpart that keeps the test above honest: the SAME expired seed without the flag still
    /// upgrades live. Without this, sealing could be doing nothing and the assertion above would pass
    /// because some other gate happened to stop the fetch.
    /// </summary>
    [Fact]
    public async Task AnUnsealedSeedStillUpgradesFromHorizons()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        var external = CreateExternal(new FakeTimeProviderWrapper(now));
        var horizons = new FakeHorizons(RefreshedComet());
        var seed = new ApparitionCacheFile([new ApparitionEntry(now - TimeSpan.FromDays(30), RefreshedComet())]);
        var repo = await LoadedWithStaleCometAsync(external, horizons, seed, ct);

        repo.RequestCurrentApparition(StaleComet().CatalogIndex.ShouldNotBeNull());

        (await WaitForAsync(() => horizons.FetchCount == 1)).ShouldBeTrue();
    }

    /// <summary>
    /// A failed fetch has to cost ONE request per cooldown window, not one per frame that draws the
    /// marker. The single-flight key clears when the fetch settles and only success writes an entry, so
    /// before the cooldown existed nothing remembered the failure: the browser build issued 45 requests
    /// and spent 50 s of request time on one comet in four minutes. An offline desktop, a dev server
    /// without the baked seed, and a JPL outage all take this same path.
    /// </summary>
    [Fact]
    public async Task AFailedFetchIsNotRetriedUntilItsCooldownExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var tp = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero));
        var external = CreateExternal(tp);
        var horizons = new ThrowingHorizons();
        var repo = await LoadedWithStaleCometAsync(external, horizons, seed: null, ct);
        var index = StaleComet().CatalogIndex.ShouldNotBeNull();

        repo.RequestCurrentApparition(index);
        (await WaitForAsync(() => Volatile.Read(ref horizons.FetchCount) == 1)).ShouldBeTrue();
        await Task.Delay(100, ct); // let the failure stamp land in the fetch task's finally

        // Well inside the cooldown: a frame-rate storm of requests must buy exactly nothing.
        tp.Advance(TimeSpan.FromMinutes(30));
        for (var i = 0; i < 50; i++)
        {
            repo.RequestCurrentApparition(index);
        }
        await Task.Delay(100, ct);
        Volatile.Read(ref horizons.FetchCount).ShouldBe(1);

        // Past it, one more attempt is made -- a transient outage must still recover on its own.
        tp.Advance(TimeSpan.FromHours(2));
        repo.RequestCurrentApparition(index);

        (await WaitForAsync(() => Volatile.Read(ref horizons.FetchCount) == 2)).ShouldBeTrue();
    }

    /// <summary>
    /// The bulk <see cref="CometEphemeris.EarthState"/> overload has to resolve through the SAME
    /// apparition-aware lookup as the per-instant one. It exists so a catalogue-wide sweep evaluates
    /// Earth once instead of ~1,800 times, and the tempting way to get that -- iterate
    /// <see cref="ICometRepository.All"/> and call <see cref="CometEphemeris"/> directly at the call
    /// site -- would quietly sweep the STALE element set, because the upgrade is applied inside
    /// <see cref="ICometRepository.TryGet"/>. For 10P in 2026 that is 9.3 degrees of sky, and nothing
    /// would look broken: every marker would simply be in the wrong place.
    ///
    /// <para>So this pins both halves. The overloads must agree exactly (same lookup, same
    /// arithmetic), AND the answer must have moved once the upgrade landed -- without the second
    /// assertion the first would still pass for a bulk overload that had bypassed the upgrade
    /// entirely, since then both would be consistently stale.</para>
    /// </summary>
    [Fact]
    public async Task TheBulkOverloadResolvesThroughTheCurrentApparitionToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var when = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        var external = CreateExternal(new FakeTimeProviderWrapper(when));
        var repo = await LoadedWithStaleCometAsync(external, new FakeHorizons(RefreshedComet()), seed: null, ct);
        var index = StaleComet().CatalogIndex.ShouldNotBeNull();

        CometEphemeris.TryGetEarthState(when, out var earth).ShouldBeTrue();
        repo.TryGetPosition(index, earth, out var staleRa, out var staleDec, out _).ShouldBeTrue();

        repo.RequestCurrentApparition(index);
        (await WaitForAsync(() => repo.TryGet(index, out var e) && e.EpochJdTt > 2461000)).ShouldBeTrue();

        repo.TryGetPosition(index, earth, out var bulkRa, out var bulkDec, out var bulkMag).ShouldBeTrue();
        repo.TryGetPosition(index, when, out var perInstantRa, out var perInstantDec, out var perInstantMag).ShouldBeTrue();

        bulkRa.ShouldBe(perInstantRa);
        bulkDec.ShouldBe(perInstantDec);
        bulkMag.ShouldBe(perInstantMag);

        // MEASURED at 9.3 degrees (see CometEphemerisTests, which decomposes it as a pure timing error
        // from the two-body period); bounded well below that so a restated JPL solution cannot fail it.
        SeparationDeg(staleRa, staleDec, bulkRa, bulkDec).ShouldBeGreaterThan(5.0);
    }

    private static double SeparationDeg(double ra1Hours, double dec1Deg, double ra2Hours, double dec2Deg)
    {
        const double d2r = Math.PI / 180.0;
        var d1 = dec1Deg * d2r;
        var d2 = dec2Deg * d2r;
        var dRa = (ra1Hours - ra2Hours) * 15.0 * d2r;
        var cosSep = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(dRa);
        return Math.Acos(Math.Clamp(cosSep, -1.0, 1.0)) / d2r;
    }
}
