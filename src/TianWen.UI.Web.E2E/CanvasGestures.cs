using Microsoft.Playwright;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Canvas gesture helpers for the WebGL sky atlas. The app is a single &lt;canvas&gt; with no DOM to
/// drive, and Playwright has no public multi-touch API, so a two-finger pinch is synthesized over the
/// Chrome DevTools Protocol (<c>Input.dispatchTouchEvent</c> with two <c>touchPoints</c>) — the
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
}
