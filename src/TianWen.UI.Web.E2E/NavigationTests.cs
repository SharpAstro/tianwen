using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Browser E2E for the Planner/Sky-Atlas view switching. The planner list and sky map are drawn
/// into a &lt;canvas&gt;, so these tests never read pixels — they assert on the observable chrome
/// DOM the unit tests can't reach: the titlebar view chips (<c>[data-view]</c> + the
/// <c>active</c> class), the address bar (there is no Blazor Router — chips navigate via
/// NavigationManager and the component parses the path itself), the document title
/// (<c>&lt;PageTitle&gt;</c>), the aria-live status line, and the catalog-loading indicator.
///
/// This suite exists because the exact same bug shipped three times in one session: chip clicks
/// changing the URL but not the active view (route params are inert with no Router), and the chip
/// being blocked while the shared catalog loaded. Each of the tests below fails on that regression.
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class NavigationTests(TianWenWebFixture fixture)
{
    // Interpreted-WASM cold boot + the catalog init (~23 s) + tonight's-best sweep (~26 s) on the
    // dev server dwarf any DOM settle time. The deployed AOT build does all of this in ~1 s, but
    // the tests run against the interpreted dev server, so the ceiling is generous.
    private const float BootTimeout = 120_000;

    private static readonly Regex ActiveClass = new(@"\bactive\b");

    private ILocator PlannerChip(IPage page) => page.Locator("[data-view=planner]");
    private ILocator SkyChip(IPage page) => page.Locator("[data-view=sky]");
    private ILocator Status(IPage page) => page.Locator(".status");
    private ILocator CatalogLoading(IPage page) => page.Locator(".catalog-loading");

    private async Task<IPage> OpenAsync(string path)
    {
        var page = await fixture.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + path, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });
        return page;
    }

    // The catalog feeds both views; the app is "ready" once the loading indicator is gone (the
    // @if (!_ready) block is removed from the DOM). Waiting for this makes view switches
    // deterministic — during the load the single WASM thread is frozen and clicks queue.
    private async Task WaitReadyAsync(IPage page)
    {
        await Expect(PlannerChip(page)).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(CatalogLoading(page)).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
    }

    // ── boot state ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FreshVisit_PlannerIsTheActiveView()
    {
        var page = await OpenAsync("");

        // Planner is the default active view; the sky chip is present but not active.
        // NB: we do NOT assert the .catalog-loading indicator is *visible* here — during the
        // interpreted-WASM catalog load the browser main thread (where WASM runs) is frozen, so
        // Playwright can't observe the transient indicator; it only ever sees the settled DOM once
        // the thread frees. The indicator's *disappearance* after load is the stable assertion,
        // covered by CatalogLoads_LoadingIndicatorClears_AndStatusReportsTargets.
        await Expect(PlannerChip(page)).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(PlannerChip(page)).ToHaveClassAsync(ActiveClass);
        await Expect(SkyChip(page)).Not.ToHaveClassAsync(ActiveClass);
        await Expect(page).ToHaveTitleAsync("Astro - Planner");
    }

    [Fact]
    public async Task CatalogLoads_LoadingIndicatorClears_AndStatusReportsTargets()
    {
        var page = await OpenAsync("");
        await WaitReadyAsync(page);

        // Once ready the indicator is gone and the status line reports the plan for the site.
        await Expect(CatalogLoading(page)).ToHaveCountAsync(0);
        await Expect(Status(page)).ToContainTextAsync("targets");
    }

    // ── view switching: the regression that shipped three times ───────────────

    [Fact]
    public async Task ClickSkyChip_MarksActive_AndUpdatesUrl_AndTitle()
    {
        var page = await OpenAsync("");
        await WaitReadyAsync(page);

        await SkyChip(page).ClickAsync();

        // URL is the source of truth; the component parses it and flips the active chip + title.
        await Expect(page).ToHaveURLAsync(new Regex("/sky-atlas$"));
        await Expect(SkyChip(page)).ToHaveClassAsync(ActiveClass);
        await Expect(PlannerChip(page)).Not.ToHaveClassAsync(ActiveClass);
        await Expect(page).ToHaveTitleAsync("Astro - Sky Atlas");

        // ...and back to the planner.
        await PlannerChip(page).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/planner$"));
        await Expect(PlannerChip(page)).ToHaveClassAsync(ActiveClass);
        await Expect(SkyChip(page)).Not.ToHaveClassAsync(ActiveClass);
    }

    [Fact]
    public async Task DeepLink_SkyAtlas_OpensWithSkyActive()
    {
        var page = await OpenAsync("sky-atlas");
        await WaitReadyAsync(page);

        await Expect(SkyChip(page)).ToHaveClassAsync(ActiveClass);
        await Expect(PlannerChip(page)).Not.ToHaveClassAsync(ActiveClass);
        await Expect(page).ToHaveTitleAsync("Astro - Sky Atlas");
    }

    [Theory]
    [InlineData("skymap")]
    [InlineData("sky")]
    public async Task DeepLink_SkyAliases_OpenAtlas(string alias)
    {
        var page = await OpenAsync(alias);
        await WaitReadyAsync(page);

        await Expect(SkyChip(page)).ToHaveClassAsync(ActiveClass);
        await Expect(page).ToHaveTitleAsync("Astro - Sky Atlas");
    }

    [Fact]
    public async Task BrowserBackForward_SyncsActiveChip()
    {
        var page = await OpenAsync("");
        await WaitReadyAsync(page);

        await SkyChip(page).ClickAsync();
        await Expect(SkyChip(page)).ToHaveClassAsync(ActiveClass);

        // Back -> planner; the LocationChanged subscription re-parses the path, so the chip and
        // title follow browser history without a reload.
        await page.GoBackAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/(planner)?$"));
        await Expect(PlannerChip(page)).ToHaveClassAsync(ActiveClass);
        await Expect(SkyChip(page)).Not.ToHaveClassAsync(ActiveClass);

        // Forward -> sky again.
        await page.GoForwardAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/sky-atlas$"));
        await Expect(SkyChip(page)).ToHaveClassAsync(ActiveClass);
    }
}
