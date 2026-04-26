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
        /// Returns null if the marker is behind the tangent plane (rare —
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
        /// scale at its centre — exact for small rings (sub-pixel error below
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

        /// <summary>Map an image-pixel position to screen pixels via the layout.</summary>
        public static (double X, double Y) ImageToScreen(double imgX, double imgY, in ViewportLayout layout)
        {
            // ViewportLayout puts the image origin (0, 0) at (ImageOffsetX, ImageOffsetY)
            // on screen, then scales by Zoom.
            double sx = layout.ImageOffsetX + imgX * layout.Zoom;
            double sy = layout.ImageOffsetY + imgY * layout.Zoom;
            return (sx, sy);
        }

        private static bool IsOnImage(double imgX, double imgY, in ViewportLayout layout) =>
            imgX >= 0 && imgX <= layout.ImageWidth &&
            imgY >= 0 && imgY <= layout.ImageHeight;
    }

    /// <summary>
    /// Projected screen position of a <see cref="SkyMarker"/>.
    /// </summary>
    /// <param name="ScreenX">Centre X in screen pixels.</param>
    /// <param name="ScreenY">Centre Y in screen pixels.</param>
    /// <param name="IsOnScreen">False if the marker projects outside the image
    /// pixel bounds — the renderer can draw an edge arrow + offset label
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
}
