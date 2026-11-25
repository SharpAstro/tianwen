using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>Offscreen Vulkan stack the size of the GUI's atlas on a laptop screen, for <see cref="SkyMapPanCostProbe"/>.</summary>
public sealed class VkPanCostGpuFixture : OffscreenGpuFixtureBase
{
    public const int Width = 2900;
    public const int Height = 1700;

    public VkPanCostGpuFixture() : base(Width, Height) { }
}

/// <summary>
/// What one frame of the GPU atlas costs and allocates with the Objects layer on, for a view that is still and for a
/// pan: the two figures the traced GUI is held to (no paint over 8 ms, under 150 KB a frame, 2026-11-25), on the tab
/// alone and without a window, so a change can be read in seconds. The window's chrome is not in it
/// (<c>GuiFrameAllocationProbe</c> has that).
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class SkyMapPanCostProbe(VkPanCostGpuFixture gpu, ITestOutputHelper output) : IClassFixture<VkPanCostGpuFixture>
{
    private static readonly DateTimeOffset Instant = new DateTimeOffset(2026, 9, 17, 23, 0, 0, TimeSpan.FromHours(10));

    private (double Ms, long Bytes) Render(VkSkyMapTab tab, PlannerState planner, FakeTimeProviderWrapper time) => gpu.Invoke(() =>
    {
        var renderer = gpu.Renderer.ShouldNotBeNull();
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var mark = Stopwatch.GetTimestamp();
        tab.Render(planner, new RectF32(0, 0, VkPanCostGpuFixture.Width, VkPanCostGpuFixture.Height), time);
        var ms = Stopwatch.GetElapsedTime(mark).TotalMilliseconds;
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        renderer.EndOffscreenFrame();
        return (ms, bytes);
    });

    private static (double Median, double Most) Of(double[] values)
    {
        double[] sorted = [.. values];
        Array.Sort(sorted);
        return (sorted[sorted.Length / 2], sorted[^1]);
    }

    /// <summary>The variable that runs the probe: it prints figures and asserts next to nothing, so a run asks for it.</summary>
    internal const string Variable = "TIANWEN_FRAME_PROBE";

    [Fact(Timeout = 180_000)]
    public async Task WhatAFrameOfTheAtlasCosts()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(Variable) == "1", $"{Variable} is not 1 (a probe that prints what a frame costs)");
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var planner = new PlannerState
        {
            ObjectDb = db, SiteLatitude = -37.8, SiteLongitude = 145.0, SiteTimeZone = TimeSpan.FromHours(10), PlanningDate = Instant,
        };
        var time = new FakeTimeProviderWrapper(Instant);
        var tab = gpu.Invoke(() => new VkSkyMapTab(gpu.Renderer.ShouldNotBeNull())
        {
            FontPath = FontResolver.ResolveSystemFont(), DpiScale = 1.5f,
        });
        Render(tab, planner, time);

        // As the GUI shows it: level with the horizon, the ground hiding what is under it, an object's panel open.
        tab.State.Mode = SkyMapMode.Horizon;
        tab.State.ShowHorizon = true;
        db.TryLookupByIndex("NGC 3372", out var carina).ShouldBeTrue();
        tab.State.Search.InfoPanel = SkyMapInfoPanelData.FromCatalogObject(
            carina, planner.SiteLatitude, planner.SiteLongitude, Instant,
            TianWen.Lib.Astrometry.SOFA.SiteContext.Create(planner.SiteLatitude, planner.SiteLongitude, Instant), null, db);
        tab.State.CenterRA = 5.5;
        tab.State.CenterDec = -20.0;
        tab.State.FieldOfViewDeg = 60.0;
        tab.State.ShowObjectOverlay = true;
        while (tab.OverlayPlacements == 0)
        {
            ct.ThrowIfCancellationRequested();
            Render(tab, planner, time);
            await Task.Yield();
        }

        void Measure(string name, int frames, Action? beforeEach)
        {
            for (var i = 0; i < 20; i++)
            {
                beforeEach?.Invoke();
                Render(tab, planner, time);
            }

            var ms = new double[frames];
            var kb = new double[frames];
            var costs = tab.LayerCosts;
            var phases = tab.OverlayPhaseMs;
            var installed = tab.OverlayGathersInstalled;
            for (var i = 0; i < frames; i++)
            {
                beforeEach?.Invoke();
                var (frameMs, bytes) = Render(tab, planner, time);
                ms[i] = frameMs;
                kb[i] = bytes / 1024.0;
            }

            var spent = tab.LayerCosts.Since(costs);
            var now = tab.OverlayPhaseMs;
            var (candidates, items, labels) = tab.OverlayCounts;
            var (msMedian, msMost) = Of(ms);
            var (kbMedian, kbMost) = Of(kb);
            output.WriteLine($"{name}: {msMedian:F2} ms median, {msMost:F2} most; {kbMedian:F0} KB median, {kbMost:F0} most; {candidates} candidates, {items} on screen, {labels} labels; {tab.OverlayGathersInstalled - installed} gathers installed");
            output.WriteLine($"   a frame: map {spent.Map / frames:F2}, grid labels {spent.GridLabels / frames:F2}, names {spent.ConstellationNames / frames:F2}, planets {spent.PlanetLabels / frames:F2}, comets {spent.CometLabels / frames:F2}, objects {spent.ObjectOverlay / frames:F2} (project {(now.Project - phases.Project) / frames:F2}, place {(now.Place - phases.Place) / frames:F2}, draw {(now.Draw - phases.Draw) / frames:F2}), fixed {spent.FixedMarkers / frames:F2}, info strip {spent.InfoStrip / frames:F2}, search {spent.SearchPanel / frames:F2}");
        }

        Measure("still", 60, null);
        var ra = tab.State.CenterRA;
        Measure("pan  ", 120, () =>
        {
            ra += 0.004;
            tab.State.CenterRA = ra;
        });

        Measure("pan again", 120, () =>
        {
            ra += 0.004;
            tab.State.CenterRA = ra;
        });
        Measure("pan back ", 120, () =>
        {
            ra -= 0.004;
            tab.State.CenterRA = ra;
        });

        // A new object's panel, as a press on one opens it: the frame that opens it, and the ones after.
        var site = TianWen.Lib.Astrometry.SOFA.SiteContext.Create(planner.SiteLatitude, planner.SiteLongitude, Instant);
        foreach (var name in new[] { "M42", "M104", "NGC 5139", "M104", "M31", "M104" })
        {
            if (!db.TryLookupByIndex(name, out var obj))
            {
                continue;
            }

            tab.State.Search.InfoPanel = SkyMapInfoPanelData.FromCatalogObject(
                obj, planner.SiteLatitude, planner.SiteLongitude, Instant, site, null, db);
            var costs = tab.LayerCosts;
            var (openMs, openBytes) = Render(tab, planner, time);
            var opened = tab.LayerCosts.Since(costs);
            costs = tab.LayerCosts;
            var (nextMs, _) = Render(tab, planner, time);
            var next = tab.LayerCosts.Since(costs);
            output.WriteLine($"open {name,-9}: frame {openMs:F1} ms (panel {opened.SearchPanel:F1}), {openBytes / 1024.0:F0} KB; next frame {nextMs:F1} ms (panel {next.SearchPanel:F1})");
        }

        // The pan by layer: what a moving view allocates with one layer switched off.
        void Without(string name, Action off, Action on)
        {
            off();
            Measure($"pan without {name}", 60, () =>
            {
                ra += 0.004;
                tab.State.CenterRA = ra;
            });
            on();
        }

        Without("objects", () => tab.State.ShowObjectOverlay = false, () => tab.State.ShowObjectOverlay = true);
        Without("grid", () => tab.State.ShowGrid = false, () => tab.State.ShowGrid = true);
        Without("constellation figures", () => tab.State.ShowConstellationFigures = false, () => tab.State.ShowConstellationFigures = true);
        Without("constellation boundaries", () => tab.State.ShowConstellationBoundaries = false, () => tab.State.ShowConstellationBoundaries = true);
        Without("planets", () => tab.State.ShowPlanets = false, () => tab.State.ShowPlanets = true);
        Without("horizon", () => tab.State.ShowHorizon = false, () => tab.State.ShowHorizon = true);

        tab.OverlayPlacements.ShouldBeGreaterThan(0);
    }
}
