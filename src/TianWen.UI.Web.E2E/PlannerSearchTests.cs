using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Browser E2E for the planner search box (canvas-drawn) + its autocomplete dropdown. Reproduces the
/// reported bug: committing a suggestion by MOUSE CLICK left the floating &lt;input&gt; overlay stranded
/// (display:block, showing the typed text) over the canvas and killed all further typing -- while the
/// keyboard path (arrow-down + Enter) worked. Root cause: clicking the canvas blurs the &lt;input&gt;,
/// which fires synchronously and clears the overlay's IsVisible flag before the async .NET commit runs,
/// so HideAsync (guarded on IsVisible) no-op'd and never set display:none. The canvas has no DOM for the
/// search box / rows, so this reads their arranged rects via the ?e2e=1 hooks
/// (window.__tianwenTest.getPlannerSearchRect / getPlannerSuggestionRect), clicks the canvas at those
/// spots, and asserts on the one real DOM element in play -- the .canvas-text-input overlay.
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class PlannerSearchTests(TianWenWebFixture fixture)
{
    // Interpreted-WASM cold boot + catalog init + the autocomplete-cache build (walks every catalog
    // designation -- interpreter-slow on the dev server) dwarf any DOM settle. The deployed AOT build
    // does it in ~1 s; the tests run against the dev server, so the ceiling is generous.
    private const float BootTimeout = 120_000;
    private static readonly Regex ActiveClass = new(@"\bactive\b");

    // Reuses the shared warm page (booted once by the fixture) instead of a fresh boot per test.
    // Arranges a clean planner state: switch to the planner view via an IN-APP chip nav (no reload)
    // and dismiss any stray floating search overlay a prior warm test left, so each test stays
    // order-independent. Clicking the planner chip also unconditionally hides an open overlay (the
    // view-switch behaviour SwitchViewWhileSearchFocused pins), so the Escape is belt-and-suspenders.
    private async Task<IPage> WarmPlannerAsync()
    {
        var page = await fixture.WarmPageAsync();
        await page.Locator("[data-view=planner]").ClickAsync();
        await Expect(page.Locator("[data-view=planner]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".canvas-text-input")).ToBeHiddenAsync(new() { Timeout = BootTimeout });
        return page;
    }

    private readonly record struct Rect(double X, double Y, double W, double H);

    private static Rect? ParseRect(string json)
    {
        if (json == "null") return null;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new Rect(r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble(),
            r.GetProperty("w").GetDouble(), r.GetProperty("h").GetDouble());
    }

    // Polls a ?e2e=1 rect hook until it returns a real rect. Regions are re-registered every frame, and
    // the dropdown only appears once the autocomplete cache has built + a query is typed, so a poll is
    // the honest wait (there is no DOM event for a canvas repaint).
    private static async Task<Rect> GetRectAsync(IPage page, string fn, int? arg = null)
    {
        var call = arg is { } a
            ? $"async () => await window.__tianwenTest.{fn}({a})"
            : $"async () => await window.__tianwenTest.{fn}()";
        for (var i = 0; i < 300; i++) // ~30 s
        {
            if (ParseRect(await page.EvaluateAsync<string>(call)) is { W: > 0, H: > 0 } r) return r;
            await page.WaitForTimeoutAsync(100);
        }
        throw new TimeoutException($"the ?e2e=1 hook {fn}({arg}) never returned a non-empty rect");
    }

    private static Position Center(Rect r) => new() { X = (float)(r.X + r.W / 2), Y = (float)(r.Y + r.H / 2) };

    [Fact]
    public async Task ClickSuggestion_CommitsHidesOverlay_AndSearchStaysUsable()
    {
        var page = await WarmPlannerAsync();
        var canvas = page.Locator("#planner");
        var input = page.Locator(".canvas-text-input");

        // 1. Activate the canvas search box -> the floating <input> overlay shows + focuses.
        var searchRect = await GetRectAsync(page, "getPlannerSearchRect");
        await canvas.ClickAsync(new LocatorClickOptions { Position = Center(searchRect) });
        await Expect(input).ToBeVisibleAsync(new() { Timeout = BootTimeout });

        // 2. Type a query -> the autocomplete dropdown renders on the canvas.
        await input.PressSequentiallyAsync("Androm");
        var suggestionRect = await GetRectAsync(page, "getPlannerSuggestionRect", 0);

        // 3. Commit by MOUSE CLICK on the first suggestion row (the path that used to break).
        await canvas.ClickAsync(new LocatorClickOptions { Position = Center(suggestionRect) });

        // 4. The overlay must be hidden after the commit (before the fix it stayed display:block).
        await Expect(input).ToBeHiddenAsync(new() { Timeout = BootTimeout });

        // 5. The search must still work: re-activate, type, and a suggestion appears again. Before the
        //    fix the orphaned overlay swallowed input (the .NET side was deactivated), so a second
        //    search did nothing at all -- exactly the "search doesn't work anymore" report.
        await canvas.ClickAsync(new LocatorClickOptions { Position = Center(searchRect) });
        await Expect(input).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await input.PressSequentiallyAsync("M42");
        await GetRectAsync(page, "getPlannerSuggestionRect", 0);
    }

    // The blur-FIRST ordering the suggestion-click test cannot reach: clicking a DOM element (the Sky
    // Atlas chip) blurs the <input> BEFORE the Blazor click handler runs, so the overlay's IsVisible is
    // already false by the time the view switch deactivates the input. Both the overlay's HideAsync and
    // the host's DeactivateTextInput must therefore hide UNCONDITIONALLY -- an IsVisible guard at either
    // layer strands the visible input over the sky atlas (the reported screenshot: "Andro" box on the
    // atlas). This is the view-switch counterpart of ClickSuggestion_CommitsHidesOverlay.
    [Fact]
    public async Task SwitchViewWhileSearchFocused_HidesOverlay()
    {
        var page = await WarmPlannerAsync();
        var canvas = page.Locator("#planner");
        var input = page.Locator(".canvas-text-input");

        // Focus the canvas search box and type (the overlay shows, focused).
        var searchRect = await GetRectAsync(page, "getPlannerSearchRect");
        await canvas.ClickAsync(new LocatorClickOptions { Position = Center(searchRect) });
        await Expect(input).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await input.PressSequentiallyAsync("Andro");

        // Switch to the Sky Atlas via the DOM chip while the input is still focused.
        await page.Locator("[data-view=sky]").ClickAsync();

        // The overlay must not carry onto the atlas.
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
        await Expect(input).ToBeHiddenAsync(new() { Timeout = BootTimeout });
    }
}
