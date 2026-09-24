using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Captures the atlas at night with the Milky Way background on, off, and on again, so the background
/// can be LOOKED at (docs/plans/skymap-milkyway.md Phase 6).
/// </summary>
/// <remarks>
/// <para>Two things make this a probe rather than a suite test. It needs <c>milkyway.png</c> staged in
/// <c>wwwroot</c>, which only <c>pages.yml</c> does (a plain dev server answers 404 and the atlas draws no
/// background, by design), and its evidence is pixels, which the suite deliberately never asserts on.</para>
/// <para>The link pins the instant (<c>t=</c>) at an astronomically dark hour at the default site, so the
/// fade is 1 and the frame is static: the two "on" captures should be identical and "off" should differ
/// from both. Without a pinned time the sky advances between shots and a difference proves nothing.</para>
/// <para><b>No sleeps.</b> Every wait is on something the app reports: the console line it prints when the
/// texture lands, and the <c>milkyWay</c> flag and <c>frames</c> counter in the <c>?e2e=1</c> render stats
/// for "a frame with the new layer state has drawn". A fixed delay would be a guess about the interpreted
/// build's speed, and a key toggles the layer through an asynchronous interop hop, so the key press
/// returning says nothing about the frame.</para>
/// <code>
/// dotnet run --project tools/bake-milkyway/BakeMilkyWay.csproj -c Release -- \
///   src/TianWen.UI.Gui/Resources/milkyway.bgra.lz src/TianWen.UI.Web/wwwroot/milkyway.png
/// cd src/TianWen.UI.Web &amp;&amp; dotnet run -c Release --urls http://localhost:5099
/// TIANWEN_WEB_BASEURL=http://localhost:5099 TIANWEN_WEB_PROBE=1 TIANWEN_E2E_CHANNEL=msedge \
///   dotnet test TianWen.UI.Web.E2E --filter FullyQualifiedName~MilkyWayBackgroundProbe
/// </code>
/// </remarks>
[Collection(TianWenWebCollection.Name)]
public sealed class MilkyWayBackgroundProbe(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 180_000;
    private const float FrameTimeout = 30_000;

    // Cygnus, high in the south-west at 23:00 local on 2026-09-17 at the default site (47.5 N, 11 E),
    // two hours past astronomical dusk: the fade is 1 and the band crosses the whole field. e2e=1 turns
    // on the render-stats hook the waits below read.
    private const string NightLink = "?e2e=1&ra=305&dec=38&fov=90&t=2026-09-17T21:00:00Z";

    [Fact]
    public async Task CaptureAsync()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_WEB_PROBE") == "1",
            "probe: set TIANWEN_WEB_PROBE=1 and serve a build with milkyway.png staged (see the remarks)");

        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS")
            ?? Path.Combine(Path.GetTempPath(), "tianwen-milkyway-shots");
        Directory.CreateDirectory(dir);

        var page = await fixture.NewPageAsync();
        var console = new ConcurrentQueue<string>();
        page.Console += (_, m) => console.Enqueue($"[{m.Type}] {m.Text}");

        // Armed BEFORE navigating: the texture can land before the page is interactive, and a waiter set up
        // afterwards would miss the line and wait out the whole timeout. Either outcome ends the wait, so a
        // 404 fails at once with its reason instead of after three minutes.
        var textureOutcome = page.WaitForConsoleMessageAsync(new PageWaitForConsoleMessageOptions
        {
            Predicate = m => m.Text.Contains("milky way texture loaded") || m.Text.Contains("no Milky Way background"),
            Timeout = BootTimeout,
        });
        await page.GotoAsync(fixture.BaseUrl + NightLink, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });

        var outcome = (await textureOutcome).Text;
        output.WriteLine($"texture: {outcome}");
        Assert.Contains("milky way texture loaded", outcome);

        var canvas = page.Locator("#planner");

        // The SKY only. Two things on the canvas move independently of the background and would make any
        // comparison prove nothing: the "Loading the sky you are looking at" banner animates across the
        // top on a server without the Tycho-2 members staged (measured: every pixel that differed between
        // two "on" captures was inside it), and the layer palette's row for this very layer changes state.
        var box = await canvas.BoundingBoxAsync() ?? throw new InvalidOperationException("the atlas canvas has no box");
        var sky = new Clip { X = box.X, Y = box.Y + 80, Width = box.Width - 160, Height = box.Height - 120 };

        // Resolves once a frame has DRAWN with the layer in the wanted state. The stats call runs on the
        // single WASM thread, so it can never observe a frame half-drawn: frames > n means the repaint that
        // followed the toggle has completed. The predicate is synchronous on purpose (RenderStatsWait): an
        // async one returned a Promise, which Playwright took as truthy on the first poll.
        async Task<int> DrawnWithLayerAsync(bool on, int afterFrame)
        {
            var script = RenderStatsWait.Script(
                $"s.milkyWay === {(on ? "true" : "false")} && s.frames > {afterFrame.ToString(CultureInfo.InvariantCulture)}",
                "s.frames");
            var handle = await page.WaitForFunctionAsync(script, null, new PageWaitForFunctionOptions { Timeout = FrameTimeout });
            return await handle.JsonValueAsync<int>();
        }

        async Task<byte[]> ShootAsync(string name)
        {
            var path = Path.Combine(dir, name + ".png");
            var png = await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, Clip = sky });
            output.WriteLine($"{name,-12} {png.Length,9} bytes  {path}");
            return png;
        }

        var frame = await DrawnWithLayerAsync(on: true, afterFrame: 0);
        var on = await ShootAsync("1-on");

        await canvas.PressAsync("s");
        frame = await DrawnWithLayerAsync(on: false, afterFrame: frame);
        var off = await ShootAsync("2-off");

        await canvas.PressAsync("s");
        await DrawnWithLayerAsync(on: true, afterFrame: frame);
        var onAgain = await ShootAsync("3-on-again");

        foreach (var line in console.Where(c => c.Contains("[tianwen-web]")))
        {
            output.WriteLine("  " + line);
        }

        Assert.False(on.AsSpan().SequenceEqual(off), "switching the layer off changed nothing on screen");
        Assert.True(on.AsSpan().SequenceEqual(onAgain), "the two 'on' captures of the sky differ, so the frame is not static and the comparison proves nothing");
    }
}
