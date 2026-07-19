using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Browser E2E for canvas touch gestures on the sky atlas. Unlike <see cref="NavigationTests"/> (which
/// asserts on chrome DOM), these drive the WebGL canvas itself and assert the *view state* changed —
/// impossible to read from the DOM of a single &lt;canvas&gt;, so they use the <c>?e2e=1</c> view-state
/// hook (<c>window.__tianwenTest.getSkyView()</c>, wired in index.html + Planner.razor) to read the
/// live field-of-view before/after a synthesized two-finger pinch (<see cref="CanvasGestures"/>, CDP).
///
/// This closes the gap the Drawboard canvas-E2E investigation surfaced: neither codebase tested
/// touch/pinch, because a real canvas offers no DOM to assert on — the fix is a read-only view-state
/// hook plus a CDP multi-touch helper.
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class CanvasGestureTests(TianWenWebFixture fixture)
{
    private const float BootTimeout = 120_000;

    // Opens the sky atlas directly (?view=sky) with the E2E view-state hook on (?e2e=1) and waits for
    // the catalog to finish loading (the same readiness gate NavigationTests uses).
    private async Task<IPage> OpenSkyAtlasAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + "?view=sky&e2e=1",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        return page;
    }

    private static async Task<double> GetFovAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getSkyView()");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("fovDeg").GetDouble();
    }

    // "Render idle" for the canvas: poll the view hook until the FOV stops changing (the Drawboard
    // waitForBoundingBoxToStopChanging pattern, adapted from a bounding box to the view-state hook).
    private static async Task WaitForFovToSettleAsync(IPage page)
    {
        var last = double.NaN;
        for (var i = 0; i < 40; i++)
        {
            var fov = await GetFovAsync(page);
            if (fov == last) return;
            last = fov;
            await page.WaitForTimeoutAsync(50);
        }
    }

    [Fact]
    public async Task PinchOut_ZoomsIn_ShrinksFieldOfView()
    {
        var page = await OpenSkyAtlasAsync();
        var canvas = page.Locator("#planner");

        var before = await GetFovAsync(page);
        await CanvasGestures.PinchAsync(page, canvas, startGap: 60, endGap: 320); // spread fingers = zoom in
        await WaitForFovToSettleAsync(page);
        var after = await GetFovAsync(page);

        Assert.True(after < before, $"pinch-out should shrink the FOV (zoom in); before={before}, after={after}");
    }

    [Fact]
    public async Task PinchIn_ZoomsOut_GrowsFieldOfView()
    {
        var page = await OpenSkyAtlasAsync();
        var canvas = page.Locator("#planner");

        var before = await GetFovAsync(page);
        await CanvasGestures.PinchAsync(page, canvas, startGap: 320, endGap: 60); // close fingers = zoom out
        await WaitForFovToSettleAsync(page);
        var after = await GetFovAsync(page);

        Assert.True(after > before, $"pinch-in should grow the FOV (zoom out); before={before}, after={after}");
    }
}
