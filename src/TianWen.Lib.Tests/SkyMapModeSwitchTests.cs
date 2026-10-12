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
/// The view's frame is shown and switched in words at the status strip's left, "Alt/Az" or "Equatorial": it was a
/// bracketed two-letter code mid-strip, and nothing said at a glance which mode was in force (the owner,
/// 2026-10-12). A press on the other segment switches as P does, levelling into the new frame.
/// </summary>
[Collection("Astrometry")]
public class SkyMapModeSwitchTests
{
    private const int ContentW = 1280;
    private const int ContentH = 720;

    private static async Task<(SkyMapTab<RgbaImage> Tab, RectF32 Content, PlannerState Planner, ITimeProvider Clock)> RenderedAsync(
        RgbaImageRenderer renderer, SkyMapMode mode)
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var tab = new SkyMapTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        tab.State.Mode = mode;
        var now = new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.Zero);
        var planner = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = 45.0,
            SiteLongitude = -75.0,
            SiteTimeZone = TimeSpan.Zero,
            PlanningDate = now,
        };
        var content = new RectF32(0, 0, ContentW, ContentH);
        var clock = new FakeTimeProviderWrapper(now);
        tab.Render(planner, content, clock);
        return (tab, content, planner, clock);
    }

    private static ClickableRegion Segment(SkyMapTab<RgbaImage> tab, string action)
        => tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit hit && hit.Action == action);

    [Theory]
    [InlineData(SkyMapMode.Horizon)]
    [InlineData(SkyMapMode.Equatorial)]
    public async Task BothFramesAreNamedInTheStrip(SkyMapMode mode)
    {
        using var renderer = new RgbaImageRenderer(ContentW, ContentH);
        var (tab, content, _, _) = await RenderedAsync(renderer, mode);

        var altAz = Segment(tab, SkyMapTab<RgbaImage>.ModeHorizonAction);
        var equatorial = Segment(tab, SkyMapTab<RgbaImage>.ModeEquatorialAction);

        // With a height: a group left to size itself in the strip's row was given none, painted nothing and took no
        // press, while its regions were still registered.
        altAz.Height.ShouldBeGreaterThan(10f);
        equatorial.Height.ShouldBe(altAz.Height);

        // At the strip's left, side by side, along its bottom edge.
        altAz.X.ShouldBeLessThan(equatorial.X);
        altAz.Y.ShouldBeGreaterThan(content.Height * 0.9f, "in the status strip along the bottom");
        altAz.X.ShouldBeLessThan(content.Width * 0.25f, "at its left");
    }

    [Theory]
    [InlineData(SkyMapMode.Horizon, SkyMapMode.Equatorial)]
    [InlineData(SkyMapMode.Equatorial, SkyMapMode.Horizon)]
    public async Task APressOnTheOtherFrameSwitchesToItAsPDoes(SkyMapMode from, SkyMapMode to)
    {
        using var renderer = new RgbaImageRenderer(ContentW, ContentH);
        var (tab, _, _, _) = await RenderedAsync(renderer, from);
        var other = Segment(tab, to == SkyMapMode.Horizon
            ? SkyMapTab<RgbaImage>.ModeHorizonAction
            : SkyMapTab<RgbaImage>.ModeEquatorialAction);

        // A click: the press and its release through one router, as a host delivers them.
        var (x, y) = (other.X + (other.Width / 2f), other.Y + (other.Height / 2f));
        UiRouting.Route(tab, new InputEvent.MouseDown(x, y, MouseButton.Left), new InputEvent.MouseUp(x, y, MouseButton.Left));

        tab.State.Mode.ShouldBe(to);
        tab.State.IsLevelling.ShouldBeTrue("the new frame is established by a level, as P establishes it");
    }
}
