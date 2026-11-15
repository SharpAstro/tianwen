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
    private const uint SurfaceH = 600;

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
}
