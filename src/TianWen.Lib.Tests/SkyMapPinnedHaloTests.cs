using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The pinned planner-target halo must hug an extended object's SHAPE, not circle its major axis.
/// Asserted on the traced rings rather than on pixels: the halo used to be a
/// <c>DrawCircle</c> sized from <c>SemiMajArcmin</c> alone, so a pinned edge-on galaxy wore a halo
/// far wider than itself. It is now the same ellipse trace as the marker, scaled uniformly, which
/// makes the check exact (the two rings must be similar figures) instead of approximate.
/// </summary>
[Collection("Astrometry")]
public sealed class SkyMapPinnedHaloTests
{
    // The two colours the pinned overlay draws with (SkyMapTab.ObjectOverlay): halo then marker.
    private static readonly (byte R, byte G, byte B) HaloRgb = (0xFF, 0x60, 0x20);
    private static readonly (byte R, byte G, byte B) MarkerRgb = (0xFF, 0x70, 0x30);

    /// <summary>
    /// Records every traced polyline's colour and its extreme radii about its own centroid, and
    /// still draws it. Radii, not a bounding box: the box of a rotated ellipse is near-square at
    /// PA 45, so it cannot see elongation, while the longest and shortest sampled radius recover
    /// the semi-axes at any position angle.
    /// </summary>
    private sealed class RingCapturingRenderer(uint w, uint h) : RgbaImageRenderer(w, h)
    {
        public List<(RGBAColor32 Color, float SemiMajor, float SemiMinor)> Rings { get; } = [];

        public override void DrawPolyline(ReadOnlySpan<(float X, float Y)> points, RGBAColor32 color, int thickness = 1)
        {
            if (points.Length > 2)
            {
                float sumX = 0, sumY = 0;
                foreach (var (x, y) in points)
                {
                    sumX += x;
                    sumY += y;
                }
                var cx = sumX / points.Length;
                var cy = sumY / points.Length;

                float rMax = 0f, rMin = float.MaxValue;
                foreach (var (x, y) in points)
                {
                    var r = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    rMax = MathF.Max(rMax, r);
                    rMin = MathF.Min(rMin, r);
                }
                Rings.Add((color, rMax, rMin));
            }
            base.DrawPolyline(points, color, thickness);
        }
    }

    private sealed class HaloTestSkyMapTab(RingCapturingRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect,
            DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site)
        {
            base.RenderSkyMap(db, contentRect, viewingTime, siteLat, siteLon, site);
            State.CurrentViewMatrix = State.ComputeViewMatrix();
        }

        protected override void RenderObjectOverlay(
            ICelestialObjectDB db, RectF32 contentRect,
            float baseFontSize, SiteContext site, bool dimBelowHorizon, PlannerState plannerState,
            bool showAllOverlays)
            => RenderObjectOverlayPrimitive(db, contentRect, baseFontSize,
                site, dimBelowHorizon, plannerState, showAllOverlays);
    }

    [Fact]
    public async Task PinnedExtendedObject_HaloIsTheMarkerEllipseScaledUniformly()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

        // NGC 4565, the Needle Galaxy: about 16' x 2', so a circular halo sized from the major axis
        // is roughly eight times too wide. An object this elongated makes the "same shape" claim
        // mean something.
        CatalogUtils.TryGetCleanedUpCatalogName("NGC4565", out var needle).ShouldBeTrue();
        db.TryLookupByIndex(needle, out var obj).ShouldBeTrue("the test object must be in the catalog");

        const int w = 800, h = 800;
        using var renderer = new RingCapturingRenderer(w, h);
        var tab = new HaloTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };

        var state = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = 48.0,
            SiteLongitude = 11.0,
            SiteTimeZone = TimeSpan.FromHours(1),
            PlanningDate = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.FromHours(1)),
            // Pinning is what turns the halo on, and the pin is matched by CatalogIndex.
            Proposals = [new ProposedObservation(new Target(obj.RA, obj.Dec, "NGC 4565", needle))],
        };
        var time = new FakeTimeProviderWrapper(state.PlanningDate.Value);
        var content = new RectF32(0, 0, w, h);

        // Both catalog layers OFF, so the pinned target is the ONLY thing the overlay draws (a pin
        // bypasses both gates). Without this the field's other galaxies trace rings too.
        tab.State.ShowObjectOverlay = false;
        tab.State.ShowDarkNebulae = false;
        tab.Render(state, content, time);

        // Centre the view on the galaxy at a narrow FOV, so its projected size clears the halo's
        // 16 px floor and the halo is a pure 1.5x of the marker.
        tab.State.CenterRA = obj.RA;
        tab.State.CenterDec = obj.Dec;
        tab.State.FieldOfViewDeg = 1.0;
        renderer.Rings.Clear();
        tab.Render(state, content, time);

        var halo = renderer.Rings.Where(r => (r.Color.Red, r.Color.Green, r.Color.Blue) == HaloRgb)
            .ShouldHaveSingleItem("the pinned halo should be traced as an ellipse (it used to be a DrawCircle)");
        var marker = renderer.Rings.Where(r => (r.Color.Red, r.Color.Green, r.Color.Blue) == MarkerRgb)
            .ShouldHaveSingleItem("the pinned marker itself should trace one ellipse");

        // Sanity first: the marker really is an elongated ellipse on screen, so the ratio check
        // below has something to catch. A circular halo is only wrong for an elongated object.
        (marker.SemiMajor / marker.SemiMinor).ShouldBeGreaterThan(2f,
            "NGC 4565 should project as a clearly elongated ellipse");

        // A uniform scale maps one ellipse onto a SIMILAR figure, so the halo/marker ratio is the
        // same on both axes. That is the property a circular halo cannot have: it sits at the
        // major-axis radius on both, blowing the minor ratio out by the object's whole axis ratio.
        var ratioMajor = halo.SemiMajor / marker.SemiMajor;
        var ratioMinor = halo.SemiMinor / marker.SemiMinor;
        ratioMinor.ShouldBe(ratioMajor, 0.02f * ratioMajor,
            "halo and marker must be similar figures, i.e. the halo keeps the object's axis ratio");
        ratioMajor.ShouldBe(TianWen.UI.Abstractions.Overlays.OverlayEngine.PinnedHaloScale, 0.05f,
            "past the pixel floor the halo is exactly the shared PinnedHaloScale");
    }
}
