using System.Collections.Concurrent;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Opens the atlas on an object with a picture and captures the info panel once the picture has drawn, so the
/// browser half of <c>docs/plans/object-imagery.md</c> P1 can be LOOKED at.
/// </summary>
/// <remarks>
/// <para>A probe, not a suite test: it fetches from Wikimedia, which the suite never depends on, and its
/// evidence is a picture on a canvas.</para>
/// <para><b>No sleeps.</b> The deep link selects the object once the catalogue loads, the picture arrives
/// whenever the network delivers it, and the app counts a drawn picture in the <c>?e2e=1</c> render stats, so
/// that count is the one thing waited on.</para>
/// <code>
/// cd src/TianWen.UI.Web &amp;&amp; dotnet run -c Release --urls http://localhost:5099
/// TIANWEN_WEB_BASEURL=http://localhost:5099 TIANWEN_WEB_PROBE=1 TIANWEN_E2E_CHANNEL=msedge \
///   dotnet test TianWen.UI.Web.E2E --filter FullyQualifiedName~ObjectPictureProbe
/// </code>
/// </remarks>
[Collection(TianWenWebCollection.Name)]
public sealed class ObjectPictureProbe(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 180_000;
    private const float PictureTimeout = 60_000;

    [Fact]
    public async Task CaptureAsync()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_WEB_PROBE") == "1",
            "probe: set TIANWEN_WEB_PROBE=1 against a running web build (see the remarks); it fetches from Wikimedia");

        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS")
            ?? Path.Combine(Path.GetTempPath(), "tianwen-object-picture-shots");
        Directory.CreateDirectory(dir);

        var page = await fixture.NewPageAsync();
        var console = new ConcurrentQueue<string>();
        page.Console += (_, m) => console.Enqueue($"[{m.Type}] {m.Text}");

        await page.GotoAsync(fixture.BaseUrl + "?e2e=1&view=sky&object=M42", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });

        // Resolves once a frame has drawn the picture, which only happens after the object resolved, its panel
        // opened, the browser fetched and decoded the thumbnail, and the texture was adopted.
        var handle = await page.WaitForFunctionAsync(
            RenderStatsWait.Script("s.pictures > 0", "s.pictures"),
            null, new PageWaitForFunctionOptions { Timeout = PictureTimeout });
        var drawn = await handle.JsonValueAsync<int>();
        output.WriteLine($"pictures drawn: {drawn}");

        // A page capture clipped to the canvas, as the Milky Way probe takes: an ELEMENT capture waits for the
        // canvas to be "stable", which a canvas the app keeps repainting never reports.
        var box = await page.Locator("#planner").BoundingBoxAsync() ?? throw new InvalidOperationException("the atlas canvas has no box");
        var path = Path.Combine(dir, "m42-panel.png");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height },
        });
        output.WriteLine($"capture: {path}");

        foreach (var line in console.Where(c => c.Contains("[error]", StringComparison.Ordinal) || c.Contains("texture", StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine("  " + line);
        }

        Assert.True(drawn > 0);
    }
}
