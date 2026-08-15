using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// The sky map's whole keyboard layer hangs off DOM focus sitting on the canvas, and nothing puts it
/// back once it leaves. The canvas is <c>TabIndex="0" AutoFocus="true"</c>, so it takes focus once at
/// startup; clicking any chrome button, dismissing an overlay, or coming back to a backgrounded tab
/// moves focus elsewhere and every shortcut ([O], [D], arrows, F3) goes silently dead while the map
/// still pans and zooms perfectly, because pointer events do not need focus.
///
/// <para><b>No existing test could see this.</b> They all press through
/// <c>canvas.PressAsync(...)</c>, and Playwright FOCUSES an element before pressing a key into it, so
/// every one of them silently repaired the exact state under test. These press through
/// <c>page.Keyboard</c>, which types at whatever the document has focused -- what a user's keyboard
/// does.</para>
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class CanvasKeyboardFocusTests(TianWenWebFixture fixture)
{
    private const float BootTimeout = 120_000;
    private static readonly Regex ActiveClass = new(@"\bactive\b");

    private static async Task<bool> OverlayOnAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getRenderStats()");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("overlay").GetBoolean();
    }

    private async Task<(IPage Page, ILocator Canvas)> SkyMapAsync()
    {
        var page = await fixture.WarmPageAsync();
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });
        return (page, page.Locator("#planner"));
    }

    /// <summary>Polls the focused element's id until it matches, and returns whatever it settled on so a
    /// failure names the element that actually holds the keyboard.</summary>
    private static async Task<string> ActiveElementIdAsync(IPage page, string expected)
    {
        var id = "";
        for (var i = 0; i < 40; i++) // ~2 s
        {
            id = await page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''");
            if (id == expected) return id;
            await Task.Delay(50);
        }
        return id;
    }

    /// <summary>Presses at the DOCUMENT, the way a real keyboard does -- never at the canvas, which
    /// would focus it first and hide the defect.</summary>
    private static async Task PressAtDocumentAsync(IPage page, string key)
    {
        await page.Keyboard.PressAsync(key);
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        await Task.Delay(150);
    }

    [Fact]
    public async Task AChromeButtonClickDoesNotSwallowTheKeyboard()
    {
        var (page, canvas) = await SkyMapAsync();

        // Start from a known state, pressing at the canvas (which focuses it) so the toggle is
        // definitely reachable before the interesting part.
        if (await OverlayOnAsync(page))
        {
            await canvas.PressAsync("o");
        }

        // The everyday way focus leaves: clicking a chrome button. The chip of the view already active
        // is used deliberately -- it is the path with an early-out, so it is the one that would be
        // missed by restoring focus only where the view actually changes.
        await page.Locator("[data-view=sky]").ClickAsync();
        // The restore crosses into JS through the page's task tracker, so it lands a beat after the
        // click rather than inside it -- poll rather than sampling the instant the click returns.
        Assert.Equal("planner", await ActiveElementIdAsync(page, expected: "planner"));

        await PressAtDocumentAsync(page, "o");
        var on = await OverlayOnAsync(page);
        await RestoreOverlayOffAsync(page, canvas);
        Assert.True(on, "[O] did nothing after a chrome button click: the button kept the keyboard");
    }

    /// <summary>The warm page is shared for the whole suite, so a test that turns the overlay on owes
    /// putting it back -- otherwise every later test gets a heavier app than it arranged for.</summary>
    private static async Task RestoreOverlayOffAsync(IPage page, ILocator canvas)
    {
        for (var i = 0; i < 4 && await OverlayOnAsync(page); i++)
        {
            await canvas.PressAsync("o");
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        }
    }

    [Fact]
    public async Task DrivingTheMapWithAGestureGivesTheKeyboardBack()
    {
        var (page, canvas) = await SkyMapAsync();
        if (await OverlayOnAsync(page))
        {
            await canvas.PressAsync("o");
        }

        await page.EvaluateAsync("() => document.activeElement instanceof HTMLElement && document.activeElement.blur()");

        // A two-finger pinch -- the gesture the reported session was driven with (910 touchmoves against
        // 127 mousemoves in its trace). It proves the pointer half of the app is alive, which is exactly
        // why a dead keyboard reads as "[O] is broken" rather than "the page is ignoring me". A MOUSE
        // press would not do as the repro: the browser focuses a tabindex element on mousedown, so a
        // mouse drag repairs the state on its own, and touch does not.
        await CanvasGestures.PinchAsync(page, canvas, startGap: 200, endGap: 260, steps: 6);

        await PressAtDocumentAsync(page, "o");
        var on = await OverlayOnAsync(page);
        await RestoreOverlayOffAsync(page, canvas);
        Assert.True(on, "[O] did nothing after a touch pinch: driving the map does not restore keyboard focus");
    }
}
