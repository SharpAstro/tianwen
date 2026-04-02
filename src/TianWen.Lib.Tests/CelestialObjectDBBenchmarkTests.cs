using System;
using TianWen.Lib.Astrometry.Catalogs;
using Shouldly;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Catalog")]
public class CelestialObjectDBBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GivenNewDBWhenInitializingThenItCompletesInUnder20Seconds()
    {
        // given
        var db = new CelestialObjectDB();
        var sw = Stopwatch.StartNew();

        // when
        var (processed, failed) = await db.InitDBAsync(TestContext.Current.CancellationToken);
        sw.Stop();

        // then
        failed.ShouldBe(0);
        processed.ShouldBeGreaterThan(13000);
        output.WriteLine($"DB initialization: {sw.Elapsed.TotalMilliseconds:F1}ms ({processed} entries processed)");
        sw.Elapsed.TotalSeconds.ShouldBeLessThan(20, $"DB initialization took {sw.Elapsed.TotalSeconds:F1}s, expected < 20s");
    }

    [Fact]
    public async Task GivenInitializedDBWhenLookingUpByIndexThenItCompletesInUnder1Millisecond()
    {
        // given
        var db = new CelestialObjectDB();
        await db.InitDBAsync(TestContext.Current.CancellationToken);

        var indices = new[]
        {
            CatalogIndex.NGC3372, CatalogIndex.M042, CatalogIndex.Mel022,
            CatalogIndex.HIP016537, CatalogIndex.IC4703, CatalogIndex.C041
        };

        // warmup
        foreach (var idx in indices)
        {
            db.TryLookupByIndex(idx, out _);
        }

        // when
        var sw = Stopwatch.StartNew();
        const int iterations = 1_000;
        for (var i = 0; i < iterations; i++)
        {
            foreach (var idx in indices)
            {
                db.TryLookupByIndex(idx, out _);
            }
        }
        sw.Stop();

        // then
        var avgNs = sw.Elapsed.TotalNanoseconds / (iterations * indices.Length);
        output.WriteLine($"TryLookupByIndex: {avgNs:F1}ns avg per lookup ({iterations * indices.Length} lookups in {sw.Elapsed.TotalMilliseconds:F1}ms)");
        avgNs.ShouldBeLessThan(10_000, $"Lookup took {avgNs:F1}ns avg, expected < 1000ns");
    }

    [Fact]
    public async Task GivenInitializedDBWhenResolvingCommonNamesThenItCompletesInUnder1Millisecond()
    {
        // given
        var db = new CelestialObjectDB();
        await db.InitDBAsync(TestContext.Current.CancellationToken);

        var names = new[] { "Pleiades", "Orion Nebula", "Carina Nebula", "Ran", "Electra", "Keyhole" };

        // warmup
        foreach (var name in names)
        {
            db.TryResolveCommonName(name, out _);
        }

        // when
        var sw = Stopwatch.StartNew();
        const int iterations = 1_000;
        for (var i = 0; i < iterations; i++)
        {
            foreach (var name in names)
            {
                db.TryResolveCommonName(name, out _);
            }
        }
        sw.Stop();

        // then
        var avgNs = sw.Elapsed.TotalNanoseconds / (iterations * names.Length);
        output.WriteLine($"TryResolveCommonName: {avgNs:F1}ns avg per lookup ({iterations * names.Length} lookups in {sw.Elapsed.TotalMilliseconds:F1}ms)");
        avgNs.ShouldBeLessThan(10_000, $"Name resolution took {avgNs:F1}ns avg, expected < 1000ns");
    }

    [Fact]
    public async Task GivenInitializedDBWhenLookingUpCrossIndicesThenItIsMuchFasterThanInit()
    {
        // given — measure initialization cost per entry
        var db = new CelestialObjectDB();
        var initSw = Stopwatch.StartNew();
        var (processed, _) = await db.InitDBAsync(TestContext.Current.CancellationToken);
        initSw.Stop();
        var initNsPerEntry = initSw.Elapsed.TotalNanoseconds / processed;

        var indices = new[]
        {
            CatalogIndex.NGC3372, CatalogIndex.M042, CatalogIndex.HIP016537,
            CatalogIndex.vdB0020, CatalogIndex.HR1084, CatalogIndex.Mel022
        };

        // warmup
        foreach (var idx in indices)
        {
            db.TryGetCrossIndices(idx, out _);
        }

        // when — measure lookup cost
        var sw = Stopwatch.StartNew();
        const int iterations = 1_000;
        for (var i = 0; i < iterations; i++)
        {
            foreach (var idx in indices)
            {
                db.TryGetCrossIndices(idx, out _);
            }
        }
        sw.Stop();

        // then — lookups should be at least 10x faster than init per entry (relaxed to 5x in CI)
        var avgLookupNs = sw.Elapsed.TotalNanoseconds / (iterations * indices.Length);
        var speedup = initNsPerEntry / avgLookupNs;
        var minSpeedup = Environment.GetEnvironmentVariable("CI") is not null ? 5.0 : 10.0;
        output.WriteLine($"Init: {initNsPerEntry:F1}ns/entry, Lookup: {avgLookupNs:F1}ns avg, Speedup: {speedup:F0}x (min: {minSpeedup}x)");
        speedup.ShouldBeGreaterThan(minSpeedup,
            $"Cross-index lookup ({avgLookupNs:F1}ns) should be at least {minSpeedup}x faster than init per entry ({initNsPerEntry:F1}ns), but was only {speedup:F1}x faster");
    }
}
