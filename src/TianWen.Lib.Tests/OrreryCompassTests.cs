using System;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The orrery's orientation compass (#1657, docs/plans/atlas-planet-detail.md, "A5: what is open"): the ecliptic plane as a ring, its
/// north-south axis and the vernal equinox's tick, drawn from the camera's own rotation in the view's top-right corner.
/// </summary>
[Collection("Astrometry")]
public sealed class OrreryCompassTests(ITestOutputHelper output)
{
    /// <summary>
    /// The camera keeps ecliptic north up on screen, so the axis's N end is straight above the compass's centre at every azimuth and
    /// elevation, foreshortened by the elevation (a pole seen end on from overhead is short), and toward the viewer above the plane,
    /// away below it: what tells the two sides apart.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(-90, 30)]
    [InlineData(0, 80)]
    [InlineData(45, 10)]
    [InlineData(160, -40)]
    public async Task NorthIsUpAndFacesTheViewerAboveThePlane(double azimuthDeg, double elevationDeg)
    {
        using var rig = await OpenAsync(1f);
        rig.Tab.State.Orrery.AzimuthDeg = azimuthDeg;
        rig.Tab.State.Orrery.ElevationDeg = elevationDeg;
        rig.Render();
        var compass = rig.Tab.LastOrreryCompass.ShouldNotBeNull("no compass drawn");
        var (dx, dy) = (compass.NorthX - compass.CentreX, compass.NorthY - compass.CentreY);
        var length = MathF.Sqrt((dx * dx) + (dy * dy));
        output.WriteLine($"az {azimuthDeg}, el {elevationDeg}: N at ({dx:F1}, {dy:F1}), {length:F1} of {compass.Radius:F1} px, toward the viewer {compass.NorthTowardViewer}");

        dx.ShouldBe(0f, 0.5f, "N is not straight up");
        dy.ShouldBeLessThan(0f, "N points down");
        length.ShouldBe(compass.Radius * (float)Math.Cos(elevationDeg * Math.PI / 180), 0.5f);
        compass.NorthTowardViewer.ShouldBe(elevationDeg > 0);
        (compass.SouthX - compass.CentreX).ShouldBe(-dx, 0.5f);
        (compass.SouthY - compass.CentreY).ShouldBe(-dy, 0.5f);
    }

    /// <summary>
    /// The equinox's tick turns with the camera: seen from longitude 270 (the default azimuth) longitude 0 is to the right, from 90 to
    /// the left; edge on, the ring is a line, and from overhead it is nearly a circle.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheEquinoxTurnsWithTheCameraAndTheRingWithItsTilt()
    {
        using var rig = await OpenAsync(1f);
        var orrery = rig.Tab.State.Orrery;

        (orrery.AzimuthDeg, orrery.ElevationDeg) = (-90, 30);
        rig.Render();
        var right = rig.Tab.LastOrreryCompass.ShouldNotBeNull();
        (right.EquinoxX - right.CentreX).ShouldBeGreaterThan(right.Radius * 0.9f, "longitude 0 is not to the right");

        (orrery.AzimuthDeg, orrery.ElevationDeg) = (90, 30);
        rig.Render();
        var left = rig.Tab.LastOrreryCompass.ShouldNotBeNull();
        (left.EquinoxX - left.CentreX).ShouldBeLessThan(-left.Radius * 0.9f, "longitude 0 is not to the left");

        (orrery.AzimuthDeg, orrery.ElevationDeg) = (0, 0);
        rig.Render();
        rig.Tab.LastOrreryCompass.ShouldNotBeNull().RingMinorRadius.ShouldBe(0f, 0.5f, "edge on, the ring is not a line");

        (orrery.AzimuthDeg, orrery.ElevationDeg) = (0, 89);
        rig.Render();
        var top = rig.Tab.LastOrreryCompass.ShouldNotBeNull();
        top.RingMinorRadius.ShouldBe(top.Radius, 0.1f * top.Radius, "from overhead, the ring is not round");
    }

    /// <summary>The compass sits inside the view's top-right corner, clear of the strip, at 1x and 1.5x.</summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheCompassIsInsideTheView(float dpiScale)
    {
        using var rig = await OpenAsync(dpiScale);
        var box = rig.Tab.LastOrreryCompass.ShouldNotBeNull().Bounds;
        output.WriteLine($"{dpiScale}x: the compass at ({box.X:F0}, {box.Y:F0}) {box.Width:F0} x {box.Height:F0}");
        box.X.ShouldBeGreaterThan(OrreryTests.Width / 2f);
        box.Y.ShouldBeGreaterThanOrEqualTo(0);
        (box.X + box.Width).ShouldBeLessThanOrEqualTo(OrreryTests.Width);
        (box.Y + box.Height).ShouldBeLessThan(OrreryTests.Height / 2f);
    }

    private static async Task<OrreryTests.Rig> OpenAsync(float dpiScale)
    {
        var rig = await OrreryTests.RigAsync(TestContext.Current.CancellationToken, dpiScale);
        OrreryTests.Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        await OrreryTests.SettleAsync(rig);
        return rig;
    }
}
