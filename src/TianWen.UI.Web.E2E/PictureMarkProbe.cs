using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Opens the atlas on M42 with the object overlay on and captures the labels, so the photo mark after the
/// label of an object with a verified picture (a COLOUR emoji, U+1F4F7) can be LOOKED at in a real browser:
/// it is the one consumer of WebGl.Renderer's colour-glyph path, whose shader and RGBA atlas upload only a
/// WebGL2 context can prove.
/// </summary>
/// <remarks>
/// <para>A probe, not a suite test, for the same reasons as <see cref="ObjectPictureProbe"/>: its evidence is a
/// picture on a canvas, and the panel it waits on fetches from Wikimedia.</para>
/// <para><b>No sleeps.</b> It waits on the <c>?e2e=1</c> render stats: a drawn panel picture (the catalogue has
/// loaded and the selection resolved), then the overlay switched on AND label time accumulated, which only a
/// frame that placed labels adds. The key goes to the DOCUMENT, never through a locator, which would focus the
/// canvas first and hide a focus bug.</para>
/// <code>
/// cd src/TianWen.UI.Web &amp;&amp; dotnet run -c Release --urls http://localhost:5099
/// TIANWEN_WEB_BASEURL=http://localhost:5099 TIANWEN_WEB_PROBE=1 TIANWEN_E2E_CHANNEL=msedge \
///   dotnet test TianWen.UI.Web.E2E --filter FullyQualifiedName~PictureMarkProbe
/// </code>
/// </remarks>
[Collection(TianWenWebCollection.Name)]
public sealed class PictureMarkProbe(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 180_000;
    private const float PictureTimeout = 60_000;
    private const float LabelTimeout = 30_000;

    [Fact]
    public async Task CaptureAsync()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_WEB_PROBE") == "1",
            "probe: set TIANWEN_WEB_PROBE=1 against a running web build (see the remarks); it fetches from Wikimedia");

        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS")
            ?? Path.Combine(Path.GetTempPath(), "tianwen-picture-mark-shots");
        Directory.CreateDirectory(dir);

        var page = await fixture.NewPageAsync();
        var console = new List<string>();
        page.Console += (_, m) => { lock (console) { console.Add($"[{m.Type}] {m.Text}"); } };
        page.PageError += (_, e) => { lock (console) { console.Add($"[pageerror] {e}"); } };
        // The console's "Failed to load resource" line names no URL; this does, so a failed fetch is identified
        // rather than guessed at.
        page.Response += (_, r) => { if (r.Status >= 400) { lock (console) { console.Add($"[http {r.Status}] {r.Url}"); } } };

        await page.GotoAsync(fixture.BaseUrl + "?e2e=1&view=sky&object=M42", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await page.WaitForFunctionAsync(RenderStatsWait.Script("s.pictures > 0", "s.pictures"),
            null, new PageWaitForFunctionOptions { Timeout = PictureTimeout });

        await page.Keyboard.PressAsync("o");
        var labelMs = await (await page.WaitForFunctionAsync(
            RenderStatsWait.Script("s.overlay && s.labelMs > 0", "s.labelMs"),
            null, new PageWaitForFunctionOptions { Timeout = LabelTimeout })).JsonValueAsync<double>();
        output.WriteLine($"overlay on, label time so far: {labelMs:F1} ms");

        var box = await page.Locator("#planner").BoundingBoxAsync() ?? throw new InvalidOperationException("the atlas canvas has no box");
        var path = Path.Combine(dir, "m42-labels.png");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height },
        });
        output.WriteLine($"capture: {path}");

        // Baked only by pages.yml's deploy (JPL sends no CORS headers), so a local dev server has neither and the
        // app reads the absence as "no comets". Exempted BY NAME, so any other failed fetch still fails the probe.
        string[] deployOnly = ["/comets-apparitions.json", "/comets-sbdb.json"];
        string[] errors;
        lock (console)
        {
            errors = [.. console.Where(c => c.StartsWith("[pageerror]", StringComparison.Ordinal)
                || (c.StartsWith("[http ", StringComparison.Ordinal) && !deployOnly.Any(asset => c.EndsWith(asset, StringComparison.Ordinal)))
                // The console's own line for a failed fetch carries no URL; the [http] entry above is its identified twin.
                || (c.StartsWith("[error]", StringComparison.Ordinal) && !c.Contains("Failed to load resource", StringComparison.Ordinal)))];
        }
        foreach (var line in errors)
        {
            output.WriteLine("  " + line);
        }

        // A colour-glyph shader that fails to compile or link surfaces here, not as a missing mark.
        Assert.Empty(errors);
    }
}
