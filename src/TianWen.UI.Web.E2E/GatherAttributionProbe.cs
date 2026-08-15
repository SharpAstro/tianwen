using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// On-demand measurement, not an assertion: attributes the browser's per-frame render cost during a
/// zoom to the object-overlay candidate gather. Gated on <c>TIANWEN_WEB_PROBE=1</c> so a normal
/// <c>dotnet test</c> skips it, mirroring the env-gated probes elsewhere in this repo
/// (<c>PhotometricRepeatabilityProbe</c>, <c>VelaMosaicStarListExport</c>).
///
/// <para><b>Why this cannot be read off a Chrome trace.</b> The browser runs the whole paint inside
/// the animation-frame callback, so a trace samples "the frame was slow" and "the gather inside it was
/// slow" as the same event. A trace of the deployed build showed 62 of 1899 rAF callbacks (3.3%)
/// carrying 54% of all render time, 61 of them during a gesture, which says WHEN but not WHAT. The
/// app-side timers (<c>renderMs</c> / <c>gatherMs</c>) separate them.</para>
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class GatherAttributionProbe(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 120_000;
    private static readonly Regex ActiveClass = new(@"\bactive\b");

    private static bool Enabled => Environment.GetEnvironmentVariable("TIANWEN_WEB_PROBE") == "1";

    private readonly record struct Stats(int Frames, int Gathers, double RenderMs, double GatherMs);

    private static async Task<Stats> ReadAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getRenderStats()");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new Stats(
            r.GetProperty("frames").GetInt32(),
            r.GetProperty("gathers").GetInt32(),
            r.GetProperty("renderMs").GetDouble(),
            r.GetProperty("gatherMs").GetDouble());
    }

    [Fact]
    public async Task AttributeZoomFrameCostToTheOverlayGather()
    {
        Assert.SkipUnless(Enabled, "set TIANWEN_WEB_PROBE=1 to run this measurement");

        var page = await fixture.WarmPageAsync();
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });
        var canvas = page.Locator("#planner");

        // The gather only runs with the [O] catalog overlay on, and it is off by default.
        for (var i = 0; i < 4 && !(await ReadAsync(page)).Gathers.Equals(int.MinValue); i++)
        {
            var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getRenderStats()");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("overlay").GetBoolean()) break;
            await canvas.PressAsync("o");
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        }

        const int steps = 26;

        // Run the SAME zoom twice, with the [O] overlay on and off. The difference isolates the
        // overlay's own per-frame cost (drawing every candidate as traced ellipses + labels with CPU
        // primitives, which is what the desktop does with an instanced GPU pipeline instead) from
        // everything else the frame does.
        async Task<List<Stats>> SweepAsync()
        {
            var acc = new List<Stats>(steps);
            var p0 = await ReadAsync(page);
            for (var i = 0; i < steps; i++)
            {
                await CanvasGestures.TrackpadPinchAsync(page, canvas, events: 1, deltaPerEvent: -1.5, burst: false);
                var c = await ReadAsync(page);
                acc.Add(new Stats(c.Frames - p0.Frames, c.Gathers - p0.Gathers,
                    c.RenderMs - p0.RenderMs, c.GatherMs - p0.GatherMs));
                p0 = c;
            }
            return acc;
        }

        var rows = await SweepAsync();

        // Overlay OFF, zooming back out over the same range.
        await canvas.PressAsync("o");
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        var offRows = new List<Stats>(steps);
        {
            var p0 = await ReadAsync(page);
            for (var i = 0; i < steps; i++)
            {
                await CanvasGestures.TrackpadPinchAsync(page, canvas, events: 1, deltaPerEvent: +1.5, burst: false);
                var c = await ReadAsync(page);
                offRows.Add(new Stats(c.Frames - p0.Frames, c.Gathers - p0.Gathers,
                    c.RenderMs - p0.RenderMs, c.GatherMs - p0.GatherMs));
                p0 = c;
            }
        }
        var offFrames = Math.Max(offRows.Sum(r => r.Frames), 1);
        output.WriteLine($"OVERLAY OFF: {offRows.Sum(r => r.RenderMs):F1} ms over {offFrames} frames "
            + $"({offRows.Sum(r => r.RenderMs) / offFrames:F1} ms/frame), {offRows.Sum(r => r.Gathers)} gathers");

        // Overlay back ON, and zoom OUT past the wide-FOV threshold. Beyond 90 degrees the gather
        // abandons the sampled bounds and sweeps the FULL sphere (minRA 0, maxRA 24, dec -90..90),
        // which is 65,160 grid cells against a few thousand for a narrow field. This is the case that
        // matches the 231 ms animation-frame callbacks seen in a real session, and the narrow-field
        // sweep above does not reach it.
        await canvas.PressAsync("o");
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        var wide = new List<Stats>(40);
        {
            var p0 = await ReadAsync(page);
            for (var i = 0; i < 40; i++)
            {
                await CanvasGestures.TrackpadPinchAsync(page, canvas, events: 1, deltaPerEvent: +8.0, burst: false);
                var c = await ReadAsync(page);
                wide.Add(new Stats(c.Frames - p0.Frames, c.Gathers - p0.Gathers,
                    c.RenderMs - p0.RenderMs, c.GatherMs - p0.GatherMs));
                p0 = c;
            }
        }
        var wg = wide.Sum(r => r.Gathers);
        var fovJson = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getSkyView()");
        using (var fd = JsonDocument.Parse(fovJson))
        {
            output.WriteLine($"ZOOM OUT to fov={fd.RootElement.GetProperty("fovDeg").GetDouble():F1} deg, overlay ON: "
                + $"{wg} gathers, {wide.Sum(r => r.GatherMs):F1} ms gathering "
                + $"({(wg > 0 ? wide.Sum(r => r.GatherMs) / wg : 0):F1} ms per gather), "
                + $"worst single step {wide.Max(r => r.RenderMs):F1} ms");
        }
        output.WriteLine("");

        output.WriteLine($"{"step",4} {"frames",7} {"gathers",8} {"renderMs",9} {"gatherMs",9}");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            output.WriteLine($"{i,4} {r.Frames,7} {r.Gathers,8} {r.RenderMs,9:F1} {r.GatherMs,9:F1}");
        }

        var totalRender = rows.Sum(r => r.RenderMs);
        var totalGather = rows.Sum(r => r.GatherMs);
        var totalFrames = rows.Sum(r => r.Frames);
        var totalGathers = rows.Sum(r => r.Gathers);
        output.WriteLine("");
        output.WriteLine($"totals: {totalFrames} frames, {totalGathers} gathers, "
            + $"{totalRender:F1} ms rendering, {totalGather:F1} ms gathering");
        output.WriteLine($"the gather is {(totalRender > 0 ? 100.0 * totalGather / totalRender : 0):F0}% of render time");

        var withG = rows.Where(r => r.Gathers > 0).ToList();
        var without = rows.Where(r => r.Gathers == 0).ToList();
        foreach (var (name, set) in new[] { ("WITH a gather", withG), ("WITHOUT a gather", without) })
        {
            if (set.Count == 0) continue;
            var f = Math.Max(set.Sum(r => r.Frames), 1);
            output.WriteLine($"steps {name,-17}: {set.Count,3} steps, {set.Sum(r => r.RenderMs),8:F1} ms "
                + $"over {f} frames ({set.Sum(r => r.RenderMs) / f:F1} ms/frame)");
        }
    }
}
