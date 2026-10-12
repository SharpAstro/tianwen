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
        RgbaImageRenderer renderer, SkyMapMode mode, float dpiScale = 1f)
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var tab = new SkyMapTab<RgbaImage>(renderer) { DpiScale = dpiScale, FontPath = FontResolver.ResolveSystemFont() };
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

    // At 1.5x as well as 1x: the strip is laid out in device pixels, every size in it times the DPI scale, and a scale
    // applied twice is invisible at 1x.
    [Theory]
    [InlineData(SkyMapMode.Horizon, 1f)]
    [InlineData(SkyMapMode.Equatorial, 1f)]
    [InlineData(SkyMapMode.Horizon, 1.5f)]
    [InlineData(SkyMapMode.Equatorial, 1.5f)]
    public async Task BothFramesAreNamedInTheStrip(SkyMapMode mode, float dpiScale)
    {
        using var renderer = new RgbaImageRenderer(ContentW, ContentH);
        var (tab, content, _, _) = await RenderedAsync(renderer, mode, dpiScale);

        var altAz = Segment(tab, SkyMapTab<RgbaImage>.ModeHorizonAction);
        var equatorial = Segment(tab, SkyMapTab<RgbaImage>.ModeEquatorialAction);

        // With a height: a group left to size itself in the strip's row was given none, painted nothing and took no
        // press, while its regions were still registered.
        altAz.Height.ShouldBeGreaterThan(10f);
        equatorial.Height.ShouldBe(altAz.Height);

        // At the strip's left, side by side, along its bottom edge, and clear of the time controls at its right.
        altAz.X.ShouldBeLessThan(equatorial.X);
        altAz.Y.ShouldBeGreaterThan(content.Height * 0.85f, "in the status strip along the bottom");
        altAz.X.ShouldBeLessThan(content.Width * 0.25f, "at its left");
        altAz.Height.ShouldBe(24f * dpiScale, 1f, "as tall as the strip it sits in");
        var firstTimeControl = tab.GetRegisteredRegions()
            .Where(r => r.Result is HitResult.ButtonHit hit && hit.Action.StartsWith("SkyTimeStep:", StringComparison.Ordinal))
            .Min(r => r.X);
        (equatorial.X + equatorial.Width).ShouldBeLessThan(firstTimeControl, "the switch never runs into the time controls");

        // Each segment holds its own label whole at this scale: the strip's text size times the DPI scale, measured
        // in the face the strip draws with.
        var label = renderer.MeasureText("Equatorial", tab.FontPath!, 12f * 0.85f * dpiScale).Width;
        equatorial.Width.ShouldBeGreaterThanOrEqualTo(label, "the wider label is not cut");
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
