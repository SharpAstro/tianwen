using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// The planner maps browser keys through WebGl.Renderer's <c>WebInputMapping.ToInputKey(key, code)</c>
/// (WebGl.Renderer #14, tianwen #2009), so an unshifted <c>=</c> (the + key on a US layout) and the
/// keypad +/- reach the sky map as <c>InputKey.Plus</c>/<c>Minus</c> as in the SDL host. The sky map's
/// keyboard zoom is Ctrl + plus/minus, read back through the <c>?e2e=1</c> view hook (FOV).
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class PlannerKeyMapTests(TianWenWebFixture fixture)
{
    private const float BootTimeout = 120_000;
    private static readonly Regex ActiveClass = new(@"\bactive\b");

    private async Task<(IPage Page, ILocator Canvas)> SkyMapAsync()
    {
        var page = await fixture.WarmPageAsync();
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });
        await Expect(page.Locator("[data-atlas-loading]")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        return (page, page.Locator("#planner"));
    }

    private static async Task<(double Fov, double Ra, double Dec, double TimeOffsetMin)> ViewAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getSkyView()");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return (r.GetProperty("fovDeg").GetDouble(), r.GetProperty("centerRA").GetDouble(),
            r.GetProperty("centerDec").GetDouble(), r.GetProperty("timeOffsetMin").GetDouble());
    }

    private static async Task<double> FovAsync(IPage page) => (await ViewAsync(page)).Fov;

    private static async Task<double> PressAsync(IPage page, ILocator canvas, string chord)
    {
        await canvas.PressAsync(chord);
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        await Task.Delay(150);
        return await FovAsync(page);
    }

    [Theory]
    [InlineData("Control+=")]
    [InlineData("Control+Shift+=")]
    [InlineData("Control+NumpadAdd")]
    public async Task APlusKeyZoomsIn(string chord)
    {
        var (page, canvas) = await SkyMapAsync();
        var before = await FovAsync(page);
        var after = await PressAsync(page, canvas, chord);
        Assert.True(after < before, $"{chord} should shrink the FOV; before={before}, after={after}");
    }

    [Theory]
    [InlineData("Control+-")]
    [InlineData("Control+NumpadSubtract")]
    public async Task AMinusKeyZoomsOut(string chord)
    {
        var (page, canvas) = await SkyMapAsync();
        var before = await FovAsync(page);
        var after = await PressAsync(page, canvas, chord);
        Assert.True(after > before, $"{chord} should grow the FOV; before={before}, after={after}");
    }

    /// <summary>
    /// A named key (not a symbol) reaches the sky map through the same mapping. In the sky map the arrows
    /// step its clock (<c>SkyMapTab.StepTime</c>, Up an hour later, Down an hour earlier), so the sky moves
    /// under the view. The view's centre is held in RA/Dec and does not change, nor does the FOV, so what
    /// the key moved is read off the clock's scrub offset.
    /// </summary>
    [Theory]
    [InlineData("ArrowUp", 60)]
    [InlineData("ArrowDown", -60)]
    public async Task AnArrowKeyStepsTheSkyClockAndLeavesTheView(string key, double stepMin)
    {
        var (page, canvas) = await SkyMapAsync();
        var before = await ViewAsync(page);
        await canvas.PressAsync(key);
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r()))");
        await Task.Delay(150);
        var after = await ViewAsync(page);
        Assert.Equal(before.TimeOffsetMin + stepMin, after.TimeOffsetMin, 6);
        Assert.Equal(before.Fov, after.Fov, 6);
        Assert.Equal(before.Ra, after.Ra, 6);
        Assert.Equal(before.Dec, after.Dec, 6);
    }
}
