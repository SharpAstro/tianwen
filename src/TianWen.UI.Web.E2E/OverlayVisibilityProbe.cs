using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Dumps what the sky map actually PAINTS with the [O] overlay off, on, and off again, so the
/// markers can be looked at rather than inferred from timers.
///
/// <para>Every existing overlay measurement reads <c>getRenderStats</c> -- gathers, uploads, per-phase
/// milliseconds. All of those stay identical whether the draw lands on the screen or is discarded by
/// the GPU, so none of them can see a marker that is submitted and never composited.</para>
///
/// <para>The third capture is the control: the sky map advances with real time, so two shots are never
/// byte-identical and a raw off-vs-on difference proves nothing on its own.</para>
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class OverlayVisibilityProbe(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 120_000;
    private static readonly Regex ActiveClass = new(@"\bactive\b");

    private static bool Enabled => Environment.GetEnvironmentVariable("TIANWEN_WEB_PROBE") == "1";

    private static async Task<JsonElement> StatsAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getRenderStats()");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task DumpOverlayFrames()
    {
        Assert.SkipUnless(Enabled, "set TIANWEN_WEB_PROBE=1 to run this measurement");

        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS")
            ?? Path.Combine(Path.GetTempPath(), "tianwen-overlay-shots");
        Directory.CreateDirectory(dir);

        var page = await fixture.WarmPageAsync();
        var console = new List<string>();
        page.Console += (_, m) => { lock (console) { console.Add($"[{m.Type}] {m.Text}"); } };
        page.PageError += (_, e) => { lock (console) { console.Add($"[pageerror] {e}"); } };

        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });
        var canvas = page.Locator("#planner");

        // Wait for the full Tycho-2 atlas to land. The user's session has it (the trace fetches
        // tyc2-cache.js); a probe that screenshots 8 seconds in has only the HR bright-star seed, so
        // it is measuring a DIFFERENT star pipeline state than the one being reported broken.
        var atlasDeadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < atlasDeadline)
        {
            lock (console)
            {
                if (console.Any(c => c.Contains("tyc2 flatten") || c.Contains("HR-only atlas")))
                {
                    break;
                }
            }
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
        lock (console)
        {
            output.WriteLine($"atlas: {(console.FirstOrDefault(c => c.Contains("tyc2 flatten")) ?? "NOT LOADED (HR seed only)")}");
        }

        async Task SettleAsync()
        {
            // Long enough for the debounced label repaint (120 ms) to have landed, so a capture shows
            // the settled frame rather than a mid-gesture one.
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
            await Task.Delay(400);
        }

        async Task<bool> OverlayOnAsync() => (await StatsAsync(page)).GetProperty("overlay").GetBoolean();

        async Task ShootAsync(string name)
        {
            await SettleAsync();
            var s = await StatsAsync(page);
            var path = Path.Combine(dir, name + ".png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = path });
            var bytes = new FileInfo(path).Length;
            var uploads = s.TryGetProperty("uploads", out var u) ? u.GetInt32() : -1;
            output.WriteLine($"{name,-12} overlay={s.GetProperty("overlay").GetBoolean(),-5} "
                + $"gathers={s.GetProperty("gathers").GetInt32(),-4} uploads={uploads,-4} "
                + $"markerMs={(s.TryGetProperty("markerMs", out var m) ? m.GetDouble() : 0),8:F1} "
                + $"{bytes,9} bytes  {path}");
        }

        if (await OverlayOnAsync())
        {
            await canvas.PressAsync("o");
        }

        await ShootAsync("1-overlay-off");
        await canvas.PressAsync("o");
        Assert.True(await OverlayOnAsync(), "[O] did not turn the overlay on");
        await ShootAsync("2-overlay-on");
        await canvas.PressAsync("o");
        await ShootAsync("3-overlay-off-again");

        lock (console)
        {
            output.WriteLine($"--- {console.Count} console messages ---");
            foreach (var m in console)
            {
                output.WriteLine("  " + m);
            }
        }
    }
}
