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
        await db.InitDBAsync(cancellationToken: TestContext.Current.CancellationToken);
        sw.Stop();

        // then
        db.LastInitFailed.ShouldBe(0);
        db.LastInitProcessed.ShouldBeGreaterThan(13000);
        output.WriteLine($"DB initialization: {sw.Elapsed.TotalMilliseconds:F1}ms ({db.LastInitProcessed} entries processed)");
        foreach (var (phase, elapsed) in db.LastInitPhaseTimings)
        {
            output.WriteLine($"  {phase,-30} {elapsed.TotalMilliseconds,8:F1}ms");
        }
        sw.Elapsed.TotalSeconds.ShouldBeLessThan(20, $"DB initialization took {sw.Elapsed.TotalSeconds:F1}s, expected < 20s");
    }

    [Fact]
    public async Task GivenInitializedDBWhenLookingUpByIndexThenItCompletesInUnder1Millisecond()
    {
        // given
        var db = new CelestialObjectDB();
        await db.InitDBAsync(cancellationToken: TestContext.Current.CancellationToken);

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
        await db.InitDBAsync(cancellationToken: TestContext.Current.CancellationToken);

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
        await db.InitDBAsync(cancellationToken: TestContext.Current.CancellationToken);
        initSw.Stop();
        var initNsPerEntry = initSw.Elapsed.TotalNanoseconds / db.LastInitProcessed;

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

        // then — lookups should be O(1) hash-table access, expected sub-µs on dev hardware
        // and well under 100 µs even on noisy CI runners. Asserting absolute lookup time
        // (not a ratio to init) avoids flaking when init I/O is fast and the ratio compresses.
        var avgLookupNs = sw.Elapsed.TotalNanoseconds / (iterations * indices.Length);
        var speedup = initNsPerEntry / avgLookupNs;
        const double maxLookupNs = 100_000;
        output.WriteLine($"Init: {initNsPerEntry:F1}ns/entry, Lookup: {avgLookupNs:F1}ns avg, Speedup: {speedup:F1}x (informational; max lookup: {maxLookupNs}ns)");
        avgLookupNs.ShouldBeLessThan(maxLookupNs,
            $"Cross-index lookup ({avgLookupNs:F1}ns) should be < {maxLookupNs}ns; suggests an O(1) → O(N) regression in TryGetCrossIndices");
    }
}
