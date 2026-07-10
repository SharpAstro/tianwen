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

    private CometRepository NewRepository(FakeExternal external, ISbdbCometSource source)
        => new(source, external, external.TimeProvider, NullLogger<CometRepository>.Instance);

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
}
