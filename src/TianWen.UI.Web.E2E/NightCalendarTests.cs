using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// The night calendar in the browser (docs/plans/night-calendar.md, P4): the toolbar's date control, its verdict,
/// and the calendar the label opens on the canvas, driven by the same keys as the desktop's.
/// </summary>
/// <remarks>
/// The calendar is drawn on the canvas, so what is asserted is the DOM it moves: the date label changes when a
/// night is planned, and the verdict appears once the night is summarised. The verdict does not need the network:
/// without Open-Meteo it is the Moon's (Dark or Moonlit). A capture of the open calendar is written for a person to
/// look at (TIANWEN_WEB_SHOTS, else the temp folder), since whether it LOOKS right is not a DOM question.
/// </remarks>
[Collection(TianWenWebCollection.Name)]
public sealed class NightCalendarTests(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 180_000;
    private const float CalendarTimeout = 60_000;

    [Fact]
    public async Task TheDateLabelOpensTheCalendarAndItsKeysPlanAnotherNight()
    {
        var page = await fixture.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + "?e2e=1", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=planner]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });

        var label = page.Locator("[data-night]");
        await Expect(label).ToContainTextAsync("Tonight", new() { Timeout = BootTimeout });
        await Expect(page.Locator("[data-verdict]")).ToBeVisibleAsync(new() { Timeout = CalendarTimeout });
        output.WriteLine($"tonight: {await label.TextContentAsync()} {await page.Locator("[data-verdict]").TextContentAsync()}");

        await label.ClickAsync();
        await CaptureAsync(page, "night-calendar-open.png");

        // Two nights on and plan it: the label leaves "Tonight" for that night's date.
        var canvas = page.Locator("#planner");
        await canvas.PressAsync("ArrowRight");
        await canvas.PressAsync("ArrowRight");
        await canvas.PressAsync("Enter");
        await Expect(label).Not.ToContainTextAsync("Tonight", new() { Timeout = CalendarTimeout });
        output.WriteLine($"planned: {await label.TextContentAsync()}");

        // And back: the calendar's own T plans tonight again.
        await label.ClickAsync();
        await canvas.PressAsync("t");
        await Expect(label).ToContainTextAsync("Tonight", new() { Timeout = CalendarTimeout });

        await page.Context.CloseAsync();
    }

    private async Task CaptureAsync(IPage page, string name)
    {
        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS")
            ?? Path.Combine(Path.GetTempPath(), "tianwen-night-calendar-shots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);

        // A page capture clipped to the canvas: an element capture waits for a "stable" canvas, which one the
        // app repaints never is.
        var box = await page.Locator("#planner").BoundingBoxAsync()
            ?? throw new InvalidOperationException("the canvas has no box");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height },
        });
        output.WriteLine($"capture: {path}");
    }
}
