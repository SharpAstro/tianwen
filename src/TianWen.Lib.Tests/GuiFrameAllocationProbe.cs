using System;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What one frame of the GUI allocates, by part. The traced app said the render thread allocates about half a megabyte a
/// frame, which at 60 fps is a generation-0 collection three or four times a second and a pause of 7 to 11 ms each
/// (2026-11-25): the slow frames of a pan. This says WHERE, on a CPU surface, which shares every widget, layout and
/// label path with the GPU build and differs only in the map's own draw.
/// </summary>
[Collection("Astrometry")]
public sealed class GuiFrameAllocationProbe(ITestOutputHelper output)
{
    private const uint Width = 1600, Height = 1000;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 17, 23, 0, 0, TimeSpan.FromHours(2));

    private sealed class ProbeSkyMapTab(RgbaImageRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect,
            DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site,
            SkyMapDrawPhase phase = SkyMapDrawPhase.All)
        {
            // The map's own draw is the GPU build's business (a few draw calls there, a CPU rasteriser here): left out, so
            // what is measured is what the two builds share. The view matrix is published as the pipelines publish it.
            State.CurrentViewMatrix = State.ComputeViewMatrix();
        }
    }

    /// <summary>Bytes the calling thread allocated in <paramref name="frame"/>, the least and the median of several.</summary>
    private static (long Least, long Median) Measure(Action frame, int warm = 8, int passes = 15)
    {
        for (var i = 0; i < warm; i++)
        {
            frame();
        }

        var readings = new long[passes];
        for (var i = 0; i < passes; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            frame();
            readings[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Array.Sort(readings);
        return (readings[0], readings[passes / 2]);
    }

    /// <summary>The variable that runs the probe: it prints figures and asserts next to nothing, so a run asks for it.</summary>
    internal const string Variable = "TIANWEN_FRAME_PROBE";

    [Fact]
    public async Task WhatAFrameAllocates()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(Variable) == "1", $"{Variable} is not 1 (a probe that prints what a frame allocates)");
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var font = FontResolver.ResolveSystemFont();
        using var renderer = new RgbaImageRenderer(Width, Height);
        var time = new FakeTimeProviderWrapper(Now);

        // The chrome: sidebar, top bar with the clock, status bar.
        var chrome = new GuiWindowChrome<RgbaImage>(renderer) { FontPath = font, DpiScale = 1.5f };
        chrome.Sidebar.FontPath = font;
        chrome.Sidebar.DpiScale = 1.5f;
        chrome.NavigationStrip.FontPath = font;
        chrome.NavigationStrip.DpiScale = 1.5f;
        var appState = new GuiAppState
        {
            ActiveProfile = new Profile(Guid.NewGuid(), "Test", ProfileData.Empty),
            StatusMessage = "Found 90 devices",
            ActiveTab = GuiTab.SkyMap,
        };
        var planner = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = -37.8,
            SiteLongitude = 145.0,
            SiteTimeZone = TimeSpan.FromHours(11),
            PlanningDate = Now,
        };
        var local = new LiveSessionState();
        var equipment = new EquipmentTabState();
        var session = new SessionTabState();

        var tab = new ProbeSkyMapTab(renderer) { FontPath = font, DpiScale = 1.5f };
        var content = new RectF32(96f, 48f, Width - 96f, Height - 90f);
        tab.Render(planner, content, time);
        tab.State.Mode = SkyMapMode.Horizon;
        tab.State.FieldOfViewDeg = 60.0;

        var chromeBytes = Measure(() => chrome.Render(appState, planner, time, local, local, equipment, session, tab.State, Width, Height));
        var still = Measure(() => tab.Render(planner, content, time));
        var ra = tab.State.CenterRA;
        var panning = Measure(() =>
        {
            ra = (ra + 0.01) % 24.0;
            tab.State.CenterRA = ra;
            tab.Render(planner, content, time);
        });

        output.WriteLine($"chrome            least {chromeBytes.Least,9:N0}  median {chromeBytes.Median,9:N0} bytes a frame");
        output.WriteLine($"atlas, still view least {still.Least,9:N0}  median {still.Median,9:N0}");
        output.WriteLine($"atlas, panning    least {panning.Least,9:N0}  median {panning.Median,9:N0}");

        // The atlas by layer, on a still view: each layer's share of the frame's allocations, from two frames that differ
        // by one layer being switched off.
        void Layer(string name, Action off, Action on)
        {
            off();
            var without = Measure(() => tab.Render(planner, content, time), warm: 4, passes: 9);
            on();
            output.WriteLine($"   {name,-28} {still.Median - without.Median,9:N0} bytes");
        }

        Layer("grid + its labels", () => tab.State.ShowGrid = false, () => tab.State.ShowGrid = true);
        Layer("layer palette", () => tab.State.ShowLayerPalette = false, () => tab.State.ShowLayerPalette = true);
        Layer("planets", () => tab.State.ShowPlanets = false, () => tab.State.ShowPlanets = true);
        Layer("comets", () => tab.State.ShowComets = false, () => tab.State.ShowComets = true);
        Layer("constellation boundaries", () => tab.State.ShowConstellationBoundaries = false, () => tab.State.ShowConstellationBoundaries = true);
        Layer("constellation figures", () => tab.State.ShowConstellationFigures = false, () => tab.State.ShowConstellationFigures = true);

        (chromeBytes.Least + still.Least).ShouldBeGreaterThan(0);
    }
}
