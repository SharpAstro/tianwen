using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The GUI window's top bar and sidebar, drawn on a CPU surface (#2139): <see cref="GuiWindowChrome{TSurface}"/> is the one
/// widget the Vulkan window composes, so a test can paint it on an <see cref="RgbaImageRenderer"/> and read what it
/// registered, at two window widths and at DPI 1 and 1.5.
/// </summary>
[Collection("UI")]
public class GuiWindowChromeTests
{
    // Tall enough for every rail cell at DPI 1.5: the last cell (Notifications) is not registered once it falls off the window.
    private const uint SurfaceH = 1000;

    public static TheoryData<uint, float> Windows => new()
    {
        { 700u, 1f },
        { 1600u, 1f },
        { 700u, 1.5f },
        { 1600u, 1.5f },
    };

    private sealed record Painted(
        GuiWindowChrome<RgbaImage> Chrome,
        List<ClickableRegion> Regions);

    private static Painted Paint(RgbaImageRenderer renderer, uint width, float dpi, bool sessionRunning)
    {
        var font = FontResolver.ResolveSystemFont();
        var chrome = new GuiWindowChrome<RgbaImage>(renderer) { FontPath = font, DpiScale = dpi };
        chrome.Sidebar.FontPath = font;
        chrome.Sidebar.DpiScale = dpi;
        chrome.NavigationStrip.FontPath = font;
        chrome.NavigationStrip.DpiScale = dpi;

        var appState = new GuiAppState
        {
            ActiveProfile = new Profile(Guid.NewGuid(), "Test", ProfileData.Empty),
            StatusMessage = "Found 3 devices, and a status message long enough that the bar has to give it up on a narrow window",
        };
        var local = new LiveSessionState { IsRunning = sessionRunning };
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 9, 19, 17, 0, 0, TimeSpan.Zero));

        chrome.Render(appState, new PlannerState(), clock, local, local,
            new EquipmentTabState(), new SessionTabState(), new SkyMapState(), width, SurfaceH);

        var regions = new List<ClickableRegion>();
        chrome.CollectPaintedRegions(regions);
        return new Painted(chrome, regions);
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void EveryTabHasARailRegion(uint width, float dpi)
    {
        using var renderer = new RgbaImageRenderer(width, SurfaceH);

        var painted = Paint(renderer, width, dpi, sessionRunning: false);

        foreach (var tab in GuiAppState.TabOrder)
        {
            painted.Regions.Any(r => r.Result is HitResult.ButtonHit { Action: var a } && a == $"Tab:{tab}")
                .ShouldBeTrue($"{tab} has a rail cell, re-stated as its Tab:<name> button");
        }
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void TheDateLabelOpensTheCalendarOnlyWhenNoSessionRuns(uint width, float dpi)
    {
        using var renderer = new RgbaImageRenderer(width, SurfaceH);

        var idle = Paint(renderer, width, dpi, sessionRunning: false);
        idle.Regions.Any(r => r.Result is HitResult.ButtonHit { Action: "DateCalendar" }).ShouldBeTrue();
        idle.Chrome.DateCalendarAnchor.ShouldNotBeNull();

        var running = Paint(renderer, width, dpi, sessionRunning: true);
        running.Regions.Any(r => r.Result is HitResult.ButtonHit { Action: "DateCalendar" }).ShouldBeFalse();
        running.Chrome.DateCalendarAnchor.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void TheClockIsSetAndInsideTheBar(uint width, float dpi)
    {
        using var renderer = new RgbaImageRenderer(width, SurfaceH);

        var painted = Paint(renderer, width, dpi, sessionRunning: false);

        var clock = painted.Chrome.ClockRect.ShouldNotBeNull();
        clock.X.ShouldBeGreaterThanOrEqualTo(painted.Chrome.SidebarWidth);
        (clock.X + clock.Width).ShouldBeLessThanOrEqualTo(width + 0.5f);
        clock.Y.ShouldBeGreaterThanOrEqualTo(0f);
        (clock.Y + clock.Height).ShouldBeLessThanOrEqualTo(painted.Chrome.StatusBarHeight + 0.5f);
    }

    /// <summary>
    /// A clock frame repaints the bar's own pixel rows and none of the tab under it. At a display scale of 1.15 the bar is
    /// 32.2 px high: its fill paints rows 0 to 31, and row 32 is the tab's. The region is rounded out as the renderer rounds
    /// it (the origin down, the far edge up) and asked of the atlas's own rule against the area the window gives its tab:
    /// a clock frame that reached the map would have it shade the whole star field for a row that did not change.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(1.15f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(1.75f)]
    [InlineData(2f)]
    public void AClockFrameTakesNoRowOfTheTabUnderTheBar(float dpi)
    {
        const uint width = 1200;
        using var renderer = new RgbaImageRenderer(width, SurfaceH);

        var painted = Paint(renderer, width, dpi, sessionRunning: false);

        var clock = painted.Chrome.ClockRect.ShouldNotBeNull();
        (clock.Y + clock.Height).ShouldBe((int)painted.Chrome.StatusBarHeight, "one past the last row the bar's fill paints");

        var x0 = (int)MathF.Floor(clock.X);
        var y0 = (int)MathF.Floor(clock.Y);
        var x1 = (int)MathF.Ceiling(clock.X + clock.Width);
        var y1 = (int)MathF.Ceiling(clock.Y + clock.Height);
        var tab = painted.Chrome.ContentArea(width, SurfaceH);
        TianWen.UI.Shared.VkSkyMapTab.RegionMissesMap(x0, y0, (uint)(x1 - x0), (uint)(y1 - y0), tab.X, tab.Y, tab.Width, tab.Height)
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData(0f, 28f, 0f, 28f)]        // whole pixels are kept
    [InlineData(0f, 32.2f, 0f, 32f)]      // a fractional bottom gives its row up
    [InlineData(3.6f, 30f, 3f, 30f)]      // rows 3 to 32, as the fill paints them
    [InlineData(0.2f, 0.5f, 0.2f, 0.5f)]  // a box inside one row is left as it is
    public void ABoxesRowsAreTheOnesItsFillPaints(float y, float height, float rowsY, float rowsHeight)
        => GuiChromePixels.PaintedRows(new RectF32(10f, y, 100f, height)).ShouldBe(new RectF32(10f, rowsY, 100f, rowsHeight));
}
