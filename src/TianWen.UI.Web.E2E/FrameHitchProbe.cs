using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Measures the hitches a person sees on the atlas and the orrery, on whatever build <c>TIANWEN_WEB_BASEURL</c> points at: a
/// mouse pan across the sky, the orrery opening (Alt+S), and the orrery playing. Each phase reports the gaps between the
/// browser's animation frames (a gap is a frame the page could not draw on time) and Chromium's long animation frames
/// (<c>long-animation-frame</c>, every frame over 50 ms, with the script that ran in it).
/// </summary>
/// <remarks>
/// <para>Found by reading a DevTools trace of the deployed showcase by hand (2026-10-19): opening the orrery held the main
/// thread for two frames of about 150 ms, about 70 ms of it in <c>getProgramParameter</c>, a WebGL shader link the page waited
/// on, since every custom pipeline is registered on the frame that first draws it. The point of the probe is that the next one
/// does not need a trace.</para>
/// <para><b>Run it against an AOT build.</b> A dev server is interpreted, where a frame's own compute is 24 to 42 times slower and
/// buries the hitches this measures. <b>Headless Chromium may draw through SwiftShader</b>, whose shader compile is the CPU's: the
/// probe prints the renderer the page got, and <c>TIANWEN_E2E_HEADED=1</c> runs it on the real GPU.</para>
/// <para>Env-gated (<c>TIANWEN_WEB_PROBE=1</c>) like <see cref="AtlasLoadCostProbe"/>: an instrument, not an assertion.</para>
/// </remarks>
[Collection(TianWenWebCollection.Name)]
public sealed class FrameHitchProbe(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 180_000;
    private const float FrameTimeout = 60_000;

    // A pinned instant, so the sky and the orrery do not depend on the day the probe runs.
    private const string AtlasLink = "?e2e=1&view=sky&t=2026-10-13T22:00:00Z";

    // The recorder: every animation frame's timestamp and every long animation frame, from start() until stop(). Installed as
    // its own rAF loop, so a gap is the main thread being busy whether or not the app asked for that frame.
    private const string RecorderScript = """
        () => {
          if (window.__hitch) return;
          const h = window.__hitch = { on: false, run: 0, stamps: [], loafs: [] };
          // One loop a recording: a stop's last pending tick must not carry on into the next recording beside its own loop.
          const loop = run => { const tick = t => { if (h.on && h.run === run) { h.stamps.push(t); requestAnimationFrame(tick); } }; return tick; };
          if (PerformanceObserver.supportedEntryTypes?.includes('long-animation-frame')) {
            new PerformanceObserver(list => {
              if (!h.on) return;
              for (const e of list.getEntries()) h.loafs.push({
                start: e.startTime, duration: e.duration, blocking: e.blockingDuration, render: e.renderStart ? e.startTime + e.duration - e.renderStart : 0,
                scripts: e.scripts.map(s => ({ invoker: s.invoker, source: (s.sourceURL || '').split('/').pop(), fn: s.sourceFunctionName, duration: s.duration }))
              });
            }).observe({ type: 'long-animation-frame', buffered: false });
            h.loaf = true;
          }
          h.start = () => { h.stamps = []; h.loafs = []; window.__glCalls = {}; window.__consoleLines = []; h.on = true; requestAnimationFrame(loop(++h.run)); };
          h.stop = () => { h.on = false; return JSON.stringify({ stamps: h.stamps, loafs: h.loafs, loaf: !!h.loaf, gl: window.__glCalls, console: window.__consoleLines }); };
        }
        """;

    // Installed before the page loads: the WebGL 2 calls that can make the main thread wait (a shader's compile and link and
    // the status queries that wait for them, texture uploads, a readback) are timed, so a long frame can be charged to them
    // without a symbolised build: the count and total of each, since the last start(). And what the page logs (the atlas logs
    // each step of its star catalogue's load) is stamped on the same clock as the long frames, so the two read as one timeline.
    private const string GlTimerScript = """
        (() => {
          window.__glCalls = {};
          window.__consoleLines = [];
          for (const level of ['log', 'info', 'warn', 'error']) {
            const f = console[level];
            console[level] = function (...args) {
              window.__consoleLines.push({ t: performance.now(), text: args.map(String).join(' ').slice(0, 160) });
              return f.apply(this, args);
            };
          }
          const p = WebGL2RenderingContext.prototype;
          for (const name of ['compileShader', 'linkProgram', 'getShaderParameter', 'getProgramParameter', 'getUniformLocation',
                              'getUniformBlockIndex', 'texImage2D', 'texSubImage2D', 'texStorage2D', 'readPixels', 'bufferData', 'bufferSubData']) {
            const f = p[name];
            p[name] = function (...args) {
              const t = performance.now();
              try { return f.apply(this, args); }
              finally { const c = window.__glCalls[name] ??= { n: 0, ms: 0, max: 0 }; const d = performance.now() - t; c.n++; c.ms += d; c.max = Math.max(c.max, d); }
            };
          }
        })();
        """;

    private sealed record Loaf(double start, double duration, double blocking, double render, Script[] scripts);
    private sealed record Script(string invoker, string source, string fn, double duration);
    private sealed record GlCall(int n, double ms, double max);
    private sealed record ConsoleLine(double t, string text);
    private sealed record Recording(double[] stamps, Loaf[] loafs, bool loaf, Dictionary<string, GlCall> gl, ConsoleLine[] console);

    [Fact]
    public async Task MeasureAsync()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_WEB_PROBE") == "1",
            "probe: set TIANWEN_WEB_PROBE=1 (and point TIANWEN_WEB_BASEURL at an AOT build)");

        var page = await fixture.NewPageAsync();
        await page.SetViewportSizeAsync(1600, 1000);
        await page.AddInitScriptAsync(GlTimerScript);
        await page.GotoAsync(fixture.BaseUrl + AtlasLink, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        var canvas = page.Locator("#planner");
        await StatsWhenAsync(page, "s.frames > 0");
        await page.EvaluateAsync(RecorderScript);

        var renderer = await page.EvaluateAsync<string>("""
            () => { const gl = document.createElement('canvas').getContext('webgl2'); if (!gl) return 'no webgl2';
              const d = gl.getExtension('WEBGL_debug_renderer_info');
              return (d ? gl.getParameter(d.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER))
                + (gl.getExtension('KHR_parallel_shader_compile') ? ' | KHR_parallel_shader_compile' : ' | no parallel compile'); }
            """);
        output.WriteLine($"[hitch] {fixture.BaseUrl} | {renderer} | {await page.EvaluateAsync<string>("() => navigator.userAgent")}");

        // Let the boot's own work (the catalog's tail, the Milky Way, first textures) settle before anything is measured.
        await IdleFramesAsync(page, 120);

        var box = await canvas.BoundingBoxAsync() ?? throw new InvalidOperationException("the atlas canvas has no box");

        // 0. Nothing at all, for five seconds: what still runs in the background (the star atlas's load) and would otherwise be
        // charged to the pan.
        await page.EvaluateAsync("() => window.__hitch.start()");
        var before = await StatsAsync(page);
        await page.EvaluateAsync("() => new Promise(r => setTimeout(r, 5000))");
        Report("idle", await StopAsync(page), before, await StatsAsync(page));

        // 1. A pan: the mouse held down and moved one step per animation frame, as a hand does, for three sweeps.
        await page.EvaluateAsync("() => window.__hitch.start()");
        before = await StatsAsync(page);
        await page.Mouse.MoveAsync(box.X + (box.Width * 0.75f), box.Y + (box.Height * 0.5f));
        await page.Mouse.DownAsync();
        for (var sweep = 0; sweep < 3; sweep++)
        {
            for (var step = 0; step < 90; step++)
            {
                var f = step / 89f;
                var x = sweep % 2 == 0 ? 0.75f - (0.5f * f) : 0.25f + (0.5f * f);
                await page.Mouse.MoveAsync(box.X + (box.Width * x), box.Y + (box.Height * (0.5f + (0.15f * MathF.Sin(f * MathF.PI)))));
                await NextFrameAsync(page);
            }
        }

        await page.Mouse.UpAsync();
        await IdleFramesAsync(page, 10);
        Report("pan", await StopAsync(page), before, await StatsAsync(page));

        // 2. The orrery opening, from the key to its first frames.
        await IdleFramesAsync(page, 30);
        await page.EvaluateAsync("() => window.__hitch.start()");
        before = await StatsAsync(page);
        await page.Keyboard.PressAsync("Alt+s");
        await StatsWhenAsync(page, "s.orreryOpen && s.orreryFrames > 2");
        await IdleFramesAsync(page, 30);
        Report("orrery open", await StopAsync(page), before, await StatsAsync(page));

        // 3. Play, for five seconds of the page's own frames.
        var play = await PaintedAsync(page, "getButtonRect('OrreryPlay')", "playButton");
        await page.EvaluateAsync("() => window.__hitch.start()");
        before = await StatsAsync(page);
        await canvas.ClickAsync(new LocatorClickOptions { Position = new Position { X = (float)(play.x + (play.w / 2)), Y = (float)(play.y + (play.h / 2)) } });
        await StatsWhenAsync(page, "s.orreryPlaying");
        await page.EvaluateAsync("() => new Promise(r => setTimeout(r, 5000))");
        Report("orrery play", await StopAsync(page), before, await StatsAsync(page));
        await canvas.ClickAsync(new LocatorClickOptions { Position = new Position { X = (float)(play.x + (play.w / 2)), Y = (float)(play.y + (play.h / 2)) } });

        await page.Context.CloseAsync();
    }

    private void Report(string phase, Recording r, JsonElement before, JsonElement after)
    {
        var gaps = new double[Math.Max(0, r.stamps.Length - 1)];
        for (var i = 1; i < r.stamps.Length; i++) gaps[i - 1] = r.stamps[i] - r.stamps[i - 1];
        Array.Sort(gaps);
        // The display's period is the frames' own shortest common gap; a gap past 1.5 periods is at least one frame missed.
        var vsync = gaps.Length > 0 ? gaps[gaps.Length / 4] : double.NaN;
        var missed = gaps.Where(g => g > vsync * 1.5).ToArray();
        var span = r.stamps.Length > 1 ? r.stamps[^1] - r.stamps[0] : 0;
        string P(double q) => gaps.Length == 0 ? "-" : gaps[Math.Min(gaps.Length - 1, (int)(gaps.Length * q))].ToString("F1", CultureInfo.InvariantCulture);
        var frames = after.GetProperty("frames").GetInt32() - before.GetProperty("frames").GetInt32();
        var renderMs = after.GetProperty("renderMs").GetDouble() - before.GetProperty("renderMs").GetDouble();

        output.WriteLine($"[hitch] {phase}: {span / 1000:F1} s, {r.stamps.Length} animation frames, period {vsync:F1} ms; gap p50 {P(0.5)} p90 {P(0.9)} p99 {P(0.99)} max {(gaps.Length > 0 ? gaps[^1] : 0):F1} ms");
        output.WriteLine($"[hitch] {phase}: {missed.Length} gaps over 1.5 periods, {missed.Sum(g => Math.Round(g / vsync) - 1):F0} frames missed; the app painted {frames} frames, {(frames > 0 ? renderMs / frames : 0):F2} ms each");
        var gl = r.gl.Where(c => c.Value.ms >= 1).OrderByDescending(c => c.Value.ms)
            .Select(c => $"{c.Key} {c.Value.n}x {c.Value.ms:F0} ms (max {c.Value.max:F0})").ToArray();
        output.WriteLine($"[hitch] {phase}: WebGL calls over 1 ms in all: {(gl.Length == 0 ? "none" : string.Join(", ", gl))}");
        if (!r.loaf)
        {
            output.WriteLine($"[hitch] {phase}: this browser reports no long animation frames");
            return;
        }

        output.WriteLine($"[hitch] {phase}: {r.loafs.Length} long animation frames, {r.loafs.Sum(l => l.duration):F0} ms in all, {r.loafs.Sum(l => l.blocking):F0} ms blocking");
        // The longest frames and what the page logged, on one clock (seconds from the recording's first frame).
        var origin = r.stamps.FirstOrDefault();
        var timeline = r.loafs.OrderByDescending(l => l.duration).Take(8).Select(l => (l.start, Line: $"{l.duration:F0} ms frame (blocking {l.blocking:F0}, render {l.render:F0}) "
                + string.Join("; ", l.scripts.OrderByDescending(s => s.duration).Take(3)
                    .Select(s => $"{s.invoker} {s.source}{(string.IsNullOrEmpty(s.fn) ? "" : ":" + s.fn)} {s.duration:F0} ms"))))
            .Concat(r.console.Select(c => (start: c.t, Line: "log: " + c.text)))
            .OrderBy(e => e.start);
        foreach (var (start, line) in timeline)
        {
            output.WriteLine($"[hitch]   {(start - origin) / 1000,6:F2} s  {line}");
        }
    }

    private static async Task<Recording> StopAsync(IPage page)
        => JsonSerializer.Deserialize<Recording>(await page.EvaluateAsync<string>("() => window.__hitch.stop()"))
            ?? throw new InvalidOperationException("the recorder answered nothing");

    private static Task NextFrameAsync(IPage page) => page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");

    private static async Task IdleFramesAsync(IPage page, int frames)
    {
        for (var i = 0; i < frames; i++) await NextFrameAsync(page);
    }

    private static async Task<JsonElement> StatsAsync(IPage page)
        => JsonDocument.Parse(await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getRenderStats()")).RootElement;

    private static async Task<JsonElement> StatsWhenAsync(IPage page, string condition)
    {
        var handle = await page.WaitForFunctionAsync(RenderStatsWait.Script(condition, "JSON.stringify(s)"), null,
            new PageWaitForFunctionOptions { Timeout = FrameTimeout });
        return JsonDocument.Parse(await handle.JsonValueAsync<string>()).RootElement;
    }

    private sealed record Rect(double x, double y, double w, double h);

    private static async Task<Rect> PaintedAsync(IPage page, string call, string slot)
    {
        var script = "() => { const t = window.__tianwenTest; if (!t) return false; "
            + $"if (!window.__{slot}InFlight) {{ window.__{slot}InFlight = true; "
            + $"t.{call}.then(j => {{ window.__{slot} = j; }}).finally(() => {{ window.__{slot}InFlight = false; }}); }} "
            + $"const r = window.__{slot}; return r && r !== 'null' ? r : false; }}";
        var handle = await page.WaitForFunctionAsync(script, null, new PageWaitForFunctionOptions { Timeout = FrameTimeout });
        return JsonSerializer.Deserialize<Rect>(await handle.JsonValueAsync<string>())
            ?? throw new InvalidOperationException($"{call} answered no rect");
    }
}
