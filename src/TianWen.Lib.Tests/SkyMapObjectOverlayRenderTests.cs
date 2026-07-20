using System;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Offline render test for the shared CPU-primitive object-overlay path
/// (<c>SkyMapTab.RenderObjectOverlayPrimitive</c>) that the browser sky map uses in place of the
/// desktop GPU instanced-ellipse pipeline. Rendered over the CPU <see cref="RgbaImageRenderer"/> — no
/// GPU/device — so it pins that the [O] overlay actually draws markers + labels (the net-new code), by
/// diffing an overlay-off frame against an overlay-on frame over a DSO-dense region.
/// </summary>
[Collection("Astrometry")]
public sealed class SkyMapObjectOverlayRenderTests
{
    // A SkyMapTab over the CPU surface that routes the object overlay through the shared primitive path
    // (exactly what WebSkyMapTab does over WebGL) and publishes the view matrix each frame the way the
    // real GPU pipelines do — so the overlay + label projections have a matrix consistent with the centre.
    private sealed class OverlayTestSkyMapTab(RgbaImageRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect, string fontPath,
            DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site)
        {
            base.RenderSkyMap(db, contentRect, fontPath, viewingTime, siteLat, siteLon, site); // background fill
            State.CurrentViewMatrix = State.ComputeViewMatrix();
        }

        protected override void RenderObjectOverlay(
            ICelestialObjectDB db, RectF32 contentRect, float dpiScale, string fontPath,
            float baseFontSize, SiteContext site, bool dimBelowHorizon, PlannerState plannerState,
            bool showAllOverlays)
            => RenderObjectOverlayPrimitive(db, contentRect, dpiScale, fontPath, baseFontSize,
                site, dimBelowHorizon, plannerState, showAllOverlays);
    }

    [Fact]
    public async Task ObjectOverlay_Primitive_DrawsMarkersAndLabels_OverDenseRegion()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

        const int w = 900, h = 900;
        using var renderer = new RgbaImageRenderer(w, h);
        var tab = new OverlayTestSkyMapTab(renderer);
        var fontPath = FontResolver.ResolveSystemFont();

        // A fixed winter night at a mid-northern site so the sky is dark and positions are deterministic.
        var state = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = 48.0,
            SiteLongitude = 11.0,
            SiteTimeZone = TimeSpan.FromHours(1),
            PlanningDate = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.FromHours(1)),
        };
        var time = new FakeTimeProviderWrapper(state.PlanningDate.Value);
        var content = new RectF32(0, 0, w, h);

        // First render initialises the view to the celestial pole; then aim at the Sagittarius Milky Way
        // (RA 18h, Dec -24 deg) — packed with Messier nebulae / clusters — at a wide FOV, overlay OFF.
        tab.State.ShowObjectOverlay = false;
        tab.Render(state, content, 1f, fontPath, time);

        tab.State.CenterRA = 18.0;
        tab.State.CenterDec = -24.0;
        tab.State.FieldOfViewDeg = 30.0;

        tab.Render(state, content, 1f, fontPath, time);
        var off = (byte[])renderer.Surface.Pixels.Clone();

        // Same view, overlay ON. Only the [O] catalog markers + labels differ between the two frames,
        // so the pixel diff IS the overlay footprint.
        tab.State.ShowObjectOverlay = true;
        tab.Render(state, content, 1f, fontPath, time);
        var on = renderer.Surface.Pixels;

        File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "skymap-overlay-on.png"),
            PngWriter.Encode(on, renderer.Surface.Width, renderer.Surface.Height));

        long changed = 0;
        for (var i = 0; i + 3 < on.Length && i + 3 < off.Length; i += 4)
        {
            if (on[i] != off[i] || on[i + 1] != off[i + 1] || on[i + 2] != off[i + 2])
            {
                changed++;
            }
        }

        // A DSO-dense 30-degree field draws many marker ellipses/crosses + label glyphs; the overlay
        // footprint is well into the thousands of pixels. 500 cleanly separates "the overlay drew" from
        // "nothing changed" (the pre-wiring web behaviour, where the base RenderObjectOverlay is a no-op).
        changed.ShouldBeGreaterThan(500,
            "enabling the [O] overlay drew too few pixels — the primitive overlay path may not be rendering");
    }
}
