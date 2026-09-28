using System;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The time controls in the atlas's info strip (2026-09-28): the strip showed the time with nothing to
/// press, so moving the atlas in time took the arrow keys, which a touch screen does not have. Pressed
/// through the regions the strip REGISTERED, the geometry a user clicks, never through the tree it meant
/// to build.
/// </summary>
[Collection("Astrometry")]
public class SkyMapTimeControlsTests
{
    private static readonly DateTimeOffset Evening = new(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(SkyMapTab<RgbaImage> Tab, PlannerState Planner, ITimeProvider Clock, RectF32 Content)>
        RenderedAsync(RgbaImageRenderer renderer)
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var tab = new SkyMapTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var planner = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = -37.9,
            SiteLongitude = 145.2,
            SiteTimeZone = TimeSpan.FromHours(10),
            PlanningDate = Evening,
            AstroDark = Evening.AddHours(7.5),
            AstroTwilight = Evening.AddHours(16.5),
        };
        var clock = new FakeTimeProviderWrapper(Evening);
        var content = new RectF32(0, 0, 1000, 600);
        tab.Render(planner, content, clock);
        return (tab, planner, clock, content);
    }

    private static ClickableRegion Button(SkyMapTab<RgbaImage> tab, string action)
        => tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit b && b.Action == action);

    private static bool HasButton(SkyMapTab<RgbaImage> tab, string action)
        => tab.GetRegisteredRegions().Any(r => r.Result is HitResult.ButtonHit b && b.Action == action);

    private static void Press(ClickableRegion region)
        => region.OnClick.ShouldNotBeNull($"{region.Result} is a button").Invoke(InputModifier.None);

    /// <summary>
    /// The controls sit in the MIDDLE of the strip, level with the info text beside them (reported
    /// 2026-09-28, a screenshot: the group measured to one line of text and hugged the strip's top). Each
    /// button spans the strip, and the ink of its label is centred in it, read off the pixels, since a
    /// region spanning the strip says nothing about where its text was drawn inside it.
    /// </summary>
    [Theory]
    [InlineData("SkyTimeStep:-1d")]
    [InlineData("SkyTimeMenu")]
    [InlineData("SkyTimeStep:+1h")]
    public async Task TheControlsAreCentredInTheStrip(string action)
    {
        using var renderer = new RgbaImageRenderer(1000, 600);
        var (tab, _, _, content) = await RenderedAsync(renderer);
        const float StripH = 24f; // DPI 1
        var stripTop = content.Y + content.Height - StripH;

        var button = Button(tab, action);
        button.Y.ShouldBe(stripTop, 0.5f, "the button starts at the strip's top edge");
        button.Height.ShouldBe(StripH, 0.5f, "and spans the strip");

        // The label's ink: the pixels of the button's own rect far brighter than its dark fill, weighted by
        // row. By brightness, not by colour, since the time is blue while shifted (as it is here, the
        // planner's date being set) and grey while live.
        var surface = renderer.Surface;
        double rowSum = 0, count = 0;
        for (var y = (int)button.Y; y < (int)(button.Y + button.Height); y++)
        {
            for (var x = (int)button.X; x < (int)(button.X + button.Width); x++)
            {
                var i = ((y * surface.Width) + x) * 4;
                if (surface.Pixels[i] + surface.Pixels[i + 1] + surface.Pixels[i + 2] > 250)
                {
                    rowSum += y;
                    count++;
                }
            }
        }
        count.ShouldBeGreaterThan(0, "the label drew");
        (rowSum / count + 0.5).ShouldBe(stripTop + (StripH / 2f), 2.5, "the label's ink is centred in the strip");
    }

    [Theory]
    [InlineData("SkyTimeStep:+10m", 10)]
    [InlineData("SkyTimeStep:+1h", 60)]
    [InlineData("SkyTimeStep:-1h", -60)]
    [InlineData("SkyTimeStep:-1d", -1440)]
    public async Task AStepButtonMovesTheTimeAsItsKeyDoes(string action, int minutes)
    {
        using var renderer = new RgbaImageRenderer(1000, 600);
        var (tab, _, _, _) = await RenderedAsync(renderer);

        Press(Button(tab, action));

        tab.State.TimeOffset.ShouldBe(TimeSpan.FromMinutes(minutes));
    }

    [Fact]
    public async Task NowShowsOnlyWhileScrubbedAndTakesTheViewBackToLive()
    {
        using var renderer = new RgbaImageRenderer(1000, 600);
        var (tab, planner, clock, content) = await RenderedAsync(renderer);
        HasButton(tab, "SkyTimeNow").ShouldBeFalse("live: there is nothing to go back to");

        Press(Button(tab, "SkyTimeStep:+1h"));
        tab.Render(planner, content, clock);
        Press(Button(tab, "SkyTimeNow"));

        tab.State.TimeOffset.ShouldBe(TimeSpan.Zero);
        tab.Render(planner, content, clock);
        HasButton(tab, "SkyTimeNow").ShouldBeFalse();
    }

    [Fact]
    public async Task TheTimeOpensTheNightsLandmarksAboveItAndDuskGoesToDusk()
    {
        using var renderer = new RgbaImageRenderer(1000, 600);
        var (tab, planner, clock, content) = await RenderedAsync(renderer);
        var time = Button(tab, "SkyTimeMenu");

        Press(time);
        tab.TimeMenu.IsOpen.ShouldBeTrue();
        tab.TimeMenu.Items.Select(i => i.Label).ShouldBe(["Dusk  05:30", "Midnight", "Dawn  14:30", "Now"],
            "the planned night's landmarks, in site-local time (UTC+10)");

        // A DROP-UP: the strip is at the bottom of the map, so the menu opens above the time, not under it.
        tab.Render(planner, content, clock);
        DropdownRows.First(tab).Y.ShouldBeLessThan(time.Y);

        tab.TimeMenu.TrySelect(0).ShouldBeTrue();
        (planner.PlanningDate.ShouldNotBeNull() + tab.State.TimeOffset).ShouldBe(planner.AstroDark,
            "the viewing time is the planner's date plus the offset, and Dusk puts it at astronomical dark");
    }
}
