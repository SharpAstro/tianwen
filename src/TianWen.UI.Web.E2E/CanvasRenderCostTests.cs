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
public sealed class CanvasRenderCostTests(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 120_000;
    private static readonly Regex ActiveClass = new(@"\bactive\b");

    /// <summary>
    /// Prints what a test actually measured, not just whether it stayed inside its bound. Every
    /// assertion here is a generous ceiling -- a gather count "under 12" is equally true at 2 and at 11,
    /// and only one of those is the fix still working. Without the number a run says the suite is green
    /// and says nothing about whether the win eroded, which is the thing a perf test exists to notice.
    /// Surfaced with <c>--logger "console;verbosity=detailed"</c>.
    /// </summary>
    private void Report(string measurement) => output.WriteLine($"[perf] {measurement}");

    private readonly record struct RenderStats(
        int Frames, int Coalesced, int Gathers, bool Overlay, int Uploads, double LabelMs);

    private static async Task<RenderStats> GetStatsAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getRenderStats()");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new RenderStats(
            r.GetProperty("frames").GetInt32(),
            r.GetProperty("coalesced").GetInt32(),
            r.GetProperty("gathers").GetInt32(),
            r.GetProperty("overlay").GetBoolean(),
            r.GetProperty("uploads").GetInt32(),
            // Only ever advances on a frame that actually DREW labels, which is what makes it the
            // observable for "did the labels come back" -- the pixels cannot say, since a frame with
            // labels and a frame without are both correct renders of their own moment.
            r.GetProperty("labelMs").GetDouble());
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
        // Wait out the Tycho-2 atlas load, which DELIBERATELY ignores canvas input while it runs. A test
        // that drives a gesture during it measures nothing: the moves are dropped, the view never moves,
        // and nothing repaints. Invisible on a Lightweight dev build (no atlas, so no busy phase) and
        // certain against the deployed site, where the 28 MB fetch outlasts the boot wait above.
        await Expect(page.Locator("[data-atlas-loading]")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
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

        Report($"burst pinch: {events} events -> {painted} painted, {coalesced} coalesced away "
            + $"({(events == 0 ? 0 : 100.0 * coalesced / events):F0}% of events)");

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

        Report($"paced zoom: {events} events -> {painted} painted, {gathers} overlay gathers "
            + $"(per-event would be {painted})");

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

    /// <summary>
    /// The overlay's GPU instance buffer across a PAN. The candidate gather is already cached across a
    /// pan; the instances additionally depend on the arcmin-to-pixel scale and the wide-FOV fade, which
    /// a pan does not move either -- so a drag must upload nothing, and the buffer is ~311 KB at a wide
    /// zoom.
    ///
    /// <para>Stated as "no more uploads than gathers" rather than "zero", because a re-gather is a
    /// legitimate reason to re-upload: the assertion is that the pan itself is not one. That also keeps
    /// it valid at any field of view, where a flat zero would only hold above the wide threshold.</para>
    ///
    /// <para>Like the gather counter beside it, this cannot be observed from the output: a stale-keyed
    /// re-upload draws the byte-identical frame, just after a needless megabyte of interop.</para>
    /// </summary>
    [Fact]
    public async Task APanDoesNotReUploadTheOverlayInstanceBuffer()
    {
        var page = await WarmSkyAtlasAsync();
        var canvas = page.Locator("#planner");
        await EnsureObjectOverlayOnAsync(page, canvas);

        RenderStats before, afterPan, afterZoom;
        try
        {
            // One upload has to have happened before the pan, or "it did not upload" is vacuous.
            await CanvasGestures.WheelZoomAsync(page, canvas, events: 2, deltaPerEvent: 1.5, burst: false);
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
            before = await GetStatsAsync(page);
            Assert.True(before.Uploads > 0,
                "no instance buffer was ever uploaded; the pan assertion below would measure nothing");

            var box = await canvas.BoundingBoxAsync() ?? throw new InvalidOperationException("no canvas box");
            var y = box.Y + (box.Height / 2);
            await page.Mouse.MoveAsync(box.X + (box.Width * 0.6f), y);
            await page.Mouse.DownAsync();
            for (var i = 1; i <= 20; i++)
            {
                await page.Mouse.MoveAsync(box.X + (box.Width * 0.6f) - (i * 6), y);
                await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
            }
            await page.Mouse.UpAsync();
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
            afterPan = await GetStatsAsync(page);

            // A zoom, by contrast, MUST re-upload: it changes the scale every marker is sized by. Without
            // this half the test would also pass on a buffer that is never uploaded again at all.
            await CanvasGestures.WheelZoomAsync(page, canvas, events: 6, deltaPerEvent: 1.5, burst: false);
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
            afterZoom = await GetStatsAsync(page);
        }
        finally
        {
            await SetObjectOverlayAsync(page, canvas, on: false);
        }

        var painted = afterPan.Frames - before.Frames;
        Report($"20-step pan: {painted} painted, uploads +{afterPan.Uploads - before.Uploads}, "
            + $"gathers +{afterPan.Gathers - before.Gathers}; the zoom after it uploaded "
            + $"+{afterZoom.Uploads - afterPan.Uploads}");

        Assert.True(painted > 0, "the pan painted nothing, so it measured nothing");
        Assert.True(afterPan.Uploads - before.Uploads <= afterPan.Gathers - before.Gathers,
            $"a pan re-uploaded the instance buffer without re-gathering; uploads +{afterPan.Uploads - before.Uploads}, "
            + $"gathers +{afterPan.Gathers - before.Gathers} over {painted} painted frames");
        Assert.True(afterZoom.Uploads > afterPan.Uploads,
            "a zoom must re-upload the instance buffer; it changes the scale every marker is sized by");
    }

    /// <summary>
    /// Labels must not blink back mid-drag. They are hidden while the view moves (about half a gesture's
    /// frame time, spent on text sliding past too fast to read) and drawn again once it stops -- and a
    /// pure DELAY cannot tell a pause from an end. Measured over a real touch session: 3.7% of the gaps
    /// between moves WITHIN one gesture were longer than the 120 ms debounce (33 of them), so the labels
    /// came back mid-drag and the next move hid them again. That is the reported flicker, and shortening
    /// the delay makes it worse (5.5% of gaps clear 60 ms).
    ///
    /// <para>So a pointer gesture states when it is over rather than being timed: the press holds the
    /// settle off, and the RELEASE brings the labels back by itself -- the release frame paints a view
    /// that has not moved since the last one, so nothing is suppressed.</para>
    /// </summary>
    [Fact]
    public async Task APauseInTheMiddleOfADragDoesNotFlashTheLabelsBack()
    {
        var page = await WarmSkyAtlasAsync();
        var canvas = page.Locator("#planner");
        var box = await canvas.BoundingBoxAsync() ?? throw new InvalidOperationException("canvas not visible");
        var cx = box.X + (box.Width / 2);
        var cy = box.Y + (box.Height / 2);

        await EnsureObjectOverlayOnAsync(page, canvas);
        try
        {
            await page.Mouse.MoveAsync((float)cx, (float)cy);
            await page.Mouse.DownAsync();
            for (var i = 1; i <= 6; i++)
            {
                await page.Mouse.MoveAsync((float)(cx + (i * 12)), (float)cy);
            }
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
            var moving = await GetStatsAsync(page);

            // Hold still, fingers/button still DOWN, well past the 120 ms settle. A human pausing to
            // look at where they have dragged to is the everyday version of this.
            await Task.Delay(500, TestContext.Current.CancellationToken);
            var paused = await GetStatsAsync(page);

            await page.Mouse.UpAsync();
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
            await Task.Delay(300, TestContext.Current.CancellationToken);
            var released = await GetStatsAsync(page);

            Report($"drag label settle: moving {moving.LabelMs:F1} ms -> paused {paused.LabelMs:F1} ms "
                + $"(+{paused.LabelMs - moving.LabelMs:F2} during a 500 ms hold) -> released {released.LabelMs:F1} ms "
                + $"(+{released.LabelMs - paused.LabelMs:F1} once the gesture ended)");

            Assert.True(paused.LabelMs - moving.LabelMs < 0.01,
                $"the labels were redrawn during a 500 ms pause inside a drag (+{paused.LabelMs - moving.LabelMs:F1} ms "
                + "of label work): the next move hides them again, which is the flicker");
            Assert.True(released.LabelMs - paused.LabelMs > 0,
                "the labels never came back after the drag ended, so the suppression is permanent");
        }
        finally
        {
            await SetObjectOverlayAsync(page, canvas, on: false);
        }
    }

    /// <summary>
    /// An idle map must paint NOTHING. There is no render loop here, so a frame while nobody is touching
    /// it can only come from the app asking for one -- and the label-settle repaint is exactly such a
    /// request, scheduled by any frame that suppressed its labels for a moving view.
    ///
    /// <para><b>The failure mode is a self-feeding loop, not a wasted frame.</b> Motion is decided by
    /// comparing the view centre against the previous frame's, and in ALT-AZ mode the equatorial centre
    /// drifts with the live clock -- so a settle repaint arriving 120 ms later legitimately sees a moved
    /// view, suppresses its labels again, and schedules another. The map then repaints for ever at ~8 fps
    /// with the labels never landing, which is what a "flicker after the movement stops" looks like.</para>
    ///
    /// <para>Asserted over a window many times the settle delay, so one late repaint passes and a
    /// standing loop cannot.</para>
    /// </summary>
    [Fact]
    public async Task AnIdleMapPaintsNothingOnceTheLabelsHaveSettled()
    {
        var page = await WarmSkyAtlasAsync();
        var canvas = page.Locator("#planner");

        await EnsureObjectOverlayOnAsync(page, canvas);
        try
        {
            // Move the view, so the labels are suppressed and a settle repaint is genuinely owed.
            await CanvasGestures.WheelZoomAsync(page, canvas, events: 6, deltaPerEvent: -1.5, burst: false);

            // Well past the 120 ms settle: anything after this point is unrequested.
            await Task.Delay(800, TestContext.Current.CancellationToken);
            var settled = await GetStatsAsync(page);
            await Task.Delay(2000, TestContext.Current.CancellationToken);
            var idle = await GetStatsAsync(page);

            var painted = idle.Frames - settled.Frames;
            Report($"idle map: {painted} frames painted over 2 s with nobody touching it");

            Assert.True(painted <= 1,
                $"the map painted {painted} frames over 2 s while nobody touched it: the label-settle "
                + "repaint is re-arming itself, so the labels never land and the map never goes quiet");
        }
        finally
        {
            await SetObjectOverlayAsync(page, canvas, on: false);
        }
    }
}
