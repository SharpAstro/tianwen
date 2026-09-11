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
        // convention): in image-pixel space, pixel Y increases downward, so for
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
            // ImageOffsetX = 0 + (1280 - 1000)/2 + 0 = 140. CRPix1 = 500.5 is a frame coordinate in
            // which the centre of pixel i is i, so it names the boundary between pixels 500 and 501;
            // on screen pixel 500 spans [140 + 500, 140 + 501), and that boundary is at
            // 140 + (500.5 + 0.5) * 1.0 = 641.
            placement!.Value.ScreenX.ShouldBe(641f, 0.01f);
            // ImageOffsetY = 0 + (960 - 800)/2 + 0 = 80; CRPix2 = 400.5 -> 80 + (400.5 + 0.5) = 481.
            placement.Value.ScreenY.ShouldBe(481f, 0.01f);
            placement.Value.IsOnScreen.ShouldBeTrue();
        }

        /// <summary>
        /// The rule itself, stated once: the centre of pixel <c>i</c> is <c>i</c> in the frame and the
        /// middle of the <c>i</c>-th zoomed cell on screen, so a frame coordinate lands at
        /// <c>origin + (i + 0.5) * zoom</c>; the inverse takes it back; and a pixel index owns the
        /// half-open band <c>[i - 0.5, i + 0.5)</c>.
        /// </summary>
        /// <remarks>
        /// Every WCS-drawn thing in the viewer goes through these two functions, which is what made
        /// the bug they replace worth a test of its own: until 2026-09-11 the catalogue markers, the
        /// grid, the selection ring and both readouts each carried a private copy of this arithmetic,
        /// written when a WCS answered 1-based, and the 2026-09-05 fix that made it 0-based could not
        /// reach any of them. The end-to-end pin against the picture is
        /// <c>ViewerObjectSelectionTests.TheRingIsDrawnWhereTheStarsLightIs</c>; this is the unit.
        /// </remarks>
        [Fact]
        public void AFrameCoordinateLandsInTheMiddleOfItsZoomedCellAndComesBack()
        {
            var layout = BuildTestLayout(zoom: 4f);
            var originX = layout.ImageOffsetX;
            var originY = layout.ImageOffsetY;

            // Pixel 0's cell is [origin, origin + 4); its centre, frame coordinate 0, is origin + 2.
            var (sx, sy) = WcsAnnotationLayer.ImageToScreen(0.0, 0.0, layout);
            sx.ShouldBe(originX + 2.0, 1e-9);
            sy.ShouldBe(originY + 2.0, 1e-9);

            // A detected centroid a third of a pixel into pixel 7 sits a third of a cell into cell 7.
            var (cx, cy) = WcsAnnotationLayer.ImageToScreen(7.333, 2.0, layout);
            cx.ShouldBe(originX + ((7.333 + 0.5) * 4.0), 1e-9);
            cy.ShouldBe(originY + ((2.0 + 0.5) * 4.0), 1e-9);

            // And back, exactly.
            var (ix, iy) = WcsAnnotationLayer.ScreenToImage(cx, cy, layout);
            ix.ShouldBe(7.333, 1e-9);
            iy.ShouldBe(2.0, 1e-9);

            // The pixel a continuous coordinate falls in: the band is centred on the integer.
            WcsAnnotationLayer.PixelIndex(7.333).ShouldBe(7);
            WcsAnnotationLayer.PixelIndex(6.5).ShouldBe(7, "a tie goes up: 6.5 is where pixel 7 begins");
            WcsAnnotationLayer.PixelIndex(6.4999).ShouldBe(6);
            WcsAnnotationLayer.PixelIndex(-0.5).ShouldBe(0, "the raster begins half a pixel before its first centre");
            WcsAnnotationLayer.PixelIndex(-0.5001).ShouldBe(-1);

            // The sensor's extent in the same frame: [-0.5, Width - 0.5) on each axis.
            WcsAnnotationLayer.IsOnImage(-0.5, -0.5, layout).ShouldBeTrue();
            WcsAnnotationLayer.IsOnImage(-0.5001, 0.0, layout).ShouldBeFalse();
            WcsAnnotationLayer.IsOnImage(999.4999, 799.4999, layout).ShouldBeTrue();
            WcsAnnotationLayer.IsOnImage(999.5, 0.0, layout).ShouldBeFalse();
        }

        /// <summary>
        /// A layout handed the placement the picture was drawn at uses it, and derives nothing: the
        /// viewer passes its own origin so a display crop moves every overlay with the quad.
        /// </summary>
        [Fact]
        public void AnExplicitOriginBeatsTheDerivedOne()
        {
            var derived = BuildTestLayout(zoom: 2f);
            var placed = derived with { ImageOrigin = (-1234.5f, 77f) };

            derived.ImageOffsetX.ShouldBe(-360f, 1e-3f, "the centred uncropped geometry, as before");
            placed.ImageOffsetX.ShouldBe(-1234.5f);
            placed.ImageOffsetY.ShouldBe(77f);

            var (sx, sy) = WcsAnnotationLayer.ImageToScreen(10.0, 20.0, placed);
            sx.ShouldBe(-1234.5 + ((10.0 + 0.5) * 2.0), 1e-9);
            sy.ShouldBe(77.0 + ((20.0 + 0.5) * 2.0), 1e-9);
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
            placement!.Value.ScreenX.ShouldBe(641f, 0.5f);
            placement.Value.ScreenY.ShouldBe(421f, 0.5f);   // 481 - 60
        }

        [Fact]
        public void GivenZoomTwoWhenProjectMarkerThenScreenOffsetDoubles()
        {
            var wcs = BuildTestWcs();
            var layout = BuildTestLayout(zoom: 2f);

            var marker = new SkyMarker(12.0, 45.0 + 1.0 / 60.0, SkyMarkerGlyph.Cross, default, null, 12f);
            var placement = WcsAnnotationLayer.ProjectMarker(marker, wcs, layout);

            placement.ShouldNotBeNull();
            // At zoom 2, image area is 2000x1600. ImageOffsetX = 0 + (1280-2000)/2 = -360, and the
            // reference pixel's centre is a cell and a half beyond pixel 500's left edge:
            // -360 + (500.5 + 0.5) * 2 = 642. 1 arcmin = 60 image-px = 120 screen-px.
            placement!.Value.ScreenX.ShouldBe(642f, 1f);
            // Screen Y centre: 0 + (960-1600)/2 + (400.5 + 0.5) * 2 = -320 + 802 = 482.
            // 1' offset -> -120 screen px -> 362.
            placement.Value.ScreenY.ShouldBe(362f, 1f);
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

            // 30 arcmin off centre in Dec: well outside the 800-pixel sensor (which spans
            // ~13.3' top-to-bottom at 1"/px).
            var marker = new SkyMarker(12.0, 45.0 + 30.0 / 60.0, SkyMarkerGlyph.Cross, default, null, 12f);
            var placement = WcsAnnotationLayer.ProjectMarker(marker, wcs, layout);

            placement.ShouldNotBeNull();
            placement!.Value.IsOnScreen.ShouldBeFalse();
        }

        [Fact]
        public void GivenMarkerNearlyOppositePoleWhenProjectThenReturnsNull()
        {
            // Position more than 90 deg from the WCS centre: behind the tangent plane.
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
