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

    /// <summary>Keys the DOCUMENT listener delivered, i.e. ones the canvas would not have heard. Without
    /// this a test that presses at the document and sees the map react cannot tell the fallback carrying
    /// the key from focus having been on the canvas anyway -- and the latter passes with it removed.
    ///
    /// <para>Returns -1 when the build reports no such counter, rather than throwing. The failing
    /// baseline for these tests is the DEPLOYED build, which predates the field, and a crash on the
    /// missing property would pre-empt the behavioural assertion that is the point of the test.</para>
    /// </summary>
    private static async Task<int> DocumentKeysAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getRenderStats()");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("documentKeys", out var keys) ? keys.GetInt32() : -1;
    }

    private async Task<(IPage Page, ILocator Canvas)> SkyMapAsync()
    {
        var page = await fixture.WarmPageAsync();
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });
        // The Tycho-2 atlas load deliberately ignores canvas input while it runs, so a gesture driven
        // during it is dropped and proves nothing about focus.
        await Expect(page.Locator("[data-atlas-loading]")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
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

    /// <summary>
    /// The residual gap the two tests above cannot close: focus taken by something the app never sees.
    /// Devtools opening, an alt-tab that returns to &lt;body&gt;, an extension, the browser's find bar --
    /// none of them is a click the app handles or a gesture on the map, so nothing calls
    /// RestoreCanvasFocus and there is nothing that COULD. The keyboard has to reach the app without
    /// focus rather than by being handed focus back, which is what document-keys.js does.
    ///
    /// <para>Deliberately no gesture anywhere in this test: a single pointer event would repair the
    /// state under test and it would pass with the fallback removed.</para>
    /// </summary>
    [Fact]
    public async Task KeysStillReachTheMapWhenFocusIsSomewhereTheAppNeverSaw()
    {
        var (page, canvas) = await SkyMapAsync();
        if (await OverlayOnAsync(page))
        {
            await canvas.PressAsync("o");
        }

        // Let the chip click's focus restore LAND before taking focus away. It goes through the page's
        // task tracker, so a blur issued straight after the click is overtaken by it and the canvas is
        // focused again -- the test would then be measuring the race, not the state it set up.
        Assert.Equal("planner", await ActiveElementIdAsync(page, expected: "planner"));

        await page.EvaluateAsync("() => document.activeElement instanceof HTMLElement && document.activeElement.blur()");
        Assert.NotEqual("planner", await page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''"));

        var before = await DocumentKeysAsync(page);
        await PressAtDocumentAsync(page, "o");
        var on = await OverlayOnAsync(page);
        var after = await DocumentKeysAsync(page);
        await RestoreOverlayOffAsync(page, canvas);

        // Behaviour first, provenance second: the first assertion is the bug as a user meets it, and
        // the second is what makes the pass mean something (the fallback carried it, not stray focus).
        Assert.True(on,
            "[O] did nothing with focus off the canvas: there was no gesture to hand it back, so the "
            + "document listener is the only path the key had");
        Assert.True(before >= 0 && after > before,
            $"the overlay toggled but the document listener did not deliver it (documentKeys {before} -> {after})");
    }

    /// <summary>
    /// The other half of the same rule: a real DOM input owns every key while it is focused, letters
    /// included. Lat/Lon are ordinary number inputs sitting beside the canvas, so a document listener
    /// that took keys unconditionally would toggle the overlay while somebody types a latitude -- the
    /// obvious way to over-fix the test above, and invisible unless it is asserted.
    /// </summary>
    [Fact]
    public async Task ARealInputKeepsItsOwnKeys()
    {
        var (page, canvas) = await SkyMapAsync();
        if (await OverlayOnAsync(page))
        {
            await canvas.PressAsync("o");
        }

        // Same ordering point as the test above: the chip click restores canvas focus asynchronously,
        // and a restore arriving after the Lat click would take the keyboard back off the input.
        Assert.Equal("planner", await ActiveElementIdAsync(page, expected: "planner"));

        var lat = page.Locator("input[type=number]").First;
        await lat.ClickAsync();
        await Expect(lat).ToBeFocusedAsync();

        var before = await DocumentKeysAsync(page);
        await PressAtDocumentAsync(page, "o");
        var on = await OverlayOnAsync(page);
        var after = await DocumentKeysAsync(page);
        await RestoreOverlayOffAsync(page, canvas);

        Assert.False(on, "typing into the Lat field toggled the map's object overlay");
        Assert.Equal(before, after);
    }
}
