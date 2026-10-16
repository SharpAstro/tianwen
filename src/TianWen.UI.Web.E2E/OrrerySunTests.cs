using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// The orrery's Sun in a real browser (#1608, docs/plans/atlas-planet-detail.md, A5): opened with Alt+S from the sky atlas, the Sun is
/// ray-traced by WebGL from its atlas map, the generated quiet photosphere, near white and limb-darkened, never the flat yellow it was.
/// The canvas is captured as a PNG and read back: the largest connected near-white blob is the Sun's filled disc.
/// </summary>
/// <remarks>
/// <para><b>No sleeps.</b> It waits on the <c>?e2e=1</c> render stats: the orrery open and a frame that traced the Sun from its map
/// (<c>sunTextured</c>, counted only when the map is in hand, which uploads asynchronously), so the capture is of a frame that drew
/// it. The key goes to the DOCUMENT, as <see cref="PictureMarkProbe"/>'s does.</para>
/// <para>The capture is decoded IN THE PAGE (an <c>ImageBitmap</c> drawn on a 2D canvas): the project carries no PNG decoder, and a
/// WebGL canvas cannot be read back once composited.</para>
/// <code>
/// TIANWEN_E2E_CHANNEL=msedge dotnet test TianWen.UI.Web.E2E --filter FullyQualifiedName~OrrerySunTests
/// </code>
/// </remarks>
[Collection(TianWenWebCollection.Name)]
public sealed class OrrerySunTests(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 180_000;
    // Generous: an interpreted Lightweight build draws a frame in about 17 s on this machine, and the atlas's decode is .NET there too.
    private const float OrreryTimeout = 240_000;

    // The atlas is decoded in .NET on the page's one thread, ten cells with their holes filled: minutes in an interpreted build.
    private const float AtlasTimeout = 900_000;

    // The largest 4-connected component of near-white pixels (each channel above 170, red no more than 45 over blue: the flat Sun's
    // blue, 0x80, never passes), its size, bounding box and centre colour, and the mean green of its outermost ring against its centre.
    private const string SunBlobScript = """
        async (b64) => {
            const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
            const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
            const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
            const ctx = canvas.getContext('2d');
            ctx.drawImage(bitmap, 0, 0);
            const { data, width, height } = ctx.getImageData(0, 0, bitmap.width, bitmap.height);
            const white = i => data[i] > 170 && data[i + 1] > 170 && data[i + 2] > 170 && data[i] - data[i + 2] <= 45;
            const seen = new Uint8Array(width * height);
            let best = null;
            for (let p = 0; p < width * height; p++) {
                if (seen[p] || !white(p * 4)) continue;
                const stack = [p]; seen[p] = 1;
                let size = 0, x0 = width, y0 = height, x1 = 0, y1 = 0, sx = 0, sy = 0;
                while (stack.length) {
                    const q = stack.pop(); const x = q % width, y = (q / width) | 0;
                    size++; sx += x; sy += y;
                    x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y);
                    for (const n of [q - 1, q + 1, q - width, q + width]) {
                        if (n < 0 || n >= width * height || seen[n]) continue;
                        if (Math.abs((n % width) - x) > 1) continue;
                        if (white(n * 4)) { seen[n] = 1; stack.push(n); }
                    }
                }
                if (!best || size > best.size) best = { size, x0, y0, x1, y1, cx: Math.round(sx / size), cy: Math.round(sy / size) };
            }
            if (!best) return JSON.stringify({ size: 0 });
            const at = (x, y) => { const i = (y * width + x) * 4; return [data[i], data[i + 1], data[i + 2]]; };
            best.centre = at(best.cx, best.cy);
            return JSON.stringify(best);
        }
        """;

    private readonly ConcurrentQueue<string> _console = new();

    // Waits on the render stats; on a timeout, fails naming what did not happen and the last snapshot the page answered.
    private async Task<int> WaitAsync(IPage page, string condition, string failure, float timeout = OrreryTimeout)
    {
        try
        {
            await page.WaitForFunctionAsync(RenderStatsWait.Script(condition, "s.orreryFrames + s.sunTextured + 1"),
                null, new PageWaitForFunctionOptions { Timeout = timeout });
            return await page.EvaluateAsync<int>("() => window.__tianwenStats.sunTextured");
        }
        catch (TimeoutException)
        {
            var last = await page.EvaluateAsync<string?>("() => JSON.stringify(window.__tianwenStats ?? null)");
            var shot = Path.Combine(Path.GetTempPath(), "tianwen-orrery-sun-shots", "orrery-timeout.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = shot });
            foreach (var line in _console)
            {
                output.WriteLine("  " + line);
            }
            output.WriteLine($"capture at the timeout: {shot}");
            throw new TimeoutException($"{failure}; the last render stats: {last}");
        }
    }

    [Fact(Timeout = 1_800_000)]
    public async Task TheOrrerysSunIsItsTexturedPhotosphere()
    {
        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS")
            ?? Path.Combine(Path.GetTempPath(), "tianwen-orrery-sun-shots");
        Directory.CreateDirectory(dir);

        var page = await fixture.NewPageAsync();
        var console = _console;
        page.Console += (_, m) => console.Enqueue($"[{m.Type}] {m.Text}");
        page.PageError += (_, e) => console.Enqueue($"[pageerror] {e}");
        page.Response += (_, r) => { if (r.Status >= 400 || r.Url.Contains("planet-textures", StringComparison.Ordinal)) console.Enqueue($"[http {r.Status}] {r.Url}"); };

        await page.GotoAsync(fixture.BaseUrl + "?e2e=1&view=sky", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await page.WaitForFunctionAsync(RenderStatsWait.Script("s.frames > 0", "s.frames"),
            null, new PageWaitForFunctionOptions { Timeout = BootTimeout });

        // The atlas is fetched beside the page when the sky first opens; until it is adopted every body is flat.
        await WaitAsync(page, "s.planetTextures > 0", "the planet texture atlas was never adopted", AtlasTimeout);
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await page.Keyboard.PressAsync("Alt+s");
        await WaitAsync(page, "s.orreryOpen", "the orrery did not open on Alt+S");
        var traced = await WaitAsync(page, "s.sunTextured > 0", "no frame traced the Sun from its map");
        output.WriteLine($"frames that traced the textured Sun: {traced}");

        var box = await page.Locator("#planner").BoundingBoxAsync() ?? throw new InvalidOperationException("the atlas canvas has no box");
        var path = Path.Combine(dir, "orrery-sun.png");
        var png = await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height },
        });
        output.WriteLine($"capture: {path}");

        var json = await page.EvaluateAsync<string>(SunBlobScript, Convert.ToBase64String(png));
        output.WriteLine($"the largest near-white blob: {json}");
        foreach (var line in console.Where(c => c.StartsWith("[pageerror]", StringComparison.Ordinal) || c.StartsWith("[error]", StringComparison.Ordinal)))
        {
            output.WriteLine("  " + line);
        }

        using var blob = JsonDocument.Parse(json);
        var size = blob.RootElement.GetProperty("size").GetInt32();
        size.ShouldBeGreaterThan(100, "no near-white disc: the Sun is not its photosphere");
        var width = blob.RootElement.GetProperty("x1").GetInt32() - blob.RootElement.GetProperty("x0").GetInt32() + 1;
        var height = blob.RootElement.GetProperty("y1").GetInt32() - blob.RootElement.GetProperty("y0").GetInt32() + 1;
        // A filled disc covers pi / 4 of its box; a label's glyphs, far less.
        ((double)size / (width * height)).ShouldBeGreaterThan(0.6, "the near-white pixels are not a filled disc");
        Math.Abs(width - height).ShouldBeLessThanOrEqualTo(3, "the near-white blob is not round");
    }
}
