using System.Diagnostics;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Times the planner's own two costs -- the embedded DSO catalog init and the tonight's-best
/// computation -- which <see cref="AtlasLoadCostProbe"/> reports on a boot but never re-runs.
///
/// <para><b>Why this is a separate instrument.</b> The atlas probe answers "what does a repeat visit
/// cost", and the answer is dominated by the star tiles, which IndexedDB caches. The planner's costs
/// are cached by NOTHING: the catalog is embedded in the assembly (no fetch to cache, the cost is
/// decompressing and parsing it) and the sweep is pure computation. Measured on the deployed build
/// they were 1306 / 1507 ms cold and 1330 / 1482 ms warm -- i.e. unchanged, which is the finding.</para>
///
/// <para>The Recompute button is also the only way to reach <c>RecomputeForDate</c> from a browser, and
/// its cost is what decides whether a date change can be wired to a keypress at all: the whole thing is
/// synchronous on the one WASM thread, so whatever it measures is a freeze the user sees.</para>
///
/// <para>Env-gated (<c>TIANWEN_WEB_PROBE=1</c>) like the others: an instrument, not an assertion.</para>
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class PlannerRecomputeCostProbe(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float LoadTimeout = 180_000;
    private const float RecomputeTimeout = 60_000;

    [Fact]
    public async Task MeasureAsync()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_WEB_PROBE") == "1",
            "probe: set TIANWEN_WEB_PROBE=1 (and point TIANWEN_WEB_BASEURL at a DEPLOYED build)");

        var page = await fixture.NewPageAsync();
        var console = new List<string>();
        page.Console += (_, m) => { lock (console) console.Add(m.Text); };

        output.WriteLine($"[planner] base url: {fixture.BaseUrl}");
        await page.GotoAsync(fixture.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=planner]")).ToBeVisibleAsync(new() { Timeout = LoadTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = LoadTimeout });
        Dump(console, "BOOT");

        // The rescore path, which is what a date change would run. Same site, so ComputeAsync takes
        // the RecomputeForDate branch rather than re-sweeping the catalog.
        for (var round = 1; round <= 3; round++)
        {
            lock (console) console.Clear();
            var sw = Stopwatch.StartNew();
            await page.GetByRole(AriaRole.Button, new() { Name = "Recompute" }).ClickAsync();
            var seen = await WaitForConsoleAsync(console, RecomputeTimeout, "tonight's best (");
            output.WriteLine($"[planner] --- Recompute #{round}: button to log line {sw.ElapsedMilliseconds} ms "
                + $"(logged={seen}) ---");
            Dump(console, $"recompute #{round}");
        }

        await page.Context.CloseAsync();
    }

    private void Dump(List<string> console, string leg)
    {
        lock (console)
        {
            foreach (var line in console.Where(c => c.Contains("[tianwen-web]", StringComparison.Ordinal)))
            {
                output.WriteLine($"[planner] {leg}: {line}");
            }
        }
    }

    private static async Task<bool> WaitForConsoleAsync(List<string> console, float timeoutMs, params string[] markers)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            lock (console)
            {
                if (console.Any(c => markers.Any(m => c.Contains(m, StringComparison.Ordinal)))) return true;
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return false;
    }
}
