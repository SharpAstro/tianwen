using System;
using System.Linq;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Grid scale selection, shared by the Vulkan and WebGL pipelines. The scales are COMPLEMENTARY:
/// <c>BuildGridLines</c> omits every line a coarser scale already draws, so a renderer has to stack
/// the active scales, and gating one off deletes its lines rather than thinning the grid. The two
/// pipelines used to carry their own rule and the browser's had a lower FOV bound, which is why its
/// grid lost the celestial equator and the principal meridians as soon as you zoomed in.
/// </summary>
public class SkyMapGridScaleTests
{
    // The zoom clamp in SkyMapTab.HandleZoomByFactor: every FOV a user can actually reach.
    private const double MinReachableFov = 0.5;
    private const double MaxReachableFov = 180.0;

    // Scale 0 (6h RA / 30 deg Dec) is the one carrying the anchor lines: Dec 0 (the celestial
    // equator), +/-30, +/-60, and RA 0h / 6h / 12h / 18h.
    private const int AnchorScale = 0;

    private static double[] ReachableFovs() =>
    [
        MinReachableFov, 0.7, 1.0, 2.0, 3.0, 5.0, 9.9, 10.0, 15.0, 20.0, 29.9, 30.0,
        45.0, 60.0, 90.0, 120.0, 150.0, MaxReachableFov,
    ];

    [Fact]
    public void AnchorScale_IsActiveAtEveryReachableFov()
    {
        // The regression: with a "fov >= minFov" gate, scale 0 switched OFF below 30 degrees, taking
        // the equator and the 6h meridians with it. There is no lower bound by design; minFov only
        // says where the fade begins.
        foreach (var fov in ReachableFovs())
        {
            SkyMapGpuGeometry.TryGetGridFade(AnchorScale, fov, out var fade)
                .ShouldBeTrue($"the anchor grid scale must stay on at {fov} deg FOV");
            fade.ShouldBeGreaterThan(0.0);
        }
    }

    [Fact]
    public void SomeScaleIsAlwaysActive()
    {
        foreach (var fov in ReachableFovs())
        {
            var active = Enumerable.Range(0, SkyMapGpuGeometry.GridScales.Length)
                .Count(i => SkyMapGpuGeometry.TryGetGridFade(i, fov, out _));
            active.ShouldBeGreaterThan(0, $"no grid scale draws at {fov} deg FOV, so there is no grid");
        }
    }

    [Fact]
    public void FinerScales_TurnOffWhenTheViewIsTooWide()
    {
        // The other direction has to keep working: a 10-arcminute grid at a 60 degree field would be
        // a solid wash, so scales fade out and then drop as the view widens.
        SkyMapGpuGeometry.TryGetGridFade(4, 60.0, out _).ShouldBeFalse();
        SkyMapGpuGeometry.TryGetGridFade(3, 60.0, out _).ShouldBeFalse();
        SkyMapGpuGeometry.TryGetGridFade(2, 20.0, out _).ShouldBeTrue();
        SkyMapGpuGeometry.TryGetGridFade(2, 60.0, out _).ShouldBeFalse();
    }

    [Fact]
    public void Fade_IsFullWhenZoomedIn_AndDecreasesTowardsMaxFov()
    {
        // Scale 1 spans 10..120 deg, so it is at full alpha below 20 and fades from there.
        SkyMapGpuGeometry.TryGetGridFade(1, 5.0, out var zoomedIn).ShouldBeTrue();
        zoomedIn.ShouldBe(1.0, 1e-9);

        SkyMapGpuGeometry.TryGetGridFade(1, 60.0, out var mid).ShouldBeTrue();
        SkyMapGpuGeometry.TryGetGridFade(1, 110.0, out var wide).ShouldBeTrue();
        mid.ShouldBeLessThan(1.0);
        wide.ShouldBeLessThan(mid);
    }

    [Fact]
    public void GridColorAt_ScalesAlphaOnly()
    {
        var full = SkyMapGpuGeometry.GridColorAt(1.0);
        var half = SkyMapGpuGeometry.GridColorAt(0.5);

        full.ShouldBe(SkyMapGpuGeometry.GridLineColor);
        (half.Red, half.Green, half.Blue).ShouldBe((full.Red, full.Green, full.Blue));
        Math.Abs(half.Alpha - full.Alpha / 2).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void EveryScaleIsReachable()
    {
        // A scale nothing can ever activate would be dead geometry uploaded to the GPU every run.
        for (var i = 0; i < SkyMapGpuGeometry.GridScales.Length; i++)
        {
            var reachable = ReachableFovs().Any(f => SkyMapGpuGeometry.TryGetGridFade(i, f, out _));
            reachable.ShouldBeTrue($"grid scale {i} never activates within the zoom clamp");
        }
    }
}
