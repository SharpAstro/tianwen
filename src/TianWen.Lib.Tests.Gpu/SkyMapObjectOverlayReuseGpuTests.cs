using System;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>Offscreen Vulkan stack for <see cref="SkyMapObjectOverlayReuseGpuTests"/>.</summary>
public sealed class VkObjectOverlayGpuFixture : OffscreenGpuFixtureBase
{
    public const int Width = 900;
    public const int Height = 700;

    public VkObjectOverlayGpuFixture() : base(Width, Height) { }
}

/// <summary>
/// <b>A frame for a view that has not moved redoes none of the object overlay's work.</b> With the Objects layer on, the
/// GUI's atlas projected every candidate, placed every label and rebuilt the marker stream for every candidate on every
/// frame: 11.5 ms of each one, 884 of the 978 slow frames in a traced session that no collection explained (2026-11-25).
/// Most frames are for a still view: a highlight following the pointer, the clock's tick. And a pan moves none of what the
/// marker stream is made from.
/// </summary>
/// <remarks>
/// Counts, not pixels: a frame that reused its items and one that did the work again are the identical picture.
/// </remarks>
[Collection(GpuCollection.Name)]
public sealed class SkyMapObjectOverlayReuseGpuTests(VkObjectOverlayGpuFixture gpu) : IClassFixture<VkObjectOverlayGpuFixture>
{
    private static readonly DateTimeOffset Instant = new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);

    private void Render(VkSkyMapTab tab, PlannerState planner, FakeTimeProviderWrapper time) => gpu.Invoke(() =>
    {
        var renderer = gpu.Renderer.ShouldNotBeNull();
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        tab.Render(planner, new RectF32(0, 0, VkObjectOverlayGpuFixture.Width, VkObjectOverlayGpuFixture.Height), time);
        renderer.EndOffscreenFrame();
        return 0;
    });

    [Fact(Timeout = 120_000)]
    public async Task AStillViewReusesThePlacedOverlayAndAPanReusesItsMarkers()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        // A planning date pins the viewing instant, so the view is the same matrix frame after frame.
        var planner = new PlannerState
        {
            ObjectDb = db, SiteLatitude = 48.0, SiteLongitude = 11.0, SiteTimeZone = TimeSpan.FromHours(1), PlanningDate = Instant,
        };
        var time = new FakeTimeProviderWrapper(Instant);
        var tab = gpu.Invoke(() => new VkSkyMapTab(gpu.Renderer.ShouldNotBeNull()) { FontPath = FontResolver.ResolveSystemFont(), DpiScale = 1f });
        Render(tab, planner, time);

        // Wide enough that the gathered set depends on neither the centre nor the field (the whole sphere), so the pan and
        // the settle below start no second gather behind the counts.
        tab.State.Mode = SkyMapMode.Equatorial;
        tab.State.CenterRA = 5.5;
        tab.State.CenterDec = 0.0;
        tab.State.FieldOfViewDeg = 100.0;
        tab.State.ShowObjectOverlay = true;

        // The gather runs on a background task and is installed by a later frame.
        while (tab.OverlayPlacements == 0)
        {
            ct.ThrowIfCancellationRequested();
            Render(tab, planner, time);
            await Task.Yield();
        }
        // One more, so the frame after the install has been drawn and the counts are at rest.
        Render(tab, planner, time);
        tab.OverlayInstanceBuilds.ShouldBeGreaterThan(0, "the overlay drew markers, or nothing below is being measured");

        var placements = tab.OverlayPlacements;
        var builds = tab.OverlayInstanceBuilds;
        for (var frame = 0; frame < 10; frame++)
        {
            Render(tab, planner, time);
        }
        tab.OverlayPlacements.ShouldBe(placements, "ten frames of the same view project and place nothing again");
        tab.OverlayInstanceBuilds.ShouldBe(builds, "and build no marker stream");

        // A pan: the items are somewhere else on screen, so they are projected and placed again, once a frame. The
        // marker stream is made of unit vectors and sizes the shader projects itself, so it is the same stream.
        for (var frame = 1; frame <= 5; frame++)
        {
            tab.State.CenterRA = 5.5 + (frame * 0.002);
            Render(tab, planner, time);
        }
        tab.OverlayPlacements.ShouldBe(placements + 5, "each frame of a pan is placed once");
        tab.OverlayInstanceBuilds.ShouldBe(builds, "a pan moves nothing the marker stream is made from");

        // A zoom changes the markers' size in pixels, which the stream carries.
        tab.State.FieldOfViewDeg = 95.0;
        Render(tab, planner, time);
        tab.OverlayInstanceBuilds.ShouldBe(builds + 1, "a zoom rebuilds it, once");
    }
}
