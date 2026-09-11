using System;
using TianWen.Lib.Astrometry;

namespace TianWen.UI.Abstractions.Overlays
{
    /// <summary>
    /// Pure projection helpers for <see cref="WcsAnnotation"/>: maps each
    /// <see cref="SkyMarker"/> / <see cref="SkyRing"/> through the active WCS
    /// to screen pixel positions + sizes. Splits cleanly from the actual
    /// drawing call so the math is unit-testable without a renderer dependency.
    ///
    /// <see cref="ImageRendererBase{TSurface}"/> consumes this output and
    /// dispatches to its existing primitives (DrawCrossOverlay / DrawEllipseOverlay).
    /// </summary>
    public static class WcsAnnotationLayer
    {
        /// <summary>
        /// Project a sky-position marker to screen pixels, including a clip
        /// flag for callers that want to draw an off-frame edge arrow.
        /// Returns null if the marker is behind the tangent plane (rare, 
        /// happens for objects more than 90° from the WCS centre).
        /// </summary>
        public static MarkerPlacement? ProjectMarker(in SkyMarker marker, in WCS wcs, in ViewportLayout layout)
        {
            if (wcs.SkyToPixel(marker.RaHours, marker.DecDeg) is not { } imgPx)
            {
                return null;
            }
            var (sx, sy) = ImageToScreen(imgPx.X, imgPx.Y, layout);
            return new MarkerPlacement(
                ScreenX: (float)sx,
                ScreenY: (float)sy,
                IsOnScreen: IsOnImage(imgPx.X, imgPx.Y, layout));
        }

        /// <summary>
        /// Project a sky-radius ring to a screen-space ellipse. The ring is
        /// rendered as a circle in the tangent plane using the *local* pixel
        /// scale at its centre: exact for small rings (sub-pixel error below
        /// ~5° radius), imperceptible for the polar-alignment use case where
        /// rings are 5'-30' across the centre of the FOV.
        /// </summary>
        public static RingPlacement? ProjectRing(in SkyRing ring, in WCS wcs, in ViewportLayout layout)
        {
            if (wcs.SkyToPixel(ring.CenterRaHours, ring.CenterDecDeg) is not { } imgPx)
            {
                return null;
            }
            var (sx, sy) = ImageToScreen(imgPx.X, imgPx.Y, layout);

            // Local pixel scale (arcsec/px) -> screen pixels per arcmin at the current zoom.
            // PixelScaleArcsec already accounts for the |det(CD)| determinant; here we just
            // scale by zoom and convert arcmin -> arcsec.
            double pxPerArcsec = 1.0 / wcs.PixelScaleArcsec;
            double radiusArcsec = ring.RadiusArcmin * 60.0;
            double radiusImgPx = radiusArcsec * pxPerArcsec;
            double radiusScreenPx = radiusImgPx * layout.Zoom;
            return new RingPlacement(
                ScreenX: (float)sx,
                ScreenY: (float)sy,
                RadiusScreenPx: (float)radiusScreenPx,
                IsOnScreen: IsOnImage(imgPx.X, imgPx.Y, layout));
        }

        /// <summary>
        /// Project a sky-to-sky arrow through the WCS to two screen-space
        /// points. Returns null if either endpoint cannot be projected
        /// (behind the tangent plane).
        /// </summary>
        public static ArrowPlacement? ProjectArrow(in SkyArrow arrow, in WCS wcs, in ViewportLayout layout)
        {
            if (wcs.SkyToPixel(arrow.StartRaHours, arrow.StartDecDeg) is not { } startPx) return null;
            if (wcs.SkyToPixel(arrow.EndRaHours, arrow.EndDecDeg) is not { } endPx) return null;
            var (sx0, sy0) = ImageToScreen(startPx.X, startPx.Y, layout);
            var (sx1, sy1) = ImageToScreen(endPx.X, endPx.Y, layout);
            return new ArrowPlacement(
                StartScreenX: (float)sx0,
                StartScreenY: (float)sy0,
                EndScreenX: (float)sx1,
                EndScreenY: (float)sy1,
                IsStartOnScreen: IsOnImage(startPx.X, startPx.Y, layout),
                IsEndOnScreen: IsOnImage(endPx.X, endPx.Y, layout));
        }

