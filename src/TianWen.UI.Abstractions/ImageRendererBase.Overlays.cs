using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // -----------------------------------------------------------------------
        // WCS Grid labels
        // -----------------------------------------------------------------------

        /// <summary>
        /// Grid spacing options in arcseconds, from fine to coarse.
        /// The renderer picks the smallest spacing that gives at least ~3 grid lines.
        /// </summary>
        private static readonly double[] GridSpacingsArcsec =
        [
            1, 2, 5, 10, 15, 30,                           // sub-arcminute
            60, 120, 300, 600, 900, 1800,                   // arcminutes
            3600, 7200, 18000, 36000, 90000, 180000,        // degrees
        ];

        /// <summary>
        /// Renders RA/Dec labels at grid line intersections with image edges.
        /// The grid lines themselves are drawn by the GPU shader.
        /// </summary>
        private void RenderGridLabels(ViewerState state, WCS wcs)
        {
            if (string.IsNullOrEmpty(FontPath) || ImageWidth <= 0 || ImageHeight <= 0)
            {
                return;
            }

            // All geometry from the single layout pass (arranged image-pane rect + image placement).
            var area = _layout.ImageArea;
            var scale = _placement.Scale;
            var imgOffsetX = _placement.OffsetX;
            var imgOffsetY = _placement.OffsetY;

            // Visible image pixel bounds (1-based FITS coordinates), clamped to the image-area pane.
            var visLeft = Math.Max(1.0, (area.X - imgOffsetX) / scale + 1);
            var visRight = Math.Min((double)ImageWidth, (area.X + area.Width - imgOffsetX) / scale + 1);
            var visTop = Math.Max(1.0, (area.Y - imgOffsetY) / scale + 1);
            var visBottom = Math.Min((double)ImageHeight, (area.Y + area.Height - imgOffsetY) / scale + 1);

            if (visLeft >= visRight || visTop >= visBottom)
            {
                return;
            }

            // Get sky coordinates at corners to determine RA/Dec range
            var corners = new (double RA, double Dec)?[]
            {
                wcs.PixelToSky(visLeft, visTop),
                wcs.PixelToSky(visRight, visTop),
                wcs.PixelToSky(visLeft, visBottom),
                wcs.PixelToSky(visRight, visBottom),
                wcs.PixelToSky((visLeft + visRight) / 2, visTop),
                wcs.PixelToSky((visLeft + visRight) / 2, visBottom),
                wcs.PixelToSky(visLeft, (visTop + visBottom) / 2),
                wcs.PixelToSky(visRight, (visTop + visBottom) / 2),
            };

            double minRA = double.MaxValue, maxRA = double.MinValue;
            double minDec = double.MaxValue, maxDec = double.MinValue;
            foreach (var c in corners)
            {
                if (c is not { } sky)
                {
                    continue;
                }
                minRA = Math.Min(minRA, sky.RA);
                maxRA = Math.Max(maxRA, sky.RA);
                minDec = Math.Min(minDec, sky.Dec);
                maxDec = Math.Max(maxDec, sky.Dec);
            }

            if (minRA > maxRA || minDec > maxDec)
            {
                return;
            }

            // Handle RA wraparound (if range spans 0h/24h)
            if (maxRA - minRA > 12.0)
            {
                double wrapMin = double.MaxValue, wrapMax = double.MinValue;
                foreach (var c in corners)
                {
                    if (c is not { } sky)
                    {
                        continue;
                    }
                    var ra = sky.RA < 12.0 ? sky.RA + 24.0 : sky.RA;
                    wrapMin = Math.Min(wrapMin, ra);
                    wrapMax = Math.Max(wrapMax, ra);
                }
                minRA = wrapMin;
                maxRA = wrapMax;
            }

            // Compute grid spacing in sky units
            var pixelScaleArcsec = wcs.PixelScaleArcsec;
            var viewImagePixels = MathF.Min(area.Width, area.Height) / scale;
            var viewArcsec = viewImagePixels * pixelScaleArcsec;
            var spacingArcsec = GridSpacingsArcsec[^1];
            foreach (var candidate in GridSpacingsArcsec)
            {
                if (candidate >= viewArcsec / 8.0)
                {
                    spacingArcsec = candidate;
                    break;
                }
            }

            var spacingDecDeg = spacingArcsec / 3600.0;
            var spacingRAhours = spacingArcsec / 3600.0 / 15.0;

            var labelSize = FontSize * 0.85f;
            var labelPad = 3f;

            var raOnHorizEdges = Math.Abs(wcs.CD1_1) > Math.Abs(wcs.CD1_2);

            var cornerMargin = labelSize * 4f;

            var numSamples = 300;

            var edges = new (double X0, double Y0, double X1, double Y1, bool IsHorizontal)[]
            {
                (visLeft, visTop, visRight, visTop, true),       // top edge
                (visLeft, visBottom, visRight, visBottom, true),  // bottom edge
                (visLeft, visTop, visLeft, visBottom, false),     // left edge
                (visRight, visTop, visRight, visBottom, false),   // right edge
            };

            foreach (var (x0, y0, x1, y1, isHoriz) in edges)
            {
                var showRA = isHoriz == raOnHorizEdges;
                var showDec = isHoriz != raOnHorizEdges;
                var isFirstEdge = isHoriz ? (y0 <= visTop + 1) : (x0 <= visLeft + 1);

                var edgeStartX = imgOffsetX + (float)(x0 - 1) * scale;
                var edgeStartY = imgOffsetY + (float)(y0 - 1) * scale;
                var edgeEndX = imgOffsetX + (float)(x1 - 1) * scale;
                var edgeEndY = imgOffsetY + (float)(y1 - 1) * scale;

                double prevRA = double.NaN, prevDec = double.NaN;
                float prevScreenX = 0, prevScreenY = 0;

                for (int i = 0; i <= numSamples; i++)
                {
                    var t = (double)i / numSamples;
                    var px = x0 + (x1 - x0) * t;
                    var py = y0 + (y1 - y0) * t;
                    var sky = wcs.PixelToSky(px, py);
                    if (sky is not { } s)
                    {
                        prevRA = double.NaN;
                        prevDec = double.NaN;
                        continue;
                    }

                    var screenX = imgOffsetX + (float)(px - 1) * scale;
                    var screenY = imgOffsetY + (float)(py - 1) * scale;

                    if (!double.IsNaN(prevRA))
                    {
                        // RA crossings (skip wraparound jumps)
                        if (showRA && Math.Abs(s.RA - prevRA) < 12.0)
                        {
                            var raLo = Math.Min(prevRA, s.RA);
                            var raHi = Math.Max(prevRA, s.RA);
                            var firstG = (int)Math.Ceiling(raLo / spacingRAhours);
                            var lastG = (int)Math.Floor(raHi / spacingRAhours);
                            for (var g = firstG; g <= lastG; g++)
                            {
                                var gridRA = g * spacingRAhours;
                                var frac = (gridRA - prevRA) / (s.RA - prevRA);
                                var lx = prevScreenX + (screenX - prevScreenX) * (float)frac;
                                var ly = prevScreenY + (screenY - prevScreenY) * (float)frac;

                                var distToStart = MathF.Abs(isHoriz ? lx - edgeStartX : ly - edgeStartY);
                                var distToEnd = MathF.Abs(isHoriz ? lx - edgeEndX : ly - edgeEndY);
                                if (distToStart < cornerMargin || distToEnd < cornerMargin)
                                {
                                    continue;
                                }

                                var normalizedRA = gridRA % 24.0;
                                if (normalizedRA < 0) normalizedRA += 24.0;
                                var raLabel = FormatRALabel(normalizedRA, spacingArcsec);
                                PlaceEdgeLabel(raLabel, lx, ly, labelSize, labelPad, isHoriz, isFirstEdge);
                            }
                        }

                        // Dec crossings
                        if (showDec)
                        {
                            var decLo = Math.Min(prevDec, s.Dec);
                            var decHi = Math.Max(prevDec, s.Dec);
                            var firstG = (int)Math.Ceiling(decLo / spacingDecDeg);
                            var lastG = (int)Math.Floor(decHi / spacingDecDeg);
                            for (var g = firstG; g <= lastG; g++)
                            {
                                var gridDec = g * spacingDecDeg;
                                var frac = (gridDec - prevDec) / (s.Dec - prevDec);
                                var lx = prevScreenX + (screenX - prevScreenX) * (float)frac;
                                var ly = prevScreenY + (screenY - prevScreenY) * (float)frac;

                                var distToStart = MathF.Abs(isHoriz ? lx - edgeStartX : ly - edgeStartY);
                                var distToEnd = MathF.Abs(isHoriz ? lx - edgeEndX : ly - edgeEndY);
                                if (distToStart < cornerMargin || distToEnd < cornerMargin)
                                {
                                    continue;
                                }

                                var decLabel = FormatDecLabel(gridDec, spacingArcsec);
                                PlaceEdgeLabel(decLabel, lx, ly, labelSize, labelPad, isHoriz, isFirstEdge);
                            }
                        }
                    }

                    prevRA = s.RA;
                    prevDec = s.Dec;
                    prevScreenX = screenX;
                    prevScreenY = screenY;
                }
            }
        }

        private void PlaceEdgeLabel(string label, float lx, float ly, float labelSize, float labelPad,
            bool isHoriz, bool isFirstEdge)
        {
            var lineOffset = labelPad + 2f;
            if (isHoriz)
            {
                var labelX = isFirstEdge ? lx + lineOffset : lx - MeasureText(label, labelSize) - lineOffset;
                var labelY = isFirstEdge ? ly + labelPad : ly - labelSize - labelPad;
                DrawText(label, labelX, labelY, labelSize, GridLabelColor);
            }
            else
            {
                var labelX = isFirstEdge ? lx + labelPad : lx - MeasureText(label, labelSize) - labelPad;
                var labelY = isFirstEdge ? ly + lineOffset : ly - labelSize - lineOffset;
                DrawText(label, labelX, labelY, labelSize, GridLabelColor);
            }
        }

        private static string FormatRALabel(double raHours, double spacingArcsec)
        {
            var h = (int)Math.Floor(raHours);
            var m = (raHours - h) * 60.0;
            var mi = (int)Math.Floor(m);
            var s = (m - mi) * 60.0;

            if (spacingArcsec >= 3600)
            {
                return $"{h}h";
            }
            if (spacingArcsec >= 60)
            {
                return $"{h}h{mi:D2}m";
            }
            return $"{h}h{mi:D2}m{s:00.0}s";
        }

        private static string FormatDecLabel(double decDeg, double spacingArcsec)
        {
            var sign = decDeg >= 0 ? "+" : "-";
            var abs = Math.Abs(decDeg);
            var d = (int)Math.Floor(abs);
            var m = (abs - d) * 60.0;
            var mi = (int)Math.Floor(m);
            var s = (m - mi) * 60.0;

            if (spacingArcsec >= 3600)
            {
                return $"{sign}{d}\u00b0";
            }
            if (spacingArcsec >= 60)
            {
                return $"{sign}{d}\u00b0{mi:D2}'";
            }
            return $"{sign}{d}\u00b0{mi:D2}'{s:00.0}\"";
        }

        // -----------------------------------------------------------------------
        // Star Overlay
        // -----------------------------------------------------------------------

        private void RenderStarOverlay(ViewerState state, StarList stars)
        {
            // Geometry from the single layout pass -- consistent with the rendered image by construction.
            var area = _layout.ImageArea;
            var offsetX = _placement.OffsetX;
            var offsetY = _placement.OffsetY;

            var clipLeft = area.X;
            var clipTop = area.Y;
            var clipRight = area.X + area.Width;
            var clipBottom = area.Y + area.Height;

            foreach (var star in stars)
            {
                var cx = offsetX + (star.XCentroid + 0.5f) * state.Zoom;
                var cy = offsetY + (star.YCentroid + 0.5f) * state.Zoom;
                var radius = MathF.Max(star.HFD * 0.5f * state.Zoom, 6f);

                if (cx + radius < clipLeft || cx - radius > clipRight ||
                    cy + radius < clipTop || cy - radius > clipBottom)
                {
                    continue;
                }

                var alpha = MathF.Min(1.0f, 0.3f + state.Zoom * 0.7f);
                DrawEllipseOverlay(cx, cy, radius, radius, 0f,
                    new RGBAColor32(0, (byte)(0.8f * 255), (byte)(0.2f * 255), (byte)(alpha * 255)), 1.5f);
            }
        }

        // -----------------------------------------------------------------------
        // Object Overlays
        // -----------------------------------------------------------------------

        private void RenderOverlays(ViewerState state, WCS wcs, ICelestialObjectDB db)
        {
            if (string.IsNullOrEmpty(FontPath) || ImageWidth <= 0 || ImageHeight <= 0)
            {
                return;
            }

            // Image-area pane rect from the single layout pass.
            var area = _layout.ImageArea;

            var layout = new ViewportLayout(
                WindowWidth: Width,
                WindowHeight: Height,
                ImageWidth: ImageWidth,
                ImageHeight: ImageHeight,
                Zoom: state.Zoom,
                PanOffset: state.PanOffset,
                AreaLeft: area.X,
                AreaTop: area.Y,
                AreaWidth: area.Width,
                AreaHeight: area.Height,
                DpiScale: DpiScale
            );

            var items = OverlayEngine.ComputeOverlays(layout, wcs, db, MeasureText, BaseFontSize);
            if (items.Count == 0)
            {
                return;
            }

            var labelSize = FontSize * 0.85f;
            var labelPad = 4f;

            // Draw markers first (brightest-first order is preserved by the engine)
            foreach (var item in items)
            {
                var (r, g, b) = item.Color;
                var marker = item.Marker;
                switch (marker.Kind)
                {
                    case OverlayMarkerKind.Ellipse:
                        DrawEllipseOverlay(item.ScreenX, item.ScreenY,
                            marker.SemiMajorPx, marker.SemiMinorPx, marker.AngleRad,
                            FloatToColor(r, g, b, 1.0f), 1.5f);
                        break;
                    case OverlayMarkerKind.Cross:
                        DrawCrossOverlay(item.ScreenX, item.ScreenY, marker.ArmPx,
                            FloatToColor(r, g, b, 1.0f));
                        break;
                    case OverlayMarkerKind.Circle:
                        DrawEllipseOverlay(item.ScreenX, item.ScreenY,
                            marker.RadiusPx, marker.RadiusPx, 0f,
                            FloatToColor(r, g, b, 0.9f), 1.5f);
                        break;
                }
            }

            // Label placement + collision avoidance is shared with the sky map object
            // overlay (see OverlayEngine.PlaceLabels).
            var lineH = labelSize * 1.2f;
            OverlayEngine.PlaceLabels(items, labelSize, labelPad, MeasureText,
                (item, lx, ly) =>
                {
                    var (r, g, b) = item.Color;
                    DrawOverlayLabelLines(item.LabelLines, lx, ly, lineH, labelSize, r, g, b);
                });
        }

        /// <summary>
        /// Render the caller-supplied <see cref="WcsAnnotation"/> through the active
        /// WCS using the renderer's existing primitives. Generic — knows nothing
        /// about polar alignment, plate-solve verification, etc.; just iterates the
        /// annotation list, projects each item via <see cref="WcsAnnotationLayer"/>,
        /// dispatches to <see cref="DrawCrossOverlay"/> or
        /// <see cref="DrawEllipseOverlay"/>.
        /// </summary>
        private void RenderWcsAnnotation(ViewerState state, WCS wcs)
        {
            if (ImageWidth <= 0 || ImageHeight <= 0) return;

            // Image-area pane rect from the single layout pass.
            var area = _layout.ImageArea;

            var layout = new ViewportLayout(
                WindowWidth: Width,
                WindowHeight: Height,
                ImageWidth: ImageWidth,
                ImageHeight: ImageHeight,
                Zoom: state.Zoom,
                PanOffset: state.PanOffset,
                AreaLeft: area.X,
                AreaTop: area.Y,
                AreaWidth: area.Width,
                AreaHeight: area.Height,
                DpiScale: DpiScale);

            var labelSize = FontSize * 0.85f;
            var labelPad = 4f;

            // Rings drawn first so marker glyphs draw on top of them.
            if (!Annotation.Rings.IsDefaultOrEmpty)
            {
                foreach (var ring in Annotation.Rings)
                {
                    if (WcsAnnotationLayer.ProjectRing(ring, wcs, layout) is not { } placement) continue;
                    if (placement.RadiusScreenPx < 1f) continue;
                    DrawEllipseOverlay(placement.ScreenX, placement.ScreenY,
                        placement.RadiusScreenPx, placement.RadiusScreenPx, 0f,
                        ring.Color, thickness: 1.5f);
                    if (!string.IsNullOrEmpty(ring.Label))
                    {
                        DrawText(ring.Label,
                            placement.ScreenX + placement.RadiusScreenPx + labelPad,
                            placement.ScreenY - labelSize * 0.5f,
                            labelSize,
                            ring.Color);
                    }
                }
            }

            if (!Annotation.Markers.IsDefaultOrEmpty)
            {
                foreach (var marker in Annotation.Markers)
                {
                    if (WcsAnnotationLayer.ProjectMarker(marker, wcs, layout) is not { } placement) continue;

                    switch (marker.Glyph)
                    {
                        case SkyMarkerGlyph.Cross:
                            DrawCrossOverlay(placement.ScreenX, placement.ScreenY, marker.SizePx, marker.Color);
                            break;
                        case SkyMarkerGlyph.Dot:
                            DrawEllipseOverlay(placement.ScreenX, placement.ScreenY,
                                marker.SizePx, marker.SizePx, 0f, marker.Color, thickness: 0f);
                            break;
                        case SkyMarkerGlyph.Circle:
                            DrawEllipseOverlay(placement.ScreenX, placement.ScreenY,
                                marker.SizePx, marker.SizePx, 0f, marker.Color, thickness: 1.5f);
                            break;
                        case SkyMarkerGlyph.CircledCross:
                            DrawEllipseOverlay(placement.ScreenX, placement.ScreenY,
                                marker.SizePx, marker.SizePx, 0f, marker.Color, thickness: 1.5f);
                            DrawCrossOverlay(placement.ScreenX, placement.ScreenY, marker.SizePx * 0.6f, marker.Color);
                            break;
                    }

                    if (!string.IsNullOrEmpty(marker.Label))
                    {
                        DrawText(marker.Label,
                            placement.ScreenX + marker.SizePx + labelPad,
                            placement.ScreenY - labelSize * 0.5f,
                            labelSize,
                            marker.Color);
                    }
                }
            }

            if (!Annotation.Arrows.IsDefaultOrEmpty)
            {
                foreach (var arrow in Annotation.Arrows)
                {
                    if (WcsAnnotationLayer.ProjectArrow(arrow, wcs, layout) is not { } placement) continue;

                    var dx = placement.EndScreenX - placement.StartScreenX;
                    var dy = placement.EndScreenY - placement.StartScreenY;
                    var len = MathF.Sqrt(dx * dx + dy * dy);
                    // Skip degenerate arrows (start and end project to ~same
                    // pixel) -- a single dot would carry no direction info.
                    if (len < 1f) continue;

                    DrawLineOverlay(placement.StartScreenX, placement.StartScreenY,
                        placement.EndScreenX, placement.EndScreenY,
                        arrow.Color, arrow.ThicknessPx);

                    // HeadSizePx <= 0 -> bare line segment, no arrowhead.
                    // Used by the polar-align cross meridians (4 radial line
                    // segments from refracted pole to outer ring).
                    if (arrow.HeadSizePx > 0f)
                    {
                        // Two-segment arrowhead: angle off the shaft direction at
                        // the head endpoint. 30deg legs match SharpCap's look.
                        var headLen = arrow.HeadSizePx;
                        var ux = dx / len;
                        var uy = dy / len;
                        const float headAngle = 0.5236f; // 30 degrees in radians
                        var ca = MathF.Cos(headAngle);
                        var sa = MathF.Sin(headAngle);
                        // Two unit vectors rotated +/-headAngle from the *reverse*
                        // shaft direction; scale by head length to produce the
                        // two head-leg endpoints.
                        var leg1X = placement.EndScreenX - headLen * (ca * ux - sa * uy);
                        var leg1Y = placement.EndScreenY - headLen * (sa * ux + ca * uy);
                        var leg2X = placement.EndScreenX - headLen * (ca * ux + sa * uy);
                        var leg2Y = placement.EndScreenY - headLen * (-sa * ux + ca * uy);
                        DrawLineOverlay(placement.EndScreenX, placement.EndScreenY, leg1X, leg1Y, arrow.Color, arrow.ThicknessPx);
                        DrawLineOverlay(placement.EndScreenX, placement.EndScreenY, leg2X, leg2Y, arrow.Color, arrow.ThicknessPx);
                    }

                    if (!string.IsNullOrEmpty(arrow.Label))
                    {
                        DrawText(arrow.Label,
                            placement.EndScreenX + labelPad,
                            placement.EndScreenY - labelSize * 0.5f,
                            labelSize,
                            arrow.Color);
                    }
                }
            }
        }

        private void DrawOverlayLabelLines(IReadOnlyList<string> lines, float x, float y, float lineH, float fontSize, float r, float g, float b)
        {
            for (int li = 0; li < lines.Count; li++)
            {
                // First line full intensity; continuation lines dimmed. Dim by scaling
                // the RGB toward black (the original behaviour) rather than via alpha.
                var dim = li == 0 ? 1.0f : 0.7f;
                DrawText(lines[li], x, y + li * lineH, fontSize, RGBAColor32.FromFloat(r * dim, g * dim, b * dim, 1f));
            }
        }

        private static RGBAColor32 FloatToColor(float r, float g, float b, float a)
            => RGBAColor32.FromFloat(r, g, b, a);

    }
}
