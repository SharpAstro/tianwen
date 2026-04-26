using DIR.Lib;
using Shouldly;
using System;
using TianWen.Lib.Astrometry;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pure projection tests for <see cref="WcsAnnotationLayer"/>: verify that
    /// sky positions and ring radii project to the right screen pixels through
    /// a known WCS + viewport, without involving any concrete renderer.
    /// </summary>
    [Collection("Astrometry")]
    public class WcsAnnotationLayerTests
    {
        // Known-good WCS for a 1000x800 sensor centred on (RA=12h, Dec=45deg) at 1"/px.
        // Reference pixel = image centre. North up, east left (standard astronomy
        // convention) — in image-pixel space, pixel Y increases downward, so for
        // "north up on screen" we want +Dec to map to *decreasing* pixel Y, i.e.
        // CD2_2 < 0. East = +RA on the sky direction; with pixel X increasing
        // rightward, "east left" means +RA -> *decreasing* pixel X, i.e. CD1_1 < 0.
        private static WCS BuildTestWcs()
        {
            const double pixelScaleArcsec = 1.0;
            const double pixelScaleDeg = pixelScaleArcsec / 3600.0;
            return new WCS(CenterRA: 12.0, CenterDec: 45.0)
            {
                CRPix1 = 500.5, CRPix2 = 400.5,
                CD1_1 = -pixelScaleDeg,
                CD1_2 = 0,
                CD2_1 = 0,
                CD2_2 = -pixelScaleDeg,
            };
        }

        // Standard layout: 1280x960 window, image fills the area unscaled (Zoom=1, no pan).
        // Toolbar/file-list/info-panel margins zeroed for predictable pixel math.
        private static ViewportLayout BuildTestLayout(float zoom = 1f, float panX = 0f, float panY = 0f)
        {
            const int imageW = 1000, imageH = 800;
            return new ViewportLayout(
                WindowWidth: 1280f, WindowHeight: 960f,
                ImageWidth: imageW, ImageHeight: imageH,
                Zoom: zoom, PanOffset: (panX, panY),
                AreaLeft: 0f, AreaTop: 0f,
                AreaWidth: 1280f, AreaHeight: 960f,
                DpiScale: 1f);
        }

        [Fact]
        public void GivenMarkerAtWcsCenterWhenProjectThenLandsAtImageCentre()
        {
            var wcs = BuildTestWcs();
            var layout = BuildTestLayout();

            var marker = new SkyMarker(12.0, 45.0, SkyMarkerGlyph.Cross, default, null, 12f);
            var placement = WcsAnnotationLayer.ProjectMarker(marker, wcs, layout);

            placement.ShouldNotBeNull();
            // ImageOffsetX = 0 + (1280 - 1000)/2 + 0 = 140; centre pixel CRPix1 = 500.5.
            // Screen X = 140 + 500.5 * 1.0 = 640.5.
            placement!.Value.ScreenX.ShouldBe(640.5f, 0.01f);
            // ImageOffsetY = 0 + (960 - 800)/2 + 0 = 80; CRPix2 = 400.5 -> 80 + 400.5 = 480.5.
            placement.Value.ScreenY.ShouldBe(480.5f, 0.01f);
            placement.Value.IsOnScreen.ShouldBeTrue();
        }

        [Fact]
        public void GivenMarkerOffByOneArcminInDecWhenProjectThenLandsSixtyPixelsAbove()
        {
            // 1"/px scale, so 1' = 60 pixels. Higher Dec -> further up on screen
            // (CD2_2 positive in test setup), so screen Y decreases by 60.
            var wcs = BuildTestWcs();
            var layout = BuildTestLayout();

            var marker = new SkyMarker(12.0, 45.0 + 1.0 / 60.0, SkyMarkerGlyph.Cross, default, null, 12f);
            var placement = WcsAnnotationLayer.ProjectMarker(marker, wcs, layout);

            placement.ShouldNotBeNull();
            placement!.Value.ScreenX.ShouldBe(640.5f, 0.5f);
            placement.Value.ScreenY.ShouldBe(420.5f, 0.5f);   // 480.5 - 60
        }

        [Fact]
        public void GivenZoomTwoWhenProjectMarkerThenScreenOffsetDoubles()
        {
            var wcs = BuildTestWcs();
            var layout = BuildTestLayout(zoom: 2f);

            var marker = new SkyMarker(12.0, 45.0 + 1.0 / 60.0, SkyMarkerGlyph.Cross, default, null, 12f);
            var placement = WcsAnnotationLayer.ProjectMarker(marker, wcs, layout);

            placement.ShouldNotBeNull();
            // At zoom 2, image area is 2000x1600. ImageOffsetX = 0 + (1280-2000)/2 = -360,
            // so centre is at -360 + 500.5*2 = 641. 1 arcmin = 60 image-px = 120 screen-px.
            placement!.Value.ScreenX.ShouldBe(641f, 1f);
            // Screen Y centre: 0 + (960-1600)/2 + 400.5*2 = -320 + 801 = 481.
            // 1' offset -> -120 screen px -> 361.
            placement.Value.ScreenY.ShouldBe(361f, 1f);
        }

        [Fact]
        public void GivenRingAtCenterFiveArcminWhenProjectThenRadiusMatchesPixelScale()
        {
            // 5' at 1"/px = 300 image pixels = 300 screen px at zoom 1.
            var wcs = BuildTestWcs();
            var layout = BuildTestLayout();

            var ring = new SkyRing(12.0, 45.0, 5.0f, default, null);
            var placement = WcsAnnotationLayer.ProjectRing(ring, wcs, layout);

            placement.ShouldNotBeNull();
            placement!.Value.RadiusScreenPx.ShouldBe(300f, 0.5f);
            placement.Value.IsOnScreen.ShouldBeTrue();
        }

        [Fact]
        public void GivenRingAtZoomTwoWhenProjectThenRadiusDoubles()
        {
            var wcs = BuildTestWcs();
            var layout = BuildTestLayout(zoom: 2f);

            var ring = new SkyRing(12.0, 45.0, 5.0f, default, null);
            var placement = WcsAnnotationLayer.ProjectRing(ring, wcs, layout);

            placement.ShouldNotBeNull();
            placement!.Value.RadiusScreenPx.ShouldBe(600f, 1f);
        }

        [Fact]
        public void GivenMarkerOffSensorWhenProjectThenIsOnScreenFalse()
        {
            var wcs = BuildTestWcs();
            var layout = BuildTestLayout();

            // 30 arcmin off centre in Dec — well outside the 800-pixel sensor (which spans
            // ~13.3' top-to-bottom at 1"/px).
            var marker = new SkyMarker(12.0, 45.0 + 30.0 / 60.0, SkyMarkerGlyph.Cross, default, null, 12f);
            var placement = WcsAnnotationLayer.ProjectMarker(marker, wcs, layout);

            placement.ShouldNotBeNull();
            placement!.Value.IsOnScreen.ShouldBeFalse();
        }

        [Fact]
        public void GivenMarkerNearlyOppositePoleWhenProjectThenReturnsNull()
        {
            // Position more than 90 deg from the WCS centre — behind the tangent plane.
            var wcs = BuildTestWcs();
            var layout = BuildTestLayout();

            // Centre is (RA=12h, Dec=45). Antipode-ish: (RA=0h, Dec=-45).
            var marker = new SkyMarker(0.0, -45.0, SkyMarkerGlyph.Cross, default, null, 12f);
            var placement = WcsAnnotationLayer.ProjectMarker(marker, wcs, layout);

            placement.ShouldBeNull();
        }

        [Fact]
        public void GivenAnnotationEmptyHelperThenIsEmpty()
        {
            WcsAnnotation.Empty.IsEmpty.ShouldBeTrue();
            new WcsAnnotation([], []).IsEmpty.ShouldBeTrue();
        }

        [Fact]
        public void GivenAnnotationWithMarkerThenNotEmpty()
        {
            var color = new RGBAColor32(255, 255, 255, 255);
            var anno = new WcsAnnotation(
                Markers: [new SkyMarker(12, 45, SkyMarkerGlyph.Cross, color, null, 12f)],
                Rings: []);
            anno.IsEmpty.ShouldBeFalse();
        }
    }
}
