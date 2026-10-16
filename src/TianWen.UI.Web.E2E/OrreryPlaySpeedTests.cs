using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// The orrery in the browser (docs/plans/atlas-planet-detail.md, A5, #1573): Alt+S opens it from the atlas, the strip's speed button
/// opens the drop-up of the fifteen speeds, a press on "1 second = 2 years" sets the rate, and Play then runs the atlas's clock at it.
/// The strip and the drop-up are canvas paintings, so every press goes where the app says it PAINTED them (the <c>?e2e=1</c> hooks
/// <c>getButtonRect</c> and <c>getDropdownRowRect</c>), and every wait is on something the app reports, never a sleep.
/// </summary>
/// <remarks>
/// The canvas is captured to <c>%TEMP%/tianwen-orrery-shots/orrery-two-years.png</c> (or <c>TIANWEN_WEB_SHOTS</c>) once Play has run,
/// so the frame can be looked at.
/// </remarks>
[Collection(TianWenWebCollection.Name)]
public sealed class OrreryPlaySpeedTests(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 180_000;
    private const float FrameTimeout = 60_000;

    // Two years a second, the thirteenth of the fifteen (index 12); 730.5 days, as OrreryState.PlaySpeeds has it.
    private const int TwoYearsRow = 12;
    private const double TwoYearsDays = 2 * 365.25;

    // A pinned instant, so the frame does not depend on the day the test runs.
    private const string AtlasLink = "?e2e=1&view=sky&t=2026-10-13T22:00:00Z";

    private sealed record Rect(double x, double y, double w, double h);

    /// <summary>
    /// A <c>page.WaitForFunctionAsync</c> predicate that calls an asynchronous <c>window.__tianwenTest</c> hook one request at a time
    /// and resolves with its last answer once that answer is not "null" (the synchronous shape <see cref="RenderStatsWait"/> explains).
    /// </summary>
    private static string HookAnswered(string call, string slot)
        => "() => { const t = window.__tianwenTest; if (!t) return false; "
            + $"if (!window.__{slot}InFlight) {{ window.__{slot}InFlight = true; "
            + $"t.{call}.then(j => {{ window.__{slot} = j; }}).finally(() => {{ window.__{slot}InFlight = false; }}); }} "
            + $"const r = window.__{slot}; return r && r !== 'null' ? r : false; }}";

    private static async Task<Rect> PaintedAsync(IPage page, string call, string slot)
    {
        var handle = await page.WaitForFunctionAsync(HookAnswered(call, slot), null, new PageWaitForFunctionOptions { Timeout = FrameTimeout });
        return JsonSerializer.Deserialize<Rect>(await handle.JsonValueAsync<string>())
            ?? throw new InvalidOperationException($"{call} answered no rect");
    }

    private static async Task PressAsync(ILocator canvas, Rect rect)
        => await canvas.ClickAsync(new LocatorClickOptions { Position = new Position { X = (float)(rect.x + (rect.w / 2)), Y = (float)(rect.y + (rect.h / 2)) } });

    private static async Task<JsonElement> StatsWhenAsync(IPage page, string condition)
    {
        var handle = await page.WaitForFunctionAsync(RenderStatsWait.Script(condition, "JSON.stringify(s)"), null,
            new PageWaitForFunctionOptions { Timeout = FrameTimeout });
        return JsonDocument.Parse(await handle.JsonValueAsync<string>()).RootElement;
    }

    [Fact]
    public async Task APickedSpeedIsTheRatePlayRunsAt()
    {
        var page = await fixture.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + AtlasLink, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        var canvas = page.Locator("#planner");

        await canvas.PressAsync("Alt+s");
        var stats = await StatsWhenAsync(page, "s.orreryOpen && s.orreryFrames > 0");
        output.WriteLine($"the orrery opened at {stats.GetProperty("orreryDaysPerSecond").GetDouble()} days a second");

        await PressAsync(canvas, await PaintedAsync(page, "getButtonRect('OrrerySpeed')", "speedButton"));
        var row = await PaintedAsync(page, $"getDropdownRowRect({TwoYearsRow.ToString(CultureInfo.InvariantCulture)})", "speedRow");
        output.WriteLine($"the drop-up's \"1 second = 2 years\" row painted at ({row.x:F0}, {row.y:F0}) {row.w:F0} x {row.h:F0}");
        await PressAsync(canvas, row);
        stats = await StatsWhenAsync(page, $"s.orreryDaysPerSecond === {TwoYearsDays.ToString(CultureInfo.InvariantCulture)}");

        var playButton = await PaintedAsync(page, "getButtonRect('OrreryPlay')", "playButton");
        var before = stats.GetProperty("timeOffsetDays").GetDouble();
        await PressAsync(canvas, playButton);
        // At least a played second: two years on the atlas's clock, however slowly the interpreted build draws.
        var after = await StatsWhenAsync(page,
            $"s.orreryPlaying && s.timeOffsetDays >= {(before + TwoYearsDays).ToString(CultureInfo.InvariantCulture)}");
        var frames = after.GetProperty("orreryFrames").GetInt32();
        output.WriteLine($"Play ran the clock {after.GetProperty("timeOffsetDays").GetDouble() - before:F1} days by orrery frame {frames}");

        // Pause on the frame captured: the clock then holds while the page is shot.
        await PressAsync(canvas, playButton);
        await StatsWhenAsync(page, $"!s.orreryPlaying && s.orreryFrames > {frames.ToString(CultureInfo.InvariantCulture)}");
        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS") ?? Path.Combine(Path.GetTempPath(), "tianwen-orrery-shots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "orrery-two-years.png");
        var box = await canvas.BoundingBoxAsync() ?? throw new InvalidOperationException("the atlas canvas has no box");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height },
        });
        output.WriteLine($"the canvas: {path}");
        await page.Context.CloseAsync();
    }
}
