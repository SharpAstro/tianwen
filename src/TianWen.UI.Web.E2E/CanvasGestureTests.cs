using System.Text.Json;
using System.Text.RegularExpressions;
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
    private static readonly Regex ActiveClass = new(@"\bactive\b");

    // Reuses the shared warm page (booted once by the fixture) and arranges the sky atlas via an
    // IN-APP chip nav (no reload -> no re-boot). Safe to share: the pinch assertions are RELATIVE
    // (after vs before), so a FOV left by a prior warm test is irrelevant. The ?e2e=1 view-state hook
    // was registered on the warm page's initial load and persists across the in-app nav.
    private async Task<IPage> WarmSkyAtlasAsync()
    {
        var page = await fixture.WarmPageAsync();
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });
        await WaitForFovToSettleAsync(page);
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
        var page = await WarmSkyAtlasAsync();
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
        var page = await WarmSkyAtlasAsync();
        var canvas = page.Locator("#planner");

        var before = await GetFovAsync(page);
        await CanvasGestures.PinchAsync(page, canvas, startGap: 320, endGap: 60); // close fingers = zoom out
        await WaitForFovToSettleAsync(page);
        var after = await GetFovAsync(page);

        Assert.True(after > before, $"pinch-in should grow the FOV (zoom out); before={before}, after={after}");
    }
}
