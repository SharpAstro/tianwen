using TianWen.Lib.Astrometry.Catalogs;
using Shouldly;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace TianWen.Lib.Tests;

public class CelestialObjectDBBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GivenNewDBWhenInitializingThenItCompletesInUnder10Seconds()
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
        sw.Elapsed.TotalSeconds.ShouldBeLessThan(10, $"DB initialization took {sw.Elapsed.TotalSeconds:F1}s, expected < 10s");
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
    public async Task GivenInitializedDBWhenLookingUpCrossIndicesThenItCompletesInUnder1Millisecond()
    {
        // given
        var db = new CelestialObjectDB();
        await db.InitDBAsync(TestContext.Current.CancellationToken);

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

        // when
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

        // then
        var avgNs = sw.Elapsed.TotalNanoseconds / (iterations * indices.Length);
        output.WriteLine($"TryGetCrossIndices: {avgNs:F1}ns avg per lookup ({iterations * indices.Length} lookups in {sw.Elapsed.TotalMilliseconds:F1}ms)");
        avgNs.ShouldBeLessThan(10_000, $"Cross-index lookup took {avgNs:F1}ns avg, expected < 1000ns");
    }
}
