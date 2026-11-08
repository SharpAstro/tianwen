using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// The selected planet stays on its orbit in the browser (2026-11-08): with Saturn's info panel open, the orrery drew no Saturn on its
/// orbit, only the selection ring, and closing the panel brought it back. The panel draws the body's disc too, and the browser's planet
/// bodies kept ONE block a body for draws replayed at Present, so the panel's draw replaced the orrery's
/// (<c>PlanetBodyInstances</c>). Opened by a link (#2072) on Saturn with its panel, the pixels where the orrery draws Saturn are
/// captured with the panel open and again with it closed (Escape): they must be the same picture. With the defect the first is the
/// starry sky and the second the disc.
/// </summary>
/// <remarks>
/// <para>Why a comparison and not a brightness test: Saturn's disc is about ten pixels across over a dense star field, and the first
/// version of this test, which asked for a bright pixel there, passed with the defect in (a star behind the missing disc reached 255).
/// The stars are the same in both captures, so they cancel; only the disc can differ. Measured: a mean luminance difference of
/// about 38 with the defect in, the disc present in both captures with it fixed.</para>
/// <para><b>No sleeps.</b> It waits on the <c>?e2e=1</c> render stats: the planet atlas adopted, the orrery open,
/// <c>orreryFocusDisc</c> (the last frame drew the focused body's textured disc, not a flat dot, which would hide the defect),
/// <c>orreryPanelOpen</c>, and a later frame for the second capture.</para>
/// <code>
/// TIANWEN_E2E_CHANNEL=msedge dotnet test TianWen.UI.Web.E2E --filter FullyQualifiedName~OrrerySelectedDiscTests
/// </code>
/// </remarks>
[Collection(TianWenWebCollection.Name)]
public sealed class OrrerySelectedDiscTests(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 180_000;
    private const float OrreryTimeout = 300_000;

    // The mean absolute difference in LUMINANCE (Rec. 709) of two PNGs of the canvas inside the given radius of (cx, cy), and the
    // largest. Luminance, not colour: Saturn's map can be upgraded between the two captures (the OPAL map landing asynchronously turned
    // the disc from the atlas's beige to OPAL's grey in a run), which moves its colour and leaves its brightness; a missing disc moves both.
    private const string DiffScript = """
        async ([a64, b64, cx, cy, reach]) => {
            const read = async b64 => {
                const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
                const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
                const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
                const ctx = canvas.getContext('2d');
                ctx.drawImage(bitmap, 0, 0);
                return ctx.getImageData(0, 0, bitmap.width, bitmap.height);
            };
            const a = await read(a64), b = await read(b64);
            let n = 0, sum = 0, max = 0;
            for (let y = Math.floor(cy - reach); y <= Math.ceil(cy + reach); y++) {
                for (let x = Math.floor(cx - reach); x <= Math.ceil(cx + reach); x++) {
                    if (x < 0 || y < 0 || x >= a.width || y >= a.height || (x - cx) ** 2 + (y - cy) ** 2 > reach * reach) continue;
                    const i = (y * a.width + x) * 4;
                    const lum = d => 0.2126 * d[i] + 0.7152 * d[i + 1] + 0.0722 * d[i + 2];
                    const d = Math.abs(lum(a.data) - lum(b.data));
                    sum += d; max = Math.max(max, d);
                    n++;
                }
            }
            return JSON.stringify({ n, mean: sum / n, max });
        }
        """;

    private readonly ConcurrentQueue<string> _console = new();

    private async Task WaitAsync(IPage page, string condition, string failure, string dir)
    {
        try
        {
            await page.WaitForFunctionAsync(RenderStatsWait.Script(condition, "s.orreryFrames"), null,
                new PageWaitForFunctionOptions { Timeout = OrreryTimeout });
        }
        catch (TimeoutException)
        {
            var last = await page.EvaluateAsync<string?>("() => JSON.stringify(window.__tianwenStats ?? null)");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, "orrery-selected-timeout.png") });
            foreach (var line in _console)
            {
                output.WriteLine("  " + line);
            }
            throw new TimeoutException($"{failure}; the last render stats: {last}");
        }
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<(byte[] Png, double X, double Y, double R, int Frames)> CaptureAsync(IPage page, string dir, string name)
    {
        var stats = await page.EvaluateAsync<string>("() => JSON.stringify(window.__tianwenStats)");
        using var parsed = JsonDocument.Parse(stats);
        var drawn = parsed.RootElement.GetProperty("orreryFocusDrawn");
        var box = await page.Locator("#planner").BoundingBoxAsync() ?? throw new InvalidOperationException("the atlas canvas has no box");
        var path = Path.Combine(dir, name);
        var png = await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height },
        });
        output.WriteLine($"capture: {path}");
        return (png, drawn.GetProperty("x").GetDouble(), drawn.GetProperty("y").GetDouble(), drawn.GetProperty("r").GetDouble(),
            parsed.RootElement.GetProperty("orreryFrames").GetInt32());
    }

    [Fact(Timeout = 1_200_000)]
    public async Task TheSelectedPlanetIsDrawnOnItsOrbitWithItsPanelOpen()
    {
        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS")
            ?? Path.Combine(Path.GetTempPath(), "tianwen-orrery-selected-shots");
        Directory.CreateDirectory(dir);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var page = await fixture.NewPageAsync();
        var console = _console;
        page.Console += (_, m) => console.Enqueue($"[{m.Type}] {m.Text}");
        page.PageError += (_, e) => console.Enqueue($"[pageerror] {e}");

        // The scene of the report: Saturn selected and focused, its panel open, the camera where the screenshot had it.
        await page.GotoAsync(fixture.BaseUrl + "?e2e=1&view=sky&object=Saturn&orrery=1&focus=Saturn&az=-71.6&el=52.6&dist=23.897",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        output.WriteLine($"{clock.Elapsed.TotalSeconds:F0} s: the catalogue has loaded");

        await WaitAsync(page, "s.planetTextures > 0 && s.orreryOpen && s.orreryPanelOpen && s.orreryFocusDisc && s.orreryFocusDrawn",
            "no orrery frame traced Saturn's disc with its panel open", dir);
        var open = await CaptureAsync(page, dir, Environment.GetEnvironmentVariable("TIANWEN_SHOT_NAME") ?? "orrery-selected-open.png");
        output.WriteLine($"{clock.Elapsed.TotalSeconds:F0} s: Saturn drawn at ({open.X}, {open.Y}), radius {open.R} CSS px, panel open");

        // Escape closes the panel first and leaves the orrery open (SkyMapTab.EscapeOrrery); the key goes to the document.
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await page.Keyboard.PressAsync("Escape");
        await WaitAsync(page, $"s.orreryOpen && !s.orreryPanelOpen && s.orreryFocusDisc && s.orreryFrames > {open.Frames}",
            "the panel did not close on Escape, or no frame after it traced Saturn's disc", dir);
        var closed = await CaptureAsync(page, dir, "orrery-selected-closed.png");
        (closed.X, closed.Y).ShouldBe((open.X, open.Y), "Saturn moved between the captures, so they cannot be compared");

        var json = await page.EvaluateAsync<string>(DiffScript,
            new object[] { Convert.ToBase64String(open.Png), Convert.ToBase64String(closed.Png), open.X, open.Y, Math.Max(2, open.R * 0.8) });
        output.WriteLine($"Saturn's disc, panel open against closed: {json}");
        using var diff = JsonDocument.Parse(json);
        diff.RootElement.GetProperty("n").GetInt32().ShouldBeGreaterThan(0, "Saturn was drawn off the canvas");
        diff.RootElement.GetProperty("mean").GetDouble().ShouldBeLessThan(6,
            "Saturn's place on its orbit differs with its panel open: the panel's draw took the orrery's disc away");
    }
}