        /// <summary>
        /// Where a position in the frame's own pixel coordinates lands on screen. <b>The one
        /// definition of that mapping</b>: every marker, ring, grid line, readout and hit-test in the
        /// viewer goes through this or its inverse, <see cref="ScreenToImage"/>, and none re-derives it.
        /// </summary>
        /// <remarks>
        /// <para><b>Half a pixel, and why.</b> A pixel coordinate in TianWen's in-memory frame -- a
        /// <see cref="WCS.SkyToPixel"/> answer, a detected centroid, <see cref="WCS.CRPix1"/> -- puts
        /// the CENTRE of pixel <c>[y, x]</c> at <c>(x, y)</c>. The picture is drawn as a quad whose
        /// pixel <c>i</c> covers the screen band <c>[origin + i * zoom, origin + (i + 1) * zoom)</c>,
        /// so the centre of that same pixel is half a cell further along. The star overlay and the sky
        /// backdrop's solver always had this right; the catalogue overlay, the grid, the selection ring
        /// and this helper did not.</para>
        /// <para><b>The history, so the trap is recognisable.</b> Until 2026-09-05 a <see cref="WCS"/>
        /// carried its header's 1-based CRPIX verbatim, and every consumer compensated locally: minus
        /// one on the way to the screen, plus one on the way into <see cref="WCS.PixelToSky"/>. The
        /// fix moved the conversion to the FITS boundary and left the WCS 0-based in memory, which
        /// made every one of those local compensations wrong by a whole pixel -- and they lived on,
        /// because each was a private line of arithmetic with no single place to correct. Measured on
        /// the selection ring against the detected star it named: 12 screen pixels apart at 8:1,
        /// exactly 1.5 image pixels, the stale minus one plus the missing half. Pinned by
        /// <c>ViewerObjectSelectionTests.TheRingIsDrawnWhereTheStarsLightIs</c> against the PICTURE
        /// rather than against this rule, so a rewrite has to keep landing the ring on the star. The
        /// GPU grid in <c>image.frag</c> (<c>wcsPixel</c>) is the shader half of the same rule.</para>
        /// </remarks>
        public static (double X, double Y) ImageToScreen(double imgX, double imgY, in ViewportLayout layout)
        {
            double sx = layout.ImageOffsetX + ((imgX + 0.5) * layout.Zoom);
            double sy = layout.ImageOffsetY + ((imgY + 0.5) * layout.Zoom);
            return (sx, sy);
        }

        /// <summary>
        /// The inverse of <see cref="ImageToScreen"/>: the frame pixel coordinate under a screen
        /// position, continuous, in the centroid frame <see cref="WCS.PixelToSky"/> takes. It keeps
        /// answering outside the sensor, which is what a click beside the picture needs.
        /// </summary>
        public static (double X, double Y) ScreenToImage(double screenX, double screenY, in ViewportLayout layout)
        {
            double ix = ((screenX - layout.ImageOffsetX) / layout.Zoom) - 0.5;
            double iy = ((screenY - layout.ImageOffsetY) / layout.Zoom) - 0.5;
            return (ix, iy);
        }

        /// <summary>
        /// The index of the pixel a continuous frame coordinate falls in: pixel <c>i</c> owns
        /// <c>[i - 0.5, i + 0.5)</c>, so this is the nearest integer with a tie going up.
        /// </summary>
        public static int PixelIndex(double imageCoord) => (int)Math.Floor(imageCoord + 0.5);

        /// <summary>
        /// Whether a frame coordinate is inside the sensor's own raster: pixel 0 begins at -0.5 and the
        /// last pixel ends at <c>ImageWidth - 0.5</c>, the same bands <see cref="PixelIndex"/> resolves.
        /// </summary>
        public static bool IsOnImage(double imgX, double imgY, in ViewportLayout layout) =>
            imgX >= -0.5 && imgX < layout.ImageWidth - 0.5 &&
            imgY >= -0.5 && imgY < layout.ImageHeight - 0.5;
    }

    /// <summary>
    /// Projected screen position of a <see cref="SkyMarker"/>.
    /// </summary>
    /// <param name="ScreenX">Centre X in screen pixels.</param>
    /// <param name="ScreenY">Centre Y in screen pixels.</param>
    /// <param name="IsOnScreen">False if the marker projects outside the image
    /// pixel bounds: the renderer can draw an edge arrow + offset label
    /// instead of the glyph itself.</param>
    public readonly record struct MarkerPlacement(float ScreenX, float ScreenY, bool IsOnScreen);

    /// <summary>
    /// Projected screen ellipse for a <see cref="SkyRing"/>. The radius is
    /// circular at the local pixel scale (no foreshortening modelled).
    /// </summary>
    /// <param name="ScreenX">Centre X in screen pixels.</param>
    /// <param name="ScreenY">Centre Y in screen pixels.</param>
    /// <param name="RadiusScreenPx">Ring radius in screen pixels.</param>
    /// <param name="IsOnScreen">Whether the centre falls within image bounds.
    /// Note: the ring may still be partially visible even when its centre is off-screen.</param>
    public readonly record struct RingPlacement(float ScreenX, float ScreenY, float RadiusScreenPx, bool IsOnScreen);

    /// <summary>
    /// Projected screen geometry for a <see cref="SkyArrow"/>: tail and head
    /// in screen pixels plus per-endpoint clip flags for the renderer.
    /// </summary>
    public readonly record struct ArrowPlacement(
        float StartScreenX,
        float StartScreenY,
        float EndScreenX,
        float EndScreenY,
        bool IsStartOnScreen,
        bool IsEndOnScreen);
}
