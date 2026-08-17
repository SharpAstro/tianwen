using System.Diagnostics;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Two things the web host did not do, both invisible from the screen.
///
/// <list type="number">
/// <item><b>A date change never recomputed.</b> PageUp / PageDown / N / T set
/// <c>PlannerState.NeedsRecompute</c>, and the browser host was the only one of the three that never
/// read it (the desktop has <c>AppSignalHandler.CheckRecompute</c>). Worse,
/// <c>CreateSiteTransform</c> stamped <c>DateTime = now</c> unconditionally, so even the Recompute
/// button planned for tonight. The sky map moved to the new night and every score, night window and
/// altitude profile stayed on the old one -- and both look entirely plausible on screen, which is why
/// this needs a log line rather than a screenshot.</item>
/// <item><b>Nothing cached the sweep.</b> A reload re-ran the full catalog scan. The candidate set is
/// now persisted, so the second visit rescores it instead: measured on the deployed build, ~1520 ms
/// of sweep replaced by ~170 ms of rescore.</item>
/// </list>
///
/// <para>Both are asserted from the app's own console output, for the reason the whole perf
/// investigation kept relearning: the rendered result is IDENTICAL either way. A planner scored for
/// the wrong night draws a perfectly good chart, and a cache hit draws the same list as a sweep.</para>
///
/// <para>Runs on its own page rather than the shared warm one: it reloads, which would hand every
/// later test a fresh boot instead of the warm state the fixture promises.</para>
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class PlannerDateAndCacheTests(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 180_000;
    private const float RecomputeTimeout = 30_000;

    [Fact]
    public async Task ADateChangeRecomputesAndAReloadSkipsTheCatalogSweep()
    {
        var page = await fixture.NewPageAsync();
        var console = new List<string>();
        page.Console += (_, m) => { lock (console) console.Add(m.Text); };

        // --- First visit: nothing cached, so the full sweep runs and seeds the cache ---
        await GotoReadyAsync(page, fixture.BaseUrl + "?e2e=1");
        Assert.True(Saw(console, "tonight's best (full sweep)"),
            "the first visit should have swept the catalog; " + Dump(console));

        // Printed so the before and after are read off ONE run of ONE build. The rescore's cost was
        // first taken from a warm session (167 ms via the Recompute button, after the astrom grid and
        // the profile code had already run once) and that is not what a reload pays -- comparing it to
        // a cold sweep would have overstated the win several times over.
        foreach (var line in Lines(console))
        {
            output.WriteLine($"[planner] first visit: {line}");
        }

        // --- A date change must reach the planner, not just the sky map ---
        lock (console) console.Clear();
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"),
            new() { Timeout = BootTimeout });

        var sw = Stopwatch.StartNew();
        await page.Locator("#planner").PressAsync("PageUp");
        var rescored = await WaitForConsoleAsync(console, RecomputeTimeout, "tonight's best (rescore");
        output.WriteLine($"[planner] PageUp -> rescore in {sw.ElapsedMilliseconds} ms");
        Assert.True(rescored,
            "PageUp moved the planning date but nothing recomputed; " + Dump(console));

        // --- Second visit: the candidate cache replaces the scan ---
        lock (console) console.Clear();
        await GotoReadyAsync(page, fixture.BaseUrl + "?e2e=1");

        Assert.True(Saw(console, "candidates restored"),
            "the reload did not restore the cached candidate set; " + Dump(console));
        Assert.True(Saw(console, "rescore, cached candidates"),
            "the cached candidates were restored but not rescored for tonight; " + Dump(console));
        Assert.False(Saw(console, "tonight's best (full sweep)"),
            "the reload swept the catalog anyway, so the cache bought nothing; " + Dump(console));

        foreach (var line in Lines(console))
        {
            output.WriteLine($"[planner] reload: {line}");
        }

        await page.Context.CloseAsync();
    }

    private static async Task GotoReadyAsync(IPage page, string url)
    {
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=planner]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
    }

    private static bool Saw(List<string> console, string marker)
    {
        lock (console) return console.Any(c => c.Contains(marker, StringComparison.Ordinal));
    }

    private static string[] Lines(List<string> console)
    {
        lock (console) return [.. console.Where(c => c.Contains("[tianwen-web]", StringComparison.Ordinal))];
    }

    private static string Dump(List<string> console)
        => "app said: " + string.Join(" | ", Lines(console));

    private static async Task<bool> WaitForConsoleAsync(List<string> console, float timeoutMs, string marker)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            if (Saw(console, marker)) return true;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return false;
    }
}
