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

    // Cygnus, high in the south-west at 23:00 local on 2026-09-17 at the default site (47.5 N, 11 E),
    // two hours past astronomical dusk: the fade is 1 and the band crosses the whole field.
    private const string NightLink = "?ra=305&dec=38&fov=90&t=2026-09-17T21:00:00Z";

    [Fact]
    public async Task CaptureAsync()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_WEB_PROBE") == "1",
            "probe: set TIANWEN_WEB_PROBE=1 and serve a build with milkyway.png staged (see the remarks)");

        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS")
            ?? Path.Combine(Path.GetTempPath(), "tianwen-milkyway-shots");
        Directory.CreateDirectory(dir);

        var page = await fixture.NewPageAsync();
        var console = new List<string>();
        page.Console += (_, m) => { lock (console) { console.Add($"[{m.Type}] {m.Text}"); } };
        page.PageError += (_, e) => { lock (console) { console.Add($"[pageerror] {e}"); } };

        await page.GotoAsync(fixture.BaseUrl + NightLink, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        var canvas = page.Locator("#planner");

        // The app says when the texture landed; a line that has printed cannot un-print, so this cannot
        // pass before the upload. The failure line is watched too, so a 404 fails fast with its reason.
        var deadline = DateTime.UtcNow.AddMilliseconds(BootTimeout);
        string? outcome = null;
        while (outcome is null && DateTime.UtcNow < deadline)
        {
            lock (console)
            {
                outcome = console.FirstOrDefault(c => c.Contains("milky way texture loaded") || c.Contains("no Milky Way background"));
            }
            if (outcome is null)
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }
        }
        output.WriteLine($"texture: {outcome ?? "no line within the boot timeout"}");
        Assert.True(outcome?.Contains("milky way texture loaded") == true, $"the Milky Way texture did not load: {outcome}");

        // The SKY only. Two things on the canvas move independently of the background and would make any
        // comparison prove nothing: the "Loading the sky you are looking at" banner animates across the
        // top on a server without the Tycho-2 members staged (measured: every pixel that differed between
        // two "on" captures was inside it), and the layer palette's row for this very layer changes state.
        var box = await canvas.BoundingBoxAsync() ?? throw new InvalidOperationException("the atlas canvas has no box");
        var sky = new Clip { X = box.X, Y = box.Y + 80, Width = box.Width - 160, Height = box.Height - 120 };

        async Task<byte[]> ShootAsync(string name)
        {
            await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
            await Task.Delay(400);
            var path = Path.Combine(dir, name + ".png");
            var png = await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, Clip = sky });
            output.WriteLine($"{name,-12} {png.Length,9} bytes  {path}");
            return png;
        }

        var on = await ShootAsync("1-on");
        await canvas.PressAsync("s");
        var off = await ShootAsync("2-off");
        await canvas.PressAsync("s");
        var onAgain = await ShootAsync("3-on-again");

        lock (console)
        {
            foreach (var line in console.Where(c => c.Contains("[tianwen-web]") || c.Contains("error", StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine("  " + line);
            }
        }

        Assert.False(on.AsSpan().SequenceEqual(off), "switching the layer off changed nothing on screen");
        Assert.True(on.AsSpan().SequenceEqual(onAgain), "the two 'on' captures differ, so the frame is not static and the comparison proves nothing");
    }
}
