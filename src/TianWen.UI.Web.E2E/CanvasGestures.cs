using System.Globalization;
using Microsoft.Playwright;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Canvas gesture helpers for the WebGL sky atlas. The app is a single &lt;canvas&gt; with no DOM to
/// drive, and Playwright has no public multi-touch API, so a two-finger pinch is synthesized over the
/// Chrome DevTools Protocol (<c>Input.dispatchTouchEvent</c> with two <c>touchPoints</c>); the
/// technique the Drawboard canvas-E2E investigation surfaced as the genuine gap (both codebases
/// lacked it). Coordinates are page (viewport) pixels, which CDP treats as clientX/clientY.
/// </summary>
internal static class CanvasGestures
{
    /// <summary>
    /// A two-finger pinch centred on <paramref name="target"/>: two fingers on a horizontal line about
    /// the centre, animating the inter-finger gap from <paramref name="startGap"/> to
    /// <paramref name="endGap"/> over <paramref name="steps"/> moves. Spreading the fingers (end &gt;
    /// start) zooms IN; closing them (end &lt; start) zooms OUT.
    /// </summary>
    public static async Task PinchAsync(
        IPage page, ILocator target, double startGap, double endGap, int steps = 12)
    {
        var box = await target.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Pinch target has no bounding box (not visible).");
        var cx = box.X + (box.Width / 2);
        var cy = box.Y + (box.Height / 2);
        var cdp = await page.Context.NewCDPSessionAsync(page);

        // Two horizontal touch points `gap` apart, centred on (cx, cy).
        static object[] Points(double cx, double cy, double gap) =>
        [
            new Dictionary<string, object> { ["x"] = cx - (gap / 2), ["y"] = cy },
            new Dictionary<string, object> { ["x"] = cx + (gap / 2), ["y"] = cy },
        ];

        await cdp.SendAsync("Input.dispatchTouchEvent", new Dictionary<string, object>
        {
            ["type"] = "touchStart",
            ["touchPoints"] = Points(cx, cy, startGap),
        });

        for (var i = 1; i <= steps; i++)
        {
            var gap = startGap + ((endGap - startGap) * i / steps);
            await cdp.SendAsync("Input.dispatchTouchEvent", new Dictionary<string, object>
            {
                ["type"] = "touchMove",
                ["touchPoints"] = Points(cx, cy, gap),
            });
            await page.WaitForTimeoutAsync(16); // ~1 frame between moves so each is processed
        }

        await cdp.SendAsync("Input.dispatchTouchEvent", new Dictionary<string, object>
        {
            ["type"] = "touchEnd",
            ["touchPoints"] = Array.Empty<object>(), // release all fingers
        });
    }

    /// <summary>
    /// A TRACKPAD pinch, which is a different input path from <see cref="PinchAsync"/> and was the
    /// densest gesture in a real browser trace: a browser reports it as a stream of <c>wheel</c> events
    /// with <c>ctrlKey</c> set (there is no pinch event and no touch event), so it arrives through
    /// Blazor's <c>@onwheel</c> binding rather than the canvas touch bridge.
    ///
    /// <para><paramref name="burst"/> selects WHICH property this exercises, and the two are mutually
    /// exclusive by construction:</para>
    /// <list type="bullet">
    /// <item><b>true</b> - every event is dispatched inside ONE JS task, so the browser cannot paint
    /// between them. This is the frame-coalescing case: a correct app paints once.</item>
    /// <item><b>false</b> - one event per animation frame, so each one paints. This is the case that can
    /// see per-repaint work such as the overlay candidate gather; a burst would collapse it to one
    /// repaint and hide exactly what is being measured.</item>
    /// </list>
    ///
    /// <para>Dispatched in-page rather than through <c>page.Mouse.WheelAsync</c> because that is a
    /// round-trip per event, which inserts a paint opportunity between them and makes the burst case
    /// timing-dependent instead of deterministic.</para>
    /// </summary>
    /// <param name="deltaPerEvent">Wheel deltaY per event; negative zooms IN. Small values keep the
    /// total zoom (and so the number of FOV buckets crossed) low.</param>
    public static async Task TrackpadPinchAsync(
        IPage page, ILocator target, int events, double deltaPerEvent, bool burst)
        => await WheelAsync(page, target, events, deltaPerEvent, burst, ctrlKey: true);

    /// <summary>
    /// A PLAIN wheel, which is what a mouse wheel and a two-finger trackpad scroll deliver, and what
    /// zooms the sky map. Distinct from <see cref="TrackpadPinchAsync"/> (ctrl+wheel) because the two
    /// reach the app by different routes and only one of them is a pinch; a real session's trace shows
    /// plain wheels, so a measurement driven by ctrl+wheel would be measuring the other gesture.
    /// </summary>
    /// <param name="deltaPerEvent">Wheel deltaY per event; positive zooms OUT on the sky map.</param>
    public static async Task WheelZoomAsync(
        IPage page, ILocator target, int events, double deltaPerEvent, bool burst)
        => await WheelAsync(page, target, events, deltaPerEvent, burst, ctrlKey: false);

    private static async Task WheelAsync(
        IPage page, ILocator target, int events, double deltaPerEvent, bool burst, bool ctrlKey)
    {
        var box = await target.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Wheel target has no bounding box (not visible).");
        var cx = box.X + (box.Width / 2);
        var cy = box.Y + (box.Height / 2);

        // The event is dispatched ON the located element (ILocator.EvaluateAsync binds it as the first
        // argument), so there is no elementFromPoint lookup to get wrong. Values are formatted into the
        // script rather than passed as an argument object: Playwright's argument serialization did not
        // bind an anonymous type's members here, which surfaced as clientX arriving non-finite.
        static string Script(int n, double delta, double cx, double cy, bool ctrlKey)
        {
            var ci = CultureInfo.InvariantCulture;
            return $$"""
                (el) => {
                  for (let i = 0; i < {{n.ToString(ci)}}; i++) {
                    el.dispatchEvent(new WheelEvent('wheel', {
                      deltaY: {{delta.ToString("R", ci)}}, deltaMode: 0, ctrlKey: {{(ctrlKey ? "true" : "false")}},
                      clientX: {{cx.ToString("R", ci)}}, clientY: {{cy.ToString("R", ci)}},
                      bubbles: true, cancelable: true,
                    }));
                  }
                }
                """;
        }

        if (burst)
        {
            await target.EvaluateAsync(Script(events, deltaPerEvent, cx, cy, ctrlKey));
            return;
        }

        var one = Script(1, deltaPerEvent, cx, cy, ctrlKey);
        for (var i = 0; i < events; i++)
        {
            await target.EvaluateAsync(one);
            // One animation frame between events, so each one is painted rather than coalesced away.
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        }
    }
}
