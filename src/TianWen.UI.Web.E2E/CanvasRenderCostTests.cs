using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Browser E2E for the COST of a continuous gesture, as opposed to <see cref="CanvasGestureTests"/>
/// which asserts its effect. Both fixes these pin were found by reading a Chrome trace of a real touch
/// session by hand; the point of these tests is that the next regression does not need that.
///
/// <para>Two independent properties, asserted separately because each one hides the other:</para>
/// <list type="number">
/// <item><b>Repaints coalesce onto the frame clock.</b> The web host has no render loop, so every input
/// event used to paint a full frame synchronously. In the traced session 1096 of 1535 move-driven
/// repaints (71%) were superseded inside their own 16.67 ms window, 275 windows carrying four each.</item>
/// <item><b>The object-overlay candidate cache survives a zoom.</b> Its key quantizes the centre into
/// FOV/8 cells, but the step was taken from the RAW field of view, so a zoom rescaled the grid
/// continuously and the rounded centre moved on every event even with the centre perfectly still - the
/// cache missed per tick and re-walked the catalog. Measured over a 60->30 degree pinch: 69 gathers
/// against 8. That is why a pinch was the app's most expensive gesture (touchmove p95 91 ms, max
/// 246 ms) while a pan of 1.4h of RA cost 3 gathers.</item>
/// </list>
///
/// <para>Read through the <c>?e2e=1</c> render-stats hook (<c>window.__tianwenTest.getRenderStats()</c>).
/// A cache miss and a superseded repaint both produce the byte-identical picture, just later, so there is
/// nothing in the output - pixels included - that could distinguish either from correct behaviour. Only a
/// count can, which is the same reason <c>SkyMapState.PlanetCacheRebuilds</c> exists.</para>
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class CanvasRenderCostTests(TianWenWebFixture fixture)
{
    private const float BootTimeout = 120_000;
    private static readonly Regex ActiveClass = new(@"\bactive\b");

    private readonly record struct RenderStats(int Frames, int Coalesced, int Gathers, bool Overlay);

    private static async Task<RenderStats> GetStatsAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getRenderStats()");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new RenderStats(
            r.GetProperty("frames").GetInt32(),
            r.GetProperty("coalesced").GetInt32(),
            r.GetProperty("gathers").GetInt32(),
            r.GetProperty("overlay").GetBoolean());
    }

    /// <summary>
    /// Turns the [O] catalog overlay ON, which is what makes the gather run at all -- it is off by
    /// default (the sky map is already dense), and with it off <c>RenderObjectOverlayPrimitive</c>
    /// early-outs before gathering. Presses until the state hook confirms it rather than pressing once,
    /// so the test cannot silently measure nothing.
    /// </summary>
    private static Task EnsureObjectOverlayOnAsync(IPage page, ILocator canvas) => SetObjectOverlayAsync(page, canvas, on: true);

    /// <summary>
    /// Drives the [O] toggle to a known state. Restoring it afterwards matters because the warm page is
    /// SHARED for the whole suite and the fixture's contract is that each warm test arranges its own
    /// start state - leaving the overlay on would hand every later test a different (and heavier) app
    /// than the one it expects.
    /// </summary>
    private static async Task SetObjectOverlayAsync(IPage page, ILocator canvas, bool on)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if ((await GetStatsAsync(page)).Overlay == on) return;
            await canvas.PressAsync("o");
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        }
        Assert.Fail($"could not drive the [O] catalog overlay to {on}; the gather assertions would measure nothing");
    }

    private async Task<IPage> WarmSkyAtlasAsync()
    {
        var page = await fixture.WarmPageAsync();
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });
        // Let any in-flight repaint land so the before-stats are a settled baseline.
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        return page;
    }

    /// <summary>
    /// A finger reaches the app TWICE: once as the touch events webgl-canvas.js bridges, and once as the
    /// pointer stream, which fires for touch as well. <c>WebGlCanvas.HandlePointerMoveAsync</c> discards
    /// the second, but only AFTER Blazor has serialized a full <c>PointerEventArgs</c> and crossed into
    /// .NET, which is the entire cost of an event that was never going to be used: 812 dispatches and
    /// 0.657 s in the traced session, every one thrown away.
    ///
    /// <para>The canvas now stops a touch-sourced pointermove at the target. This asserts the mechanism
    /// at the exact boundary that matters: Blazor DELEGATES, registering one listener per event type on
    /// <c>document</c>, and uses the capture phase only for its non-bubbling set (focus / blur /
    /// mouseenter / pointerenter), which pointermove is not in. So a document-level bubble listener sees
    /// precisely what Blazor's dispatcher would see.</para>
    ///
    /// <para>Mouse and pen must still get through: they have no bridge, so the pointer stream IS their
    /// input, and blanket-stopping it would delete mouse dragging entirely.</para>
    /// </summary>
    [Fact]
    public async Task ATouchPointerMoveIsStoppedBeforeBlazorButMouseAndPenGetThrough()
    {
        var page = await WarmSkyAtlasAsync();
        var canvas = page.Locator("#planner");

        var reached = await canvas.EvaluateAsync<string>("""
            (el) => {
              const seen = [];
              const spy = (e) => seen.push(e.pointerType);
              // Same target and phase as Blazor's own delegated listener.
              document.addEventListener('pointermove', spy);
              try {
                for (const t of ['touch', 'mouse', 'pen']) {
                  el.dispatchEvent(new PointerEvent('pointermove', {
                    pointerType: t, bubbles: true, cancelable: true, clientX: 10, clientY: 10,
                  }));
                }
              } finally {
                document.removeEventListener('pointermove', spy);
              }
              return seen.join(',');
            }
            """);

        Assert.Equal("mouse,pen", reached);
    }

    /// <summary>
    /// A dense gesture must not paint per event. The burst puts every wheel event in one JS task, so the
    /// browser has no chance to paint between them: a coalescing app paints once for the whole run, and
    /// one that does not paints 40 frames of which 39 are never seen.
    /// </summary>
    [Fact]
    public async Task ADenseTrackpadPinchPaintsOncePerFrameNotOncePerEvent()
    {
        var page = await WarmSkyAtlasAsync();
        var canvas = page.Locator("#planner");
        const int events = 40;

        var before = await GetStatsAsync(page);
        await CanvasGestures.TrackpadPinchAsync(page, canvas, events, deltaPerEvent: -1.0, burst: true);
        // Give the scheduled repaint its frame, then one more so a second would have landed too.
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r())))");
        var after = await GetStatsAsync(page);

        var painted = after.Frames - before.Frames;
        var coalesced = after.Coalesced - before.Coalesced;

        Assert.True(coalesced > 0,
            $"a {events}-event burst in one task must coalesce; coalesced={coalesced}, painted={painted}");
        Assert.True(painted < events,
            $"painted {painted} frames for {events} events - repaints are not coalescing onto the frame clock");
    }

    /// <summary>
    /// The overlay cache across a zoom, measured where a burst cannot help: one event per animation
    /// frame, so every event genuinely paints. The gathers must then be bounded by the FOV buckets the
    /// zoom crossed, NOT by the number of frames painted.
    ///
    /// <para>The delta is small on purpose (~1.4x total zoom, ~4 buckets of 10%), which is what makes the
    /// assertion discriminating: before the fix this cost a gather on every one of the 30 events.</para>
    /// </summary>
    [Fact]
    public async Task APacedZoomDoesNotReGatherTheOverlayOnEveryFrame()
    {
        var page = await WarmSkyAtlasAsync();
        var canvas = page.Locator("#planner");
        const int events = 30;
        await EnsureObjectOverlayOnAsync(page, canvas);

        RenderStats before, after;
        try
        {
            before = await GetStatsAsync(page);
            await CanvasGestures.TrackpadPinchAsync(page, canvas, events, deltaPerEvent: -1.5, burst: false);
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
            after = await GetStatsAsync(page);
        }
        finally
        {
            // Hand the shared warm page back with the overlay off, however this test ends.
            await SetObjectOverlayAsync(page, canvas, on: false);
        }

        var painted = after.Frames - before.Frames;
        var gathers = after.Gathers - before.Gathers;

        // The zoom really happened, so SOME repaints landed - otherwise the test proves nothing.
        Assert.True(painted >= events / 2,
            $"expected the paced gesture to paint per event; painted={painted} for {events} events");
        // Non-zero, or the assertion below would pass on a gather that never ran. This is the guard the
        // first version of this test lacked: the overlay is off by default, so it passed on gathers=0
        // even with the fix reverted.
        Assert.True(gathers > 0,
            "the overlay never gathered, so the bound below would be vacuous");
        // Bounded well under the painted count. Generous (the exact bucket count depends on the starting
        // FOV the warm page happens to be at) but far below per-event, which is the regression.
        Assert.True(gathers <= 12,
            $"overlay re-gathered {gathers} times over {painted} painted frames - the cache key is "
            + "missing per event, which is the raw-FOV grid-step regression");
    }
}
