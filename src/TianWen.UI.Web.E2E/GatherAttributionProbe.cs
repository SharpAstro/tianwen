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

    private readonly record struct Stats(
        int Frames, int Gathers, double RenderMs, double GatherMs, int Uploads,
        double ProjectMs, double MarkerMs, double LabelMs);

    private static async Task<Stats> ReadAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getRenderStats()");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new Stats(
            r.GetProperty("frames").GetInt32(),
            r.GetProperty("gathers").GetInt32(),
            r.GetProperty("renderMs").GetDouble(),
            r.GetProperty("gatherMs").GetDouble(),
            // Absent on any build predating the instanced overlay; -1 is the "old artifact" signal
            // RequireInstancedOverlayAsync turns into a failure.
            r.TryGetProperty("uploads", out var u) ? u.GetInt32() : -1,
            Phase(r, "projectMs"), Phase(r, "markerMs"), Phase(r, "labelMs"));
    }

    /// <summary>A per-phase timer, or 0 on a build that predates it (so an old artifact still reads).</summary>
    private static double Phase(JsonElement r, string name)
        => r.TryGetProperty(name, out var v) ? v.GetDouble() : 0.0;

    private static Stats Sub(Stats a, Stats b)
    {
        return new Stats(a.Frames - b.Frames, a.Gathers - b.Gathers, a.RenderMs - b.RenderMs,
            a.GatherMs - b.GatherMs, a.Uploads - b.Uploads,
            a.ProjectMs - b.ProjectMs, a.MarkerMs - b.MarkerMs, a.LabelMs - b.LabelMs);
    }

    /// <summary>
    /// Fails unless the app under test actually contains the instanced overlay.
    ///
    /// <para><b>This is the guard the deployed-site measurements needed and did not have.</b> A local
    /// dev server served a STALE build for an entire measurement session: the build was green, the
    /// timestamps were fresh, the page rendered, the tests passed -- and the numbers came from code
    /// that predated the change. It was caught only because the "fixed" and "unfixed" runs agreed
    /// exactly, which they cannot. The same trap is worse against a deployed site, where the artifact
    /// is whatever the last successful Pages deploy published and a merge is not a deploy.</para>
    ///
    /// <para>The check is a capability the old build cannot fake: <c>uploads</c> only exists in the
    /// render-stats payload once the instanced path is present.</para>
    /// </summary>
    private static async Task RequireInstancedOverlayAsync(IPage page, string baseUrl)
    {
        var stats = await ReadAsync(page);
        Assert.SkipWhen(stats.Uploads < 0,
            $"{baseUrl} is serving a build with no instanced overlay (no 'uploads' in getRenderStats) -- "
            + "measuring it would attribute the OLD renderer's cost to the new one");
    }

    /// <summary>Current sky-map field of view, so a per-step cost can be placed on the zoom axis.</summary>
    private static async Task<double> FovAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getSkyView()");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("fovDeg").GetDouble();
    }

    /// <summary>
    /// Zooms back to <paramref name="targetFov"/> before a sweep starts.
    ///
    /// <para><b>Sweeps do not start where the previous one started.</b> Each leaves the zoom wherever
    /// it ended -- the second sweep here bottoms out around 0.5 degrees -- so a following zoom-out
    /// spends nearly all its steps BELOW the 90 degree wide threshold, where a gather per 10% FOV
    /// bucket is correct behaviour. Read without this, the third sweep reported 30 gathers and they
    /// were attributed to its [D] toggle; measured from a common start the real figure is 6 against 3.
    /// A leg that silently measures a different field of view than the one it is compared against is
    /// worse than no measurement, because it looks like one.</para>
    /// </summary>
    private static async Task ResetFovAsync(IPage page, ILocator canvas, double targetFov)
    {
        for (var i = 0; i < 60; i++)
        {
            var fov = await FovAsync(page);
            if (Math.Abs(fov - targetFov) / targetFov < 0.08)
            {
                return;
            }
            await CanvasGestures.WheelZoomAsync(page, canvas, events: 1,
                deltaPerEvent: fov < targetFov ? +120.0 : -120.0, burst: false);
        }
        Assert.Fail($"could not return the sky map to {targetFov} deg; the sweeps would not be comparable");
    }

    /// <summary>Presses a sky-map toggle key and lets the resulting repaint land.</summary>
    private static async Task ToggleAsync(IPage page, ILocator canvas, string key)
    {
        await canvas.PressAsync(key);
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
    }

    /// <summary>
    /// The follow-up measurement, and the one aimed at what is LEFT. A trace of the deployed build
    /// after the gather fixes split cleanly by gesture: 805 pan frames averaged 2.22 ms (the
    /// centre-quantized cache holding), while 57 frames following a wheel averaged 17.93 ms and carried
    /// 36.4% of all animation-frame time, p95 124 ms and worst 161 ms. Twenty wheel events cost more
    /// than eight hundred pointermoves, so the remaining cost is on the ZOOM axis specifically.
    ///
    /// <para>This drives plain wheel zooms and reports, per step, the field of view, whether a gather
    /// ran, and the render/gather split -- which separates the two candidate explanations that a trace
    /// cannot: a gather still re-running per 10% FOV bucket below the wide threshold, or a per-frame
    /// cost that scales with zoom and was never cached at all (the overlay DRAW, which re-projects and
    /// re-strokes every candidate each frame).</para>
    ///
    /// <para>Run it against the DEPLOYED artifact to reproduce a real trace, by pointing
    /// <c>TIANWEN_WEB_BASEURL</c> at the published site. Interpreted WASM does not preserve ratios
    /// between code paths (measured 29x inflation on primitive drawing against 8x on the catalog walk),
    /// so a dev-server run can attribute this to the wrong half.</para>
    /// </summary>
    [Fact]
    public async Task AttributeWheelZoomFrameCost()
    {
        Assert.SkipUnless(Enabled, "set TIANWEN_WEB_PROBE=1 to run this measurement");

        var page = await fixture.WarmPageAsync();
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });
        var canvas = page.Locator("#planner");

        // The gather only runs with the [O] catalog overlay on, and it is off by default.
        var stats = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getRenderStats()");
        using (var doc = JsonDocument.Parse(stats))
        {
            if (!doc.RootElement.GetProperty("overlay").GetBoolean())
            {
                await ToggleAsync(page, canvas, "o");
            }
        }

        // [D] is the one thing that keeps the field of view in the wide cache key, so the same sweep is
        // run with dark nebulae off and on. If the residual cost is the gather, the two differ sharply
        // past 90 degrees; if it is the per-frame draw, they do not.
        async Task SweepAsync(string label, int steps, double deltaPerEvent)
        {
            // Every sweep starts from the app's default field of view, so their gather counts are
            // comparable with each other (see ResetFovAsync).
            await ResetFovAsync(page, canvas, targetFov: 60.0);
            output.WriteLine($"=== {label} ===");
            output.WriteLine($"{"step",4} {"fov",8} {"frames",7} {"gathers",8} {"renderMs",9} {"gatherMs",9}");
            var rows = new List<(double Fov, Stats S)>(steps);
            var p0 = await ReadAsync(page);
            for (var i = 0; i < steps; i++)
            {
                await CanvasGestures.WheelZoomAsync(page, canvas, events: 1, deltaPerEvent, burst: false);
                var c = await ReadAsync(page);
                var d = Sub(c, p0);
                p0 = c;
                var fov = await FovAsync(page);
                rows.Add((fov, d));
                output.WriteLine($"{i,4} {fov,8:F1} {d.Frames,7} {d.Gathers,8} {d.RenderMs,9:F1} {d.GatherMs,9:F1}");
            }

            var render = rows.Sum(r => r.S.RenderMs);
            var gather = rows.Sum(r => r.S.GatherMs);
            var gathers = rows.Sum(r => r.S.Gathers);
            output.WriteLine($"  totals: {gathers} gathers, {render:F1} ms rendering, {gather:F1} ms gathering "
                + $"({(render > 0 ? 100.0 * gather / render : 0):F0}% of render time)");
            var withG = rows.Where(r => r.S.Gathers > 0).ToList();
            var noG = rows.Where(r => r.S.Gathers == 0).ToList();
            foreach (var (name, set) in new[] { ("WITH a gather", withG), ("WITHOUT a gather", noG) })
            {
                if (set.Count == 0) continue;
                output.WriteLine($"  steps {name,-17}: {set.Count,3} steps, "
                    + $"{set.Sum(r => r.S.RenderMs) / set.Count:8.1f} ms mean render, "
                    + $"worst {set.Max(r => r.S.RenderMs):.1f} ms");
            }
            output.WriteLine("");
        }

        await SweepAsync("zoom OUT, dark nebulae OFF", steps: 30, deltaPerEvent: +120.0);
        await SweepAsync("zoom IN, dark nebulae OFF", steps: 30, deltaPerEvent: -120.0);
        await ToggleAsync(page, canvas, "d");
        await SweepAsync("zoom OUT, dark nebulae ON", steps: 30, deltaPerEvent: +120.0);
        await ToggleAsync(page, canvas, "d");
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
                    c.RenderMs - p0.RenderMs, c.GatherMs - p0.GatherMs, c.Uploads - p0.Uploads,
                    c.ProjectMs - p0.ProjectMs, c.MarkerMs - p0.MarkerMs, c.LabelMs - p0.LabelMs));
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
                    c.RenderMs - p0.RenderMs, c.GatherMs - p0.GatherMs, c.Uploads - p0.Uploads,
                    c.ProjectMs - p0.ProjectMs, c.MarkerMs - p0.MarkerMs, c.LabelMs - p0.LabelMs));
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
                    c.RenderMs - p0.RenderMs, c.GatherMs - p0.GatherMs, c.Uploads - p0.Uploads,
                    c.ProjectMs - p0.ProjectMs, c.MarkerMs - p0.MarkerMs, c.LabelMs - p0.LabelMs));
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

    /// <summary>
    /// The wide-FOV overlay cost on ONE artifact, as a table on the zoom axis. Point
    /// <c>TIANWEN_WEB_BASEURL</c> at the published site to measure what users actually run:
    ///
    /// <code>
    /// TIANWEN_WEB_BASEURL=https://sharpastro.github.io/tianwen/ TIANWEN_WEB_PROBE=1     ///   dotnet test TianWen.UI.Web.E2E --filter FullyQualifiedName~WideOverlayZoomCost
    /// </code>
    ///
    /// <para><b>Why the deployed site and not a dev server.</b> The published build is mono AOT; a dev
    /// server is interpreted, and interpreted WASM does not preserve ratios BETWEEN code paths --
    /// measured 29x inflation on primitive drawing against 8x on the catalog walk. So a dev-server A/B
    /// of "CPU polylines against an instanced draw" reports an upper bound on the win (it measured
    /// 842 ms against 246 ms per wide frame), and only the deployed artifact says what the change is
    /// worth in practice. It is a MEASUREMENT, not an assertion: there is no threshold here, because a
    /// number that varies with the runner's GPU cannot be a gate.</para>
    ///
    /// <para>Each run starts from a fresh page, so the field of view begins at the app default and the
    /// legs are comparable; see <see cref="ResetFovAsync"/> for what happens when they are not.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WideOverlayZoomCost(bool showDark)
    {
        Assert.SkipUnless(Enabled, "set TIANWEN_WEB_PROBE=1 to run this measurement");

        // A FRESH page, not the shared warm one: this needs the app's default field of view as the
        // starting point, and the warm page carries whatever the previous test left.
        var page = await fixture.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + "?e2e=1", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=planner]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });

        var canvas = page.Locator("#planner");
        await RequireInstancedOverlayAsync(page, fixture.BaseUrl);

        // The overlay is OFF by default and the whole path is skipped when it is, so without this the
        // sweep would measure an empty overlay and report a flattering number for nothing.
        await canvas.ClickAsync();
        await ToggleAsync(page, canvas, "o");
        if (showDark)
        {
            await ToggleAsync(page, canvas, "d");
        }

        output.WriteLine($"=== {fixture.BaseUrl}  [O] on, [D] {(showDark ? "on" : "off")} ===");
        output.WriteLine($"{"step",4} {"fov",8} {"gathers",8} {"uploads",8} {"renderMs",9} {"gatherMs",9}");

        var rows = new List<(double Fov, Stats S)>();
        var prev = await ReadAsync(page);
        var startFov = await FovAsync(page);
        for (var i = 0; i < 30; i++)
        {
            var fovBefore = await FovAsync(page);
            await CanvasGestures.WheelZoomAsync(page, canvas, events: 1, deltaPerEvent: +120.0, burst: false);
            var cur = await ReadAsync(page);
            var d = Sub(cur, prev);
            prev = cur;
            rows.Add((fovBefore, d));
            output.WriteLine($"{i,4} {fovBefore,8:F1} {d.Gathers,8} {d.Uploads,8} {d.RenderMs,9:F1} {d.GatherMs,9:F1}");
        }

        // Split on the wide threshold: below it a re-gather per FOV bucket is correct, so only the
        // steps above it say what the cached wide path costs per frame.
        var wide = rows.Where(r => r.Fov >= 90.0).ToList();
        var narrow = rows.Where(r => r.Fov < 90.0).ToList();
        output.WriteLine($"  start fov {startFov:F1}, {rows.Sum(r => r.S.Gathers)} gathers, "
            + $"{rows.Sum(r => r.S.Uploads)} uploads, {rows.Sum(r => r.S.RenderMs):F0} ms rendering, "
            + $"{rows.Sum(r => r.S.GatherMs):F0} ms gathering");
        foreach (var (name, set) in new[] { ("fov >= 90", wide), ("fov <  90", narrow) })
        {
            if (set.Count == 0) continue;
            var noGather = set.Where(r => r.S.Gathers == 0).ToList();
            output.WriteLine($"  {name}: {set.Count,3} steps, {set.Average(r => r.S.RenderMs),7:F1} ms mean, "
                + $"worst {set.Max(r => r.S.RenderMs):F1} ms"
                + (noGather.Count > 0
                    ? $"  |  {noGather.Count} without a gather: {noGather.Average(r => r.S.RenderMs):F1} ms mean"
                    : ""));
        }
    }

    /// <summary>
    /// The cost of a DENSE zoom, which is the gesture the coarse sweep above cannot measure: with a
    /// large per-event delta the field of view clamps at 180 within a few steps, so 24 of 30 steps
    /// move nothing and cross no FOV bucket. A real wheel or trackpad delivers ~1.5 units per event,
    /// crossing a bucket every few frames -- and a gather is 14-55 ms in the browser, so that is a
    /// dropped frame per bucket for the length of the gesture.
    ///
    /// <para>Both routes are driven because they are different code paths: a trackpad two-finger
    /// pinch arrives as ctrl+wheel through the Blazor wheel binding, a mouse wheel as a plain one.
    /// The overlay is also driven ON and off, because the difference between them IS the overlay's
    /// share -- the base frame (star field, lines, chrome) is over half the 60 fps budget on its own,
    /// so a total with no baseline beside it cannot say what the overlay costs.</para>
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task DenseZoomCost(bool ctrlPinch, bool overlayOn)
    {
        Assert.SkipUnless(Enabled, "set TIANWEN_WEB_PROBE=1 to run this measurement");

        var page = await fixture.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + "?e2e=1", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=planner]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });

        var canvas = page.Locator("#planner");
        await canvas.ClickAsync();
        if (overlayOn)
        {
            await ToggleAsync(page, canvas, "o");
        }

        const int events = 40;
        const double delta = -1.5;
        var before = await ReadAsync(page);
        var fov0 = await FovAsync(page);
        if (ctrlPinch)
        {
            await CanvasGestures.TrackpadPinchAsync(page, canvas, events, delta, burst: false);
        }
        else
        {
            await CanvasGestures.WheelZoomAsync(page, canvas, events, delta, burst: false);
        }
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        var after = await ReadAsync(page);
        var fov1 = await FovAsync(page);

        var frames = after.Frames - before.Frames;
        var render = after.RenderMs - before.RenderMs;
        output.WriteLine($"=== {(ctrlPinch ? "trackpad pinch (ctrl+wheel)" : "plain wheel")}, "
            + $"[O] {(overlayOn ? "ON" : "off")}: {events} events, fov {fov0:F1} -> {fov1:F1} ===");
        var d = Sub(after, before);
        output.WriteLine($"  {d.Gathers} gathers, {d.Uploads} uploads, {frames} frames, "
            + $"{render:F0} ms render ({render / Math.Max(1, frames):F1} ms/frame)");
        output.WriteLine($"  phases: gather {d.GatherMs:F0} | project {d.ProjectMs:F0} | markers {d.MarkerMs:F0} "
            + $"| labels {d.LabelMs:F0} ms  (overlay {d.GatherMs + d.ProjectMs + d.MarkerMs + d.LabelMs:F0} of "
            + $"{render:F0} ms = {100 * (d.GatherMs + d.ProjectMs + d.MarkerMs + d.LabelMs) / Math.Max(1, render):F0}%)");
    }
}
