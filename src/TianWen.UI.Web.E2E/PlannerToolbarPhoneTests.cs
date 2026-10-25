using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// The planner's toolbar on a phone (issue #1818 item 1): at 390x844 it is one row, Lat and Lon sit inside the
/// viewport, and Recompute / Locate are still reachable as icon buttons. The toolbar is plain HTML/CSS, so its
/// measured boxes are the truth.
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class PlannerToolbarPhoneTests(TianWenWebFixture fixture)
{
    private const float BootTimeout = 180_000;

    // One row: a button is about 28 px tall and the toolbar adds 8 px of padding; two rows would be 60 or more.
    private const double OneRowMaxHeight = 48;

    [Fact]
    public async Task OnAPhoneTheToolbarIsOneRowWithLatLonInView()
    {
        var page = await fixture.NewPageAsync();
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.BaseUrl + "?e2e=1", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=planner]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });

        // The catalog load keeps the main thread busy, and a box read waits on it.
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });

        var toolbar = await page.Locator(".toolbar").BoundingBoxAsync(new() { Timeout = BootTimeout });
        Assert.NotNull(toolbar);
        Assert.True(toolbar.Height <= OneRowMaxHeight, $"toolbar is {toolbar.Height} px tall, more than one row");

        foreach (var name in new[] { "Latitude", "Longitude" })
        {
            var box = await page.GetByLabel(name).BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.True(box.X >= 0 && box.X + box.Width <= 390, $"{name} spans {box.X}..{box.X + box.Width}, outside the viewport");
        }

        foreach (var name in new[] { "Recompute", "Locate" })
        {
            var button = page.GetByRole(AriaRole.Button, new() { Name = name });
            await Expect(button).ToBeVisibleAsync();
            var box = await button.BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.True(box.X + box.Width <= 390, $"{name} runs off the right edge");
        }
        await page.Context.CloseAsync();
    }
}
