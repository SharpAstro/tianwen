using System.Diagnostics;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Times the Tycho-2 atlas load, phase by phase, on whatever build <c>TIANWEN_WEB_BASEURL</c> points
/// at. This is the number <c>docs/plans/web-tycho2.md</c> has been gating its P2 (parallel decode) and
/// P4 (spatial tiling) phases on since P1 shipped, and which its open questions still record as
/// unknown: the phases are a ~30 MB fetch, an lzip decompress, and a flatten to star instances, all
/// synchronous on the single WASM thread, and each one wants a completely different fix.
///
/// <para><b>It must run against a DEPLOYED build.</b> A dev server is interpreted, where compute is
/// 24-42x slower and the ratios between phases are meaningless, and it 404s the asset anyway
/// (Lightweight strips it, and staging it is a CI step). Fetch time is the one phase a local server
/// would understate rather than overstate, so a local run is wrong in both directions at once.</para>
///
/// <para><b>Cold means a fresh browser context.</b> P3 caches the decompressed catalog in IndexedDB,
/// so the second visit skips both the fetch and the decompress -- measuring the warm path and calling
/// it the load cost would report a solved problem. Each leg here opens its own context; the second
/// reuses the first's so the cache is live.</para>
///
/// <para>Env-gated (<c>TIANWEN_WEB_PROBE=1</c>) like <see cref="OverlayVisibilityProbe"/>: it is an
/// instrument, not an assertion. There is no correct number to bound it against -- the point is to
/// read the split between phases and decide which of them is worth engineering.</para>
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class AtlasLoadCostProbe(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float LoadTimeout = 180_000;
    private const float CacheWriteTimeout = 60_000;

    [Fact]
    public async Task MeasureAsync()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_WEB_PROBE") == "1",
            "probe: set TIANWEN_WEB_PROBE=1 (and point TIANWEN_WEB_BASEURL at a DEPLOYED build)");

        // One context for both legs: the cold leg populates the IndexedDB cache that the warm leg is
        // there to measure, and a second context would start empty and re-measure the cold path.
        var page = await fixture.NewPageAsync();
        var console = new List<string>();
        page.Console += (_, m) => { lock (console) console.Add(m.Text); };

        output.WriteLine($"[atlas] base url: {fixture.BaseUrl}");
        await RunLegAsync(page, console, "COLD (empty IndexedDB)");

        // The cache write is fire-and-forget and moves 41 MB into IndexedDB, so it is still running
        // when the flatten line prints. Navigating on that line measured a SECOND cold load and read
        // as a broken cache -- the phase timings were identical and the source said "(decode)", which
        // is the only thing that gave it away. Wait for the app to say it stored the catalog.
        var saved = await WaitForConsoleAsync(console, "tyc2 cached to IndexedDB", CacheWriteTimeout);
        output.WriteLine($"[atlas] cache write completed: {saved}");

        // Same context, reloaded: the catalog is now cached, so this is the repeat-visit cost.
        await RunLegAsync(page, console, "WARM (IndexedDB primed)");
        await page.Context.CloseAsync();
    }

    private async Task RunLegAsync(IPage page, List<string> console, string leg)
    {
        lock (console) console.Clear();
        var sw = Stopwatch.StartNew();

        // Deep-link straight to the atlas: the fetch fires on the first Sky-Atlas PAINT, so landing on
        // the planner and clicking across would fold the chip navigation into the measurement.
        await page.GotoAsync(fixture.BaseUrl + "?view=sky",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = LoadTimeout });
        var interactive = sw.ElapsedMilliseconds;

        // Wait for the app to SAY it flattened the catalog, not for the loading overlay to be absent.
        // Absent is the state before the load starts as well as after it ends, so the first version of
        // this probe passed in 31 ms having measured the gap before the fetch was even issued -- with
        // no tyc2 console lines to show for it, which is the only reason it was caught.
        var flatten = await WaitForConsoleAsync(console, "tyc2 flatten", LoadTimeout);
        var total = sw.ElapsedMilliseconds;
        Assert.True(flatten, $"{leg}: the catalog never flattened within {LoadTimeout} ms");

        output.WriteLine($"[atlas] --- {leg} ---");
        output.WriteLine($"[atlas] chrome interactive at {interactive} ms, catalog on screen at {total} ms "
            + $"({total - interactive} ms of atlas work after the app was usable)");
        lock (console)
        {
            foreach (var line in console.Where(c => c.Contains("tyc2") || c.Contains("atlas")))
            {
                output.WriteLine($"[atlas]   {line}");
            }
        }
    }

    /// <summary>Polls the captured console for a marker line. The app reports each phase as it
    /// finishes, so its own output is the completion signal -- and unlike a DOM state, a line that has
    /// been printed cannot un-print, so this cannot pass before the work it names has happened.</summary>
    private static async Task<bool> WaitForConsoleAsync(List<string> console, string marker, float timeoutMs)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            lock (console)
            {
                if (console.Any(c => c.Contains(marker, StringComparison.Ordinal))) return true;
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return false;
    }
}
