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
        /// The viewport every overlay draws into, from the same placement the picture was drawn with.
        /// </summary>
        /// <remarks>
        /// The origin is the placement's rather than re-derived from the pan, so a display crop moves
        /// the overlays with the quad; before this each overlay built its own layout from
        /// <c>Zoom</c> and <c>PanOffset</c>, which is the uncropped frame's geometry. One builder, so
        /// the markers, the grid, the ring, the star circles and both readouts cannot disagree about
        /// where pixel (0, 0) is.
        /// </remarks>
        private ViewportLayout CurrentViewportLayout(ViewerState state)
        {
            var area = _layout.ImageArea;
            return new ViewportLayout(
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
                DpiScale: DpiScale,
                ImageOrigin: (_placement.OffsetX, _placement.OffsetY));
        }

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
            var layout = CurrentViewportLayout(state);

            // The visible part of the sensor in the frame's own pixel coordinates: the pane's edges
            // brought into the frame and clamped to the raster, which runs from -0.5 to Width - 0.5
            // because the centre of pixel i is i. Labels are then placed where a grid line leaves the
            // PICTURE, which is where the shader stops drawing it.
            var (paneLeftPx, paneTopPx) = WcsAnnotationLayer.ScreenToImage(area.X, area.Y, layout);
            var (paneRightPx, paneBottomPx) = WcsAnnotationLayer.ScreenToImage(
                area.X + area.Width, area.Y + area.Height, layout);
            var visLeft = Math.Max(-0.5, paneLeftPx);
            var visRight = Math.Min(ImageWidth - 0.5, paneRightPx);
            var visTop = Math.Max(-0.5, paneTopPx);
            var visBottom = Math.Min(ImageHeight - 0.5, paneBottomPx);

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

                var (edgeStartXd, edgeStartYd) = WcsAnnotationLayer.ImageToScreen(x0, y0, layout);
                var (edgeEndXd, edgeEndYd) = WcsAnnotationLayer.ImageToScreen(x1, y1, layout);
                var edgeStartX = (float)edgeStartXd;
                var edgeStartY = (float)edgeStartYd;
                var edgeEndX = (float)edgeEndXd;
                var edgeEndY = (float)edgeEndYd;

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

                    var (screenXd, screenYd) = WcsAnnotationLayer.ImageToScreen(px, py, layout);
                    var screenX = (float)screenXd;
                    var screenY = (float)screenYd;

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
            var layout = CurrentViewportLayout(state);

            var clipLeft = area.X;
            var clipTop = area.Y;
            var clipRight = area.X + area.Width;
            var clipBottom = area.Y + area.Height;

            foreach (var star in stars)
            {
                // A centroid is in the same frame a WCS answers in, so it takes the same mapping.
                var (sx, sy) = WcsAnnotationLayer.ImageToScreen(star.XCentroid, star.YCentroid, layout);
                var cx = (float)sx;
                var cy = (float)sy;
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

        /// <summary>
        /// One catalogued object as the overlay DREW it last frame: where its marker is, what shape
        /// the marker has, and the box its label occupies when something gave it one.
        /// </summary>
        /// <param name="NamedByRing">
        /// True when <paramref name="LabelBox"/> is the selection ring's name rather than a label the
        /// overlay's own pass placed -- which is the SELECTED object's case, and only if the overlay
        /// really did leave it out: the placement outcome is consulted first, so an overlay that went
        /// on labelling the selected object reports that, box and all.
        /// </param>
        internal readonly record struct DrawnOverlayObject(
            CatalogIndex Index,
            float ScreenX,
            float ScreenY,
            OverlayMarker Marker,
            (float X, float Y, float W, float H)? LabelBox,
            bool NamedByRing);

        /// <summary>
        /// What the object overlay drew last frame, in screen pixels -- empty whenever it did not draw.
        /// </summary>
        /// <remarks>
        /// <para><b>This is what a tap resolves against first.</b> A label is drawn BESIDE its marker,
        /// usually further from the object's centre than the click tolerance reaches, and the resolver
        /// used to know nothing about it: a tap on the letters answered with whichever catalogue CENTRE
        /// was nearest them. Measured on the 10P master's seven ellipse-marked galaxies, a tap on the
        /// label selected a neighbour three times and nothing once -- a tap on "NGC 7201" selected the
        /// star NGC 7202, whose circular ring then sat beside the galaxy's ellipse, which is what
        /// "the ring is a circle and it is off-centre" was. Recording where things were drawn is the
        /// only way to answer "what did I click on" with what was on the screen.</para>
        /// <para>Written by the render pass and read by the input pass on the SAME thread (both hosts
        /// dispatch input from the thread that renders), so a plain field suffices. Cleared whenever
        /// the overlay is not drawn, so a label from the last frame it was ON can never be hit after
        /// it is switched off: a hidden widget consumes no input.</para>
        /// </remarks>
        private ImmutableArray<DrawnOverlayObject> _drawnOverlayObjects = ImmutableArray<DrawnOverlayObject>.Empty;

        /// <summary>Test seam: what the overlay drew last frame. See <see cref="_drawnOverlayObjects"/>.</summary>
        internal ImmutableArray<DrawnOverlayObject> DrawnOverlayObjects => _drawnOverlayObjects;

        /// <summary>
        /// Draws the catalogue markers and their labels, and records where each landed.
        /// </summary>
        /// <param name="selectionRing">
        /// The selection ring this frame, if any: its object's own label is left OUT of the label
        /// pass and the ring's name box is reserved instead. The ring names the object, in the accent
        /// colour, and a second copy of the name drawn by the overlay landed on the first -- that is
        /// what "the text is mangled" was. Reserving the box is what keeps a NEIGHBOUR's label off the
        /// ring's name too: the ring is drawn after this pass and cannot dodge.
        /// </param>
        private void RenderOverlays(ViewerState state, WCS wcs, ICelestialObjectDB db, SelectionRingGeometry? selectionRing)
        {
            _drawnOverlayObjects = ImmutableArray<DrawnOverlayObject>.Empty;

            if (string.IsNullOrEmpty(FontPath) || ImageWidth <= 0 || ImageHeight <= 0)
            {
                return;
            }

            var layout = CurrentViewportLayout(state);

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

            IReadOnlyList<OverlayItem> toLabel = items;
            IReadOnlyList<(float X, float Y, float W, float H)>? reserved = null;
            if (selectionRing is { Index: { } selectedIndex } ring)
            {
                var others = new List<OverlayItem>(items.Count);
                foreach (var item in items)
                {
                    if (item.Index != selectedIndex)
                    {
                        others.Add(item);
                    }
                }
                toLabel = others;
                reserved = [ring.LabelBox];
            }

            // Label placement + collision avoidance is shared with the sky map object
            // overlay (see OverlayEngine.PlaceLabels).
            var lineH = labelSize * 1.2f;
            var placed = new Dictionary<OverlayItem, PlacedLabel>();
            OverlayEngine.PlaceLabels(toLabel, labelSize, labelPad, MeasureText,
                label =>
                {
                    var (r, g, b) = label.Item.Color;
                    DrawOverlayLabelLines(label.Item.LabelLines, label.X, label.Y, lineH, labelSize, r, g, b);
                    placed[label.Item] = label;
                },
                reservedRegions: reserved);

            var drawn = ImmutableArray.CreateBuilder<DrawnOverlayObject>(items.Count);
            foreach (var item in items)
            {
                (float X, float Y, float W, float H)? box = null;
                var namedByRing = false;
                if (placed.TryGetValue(item, out var label))
                {
                    box = (label.X, label.Y, label.Width, label.Height);
                }
                else if (selectionRing is { } sel && sel.Index == item.Index)
                {
                    // The ring's name IS this object's label this frame, so that is its box.
                    box = sel.LabelBox;
                    namedByRing = true;
                }
                drawn.Add(new DrawnOverlayObject(item.Index, item.ScreenX, item.ScreenY, item.Marker, box, namedByRing));
            }
            _drawnOverlayObjects = drawn.MoveToImmutable();
        }

        /// <summary>
        /// Render the caller-supplied <see cref="WcsAnnotation"/> through the active
        /// WCS using the renderer's existing primitives. Generic; knows nothing
        /// about polar alignment, plate-solve verification, etc.; just iterates the
        /// annotation list, projects each item via <see cref="WcsAnnotationLayer"/>,
        /// dispatches to <see cref="DrawCrossOverlay"/> or
        /// <see cref="DrawEllipseOverlay"/>.
        /// </summary>
        private void RenderWcsAnnotation(ViewerState state, WCS wcs)
        {
            if (ImageWidth <= 0 || ImageHeight <= 0) return;

            var layout = CurrentViewportLayout(state);

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

        /// <summary>
        /// Rings the SELECTED object and names it, at whatever rung of the context ladder the viewer
        /// is on.
        /// </summary>
        /// <remarks>
        /// <para><b>Independent of <see cref="ViewerState.ShowOverlays"/> on purpose.</b> The resolver
        /// behind a selection needs only a WCS and the catalogue, so an object can be selected with the
        /// overlay off -- and a selection that answers a click by drawing nothing is indistinguishable
        /// from a click that missed. The NAME is drawn beside the ring for the same reason: with the
        /// overlay off there is no label anywhere else, and a bare ring says "something" rather than
        /// "tau PsA".</para>
        /// <para><b>Two rings rather than one, and the accent colour rather than a marker colour.</b>
        /// A single ring in a marker's own palette is a marker; the pair plus the theme's
        /// <see cref="UiPalette.Accent"/> cannot be mistaken for one. Accent, not a literal, so Night
        /// mode gets a ring with no blue in it instead of a cyan one on a red-on-black sky.</para>
        /// <para>Drawn from the OBJECT's coordinates through the same
        /// <see cref="ViewportLayout"/> the markers use, so the ring lands on the marker rather than
        /// beside it -- and it is the object's position, never the click's, so it stays put when the
        /// pointer moves on.</para>
        /// <para><b>Solved before the overlay pass and drawn after it.</b> The geometry is one
        /// <see cref="SolveSelectionRing"/> per frame: the overlay needs the name's box first, to keep
        /// every label off it and to leave the selected object's own label out, while the ring itself
        /// has to be drawn LAST so it sits over that object's marker rather than under it.</para>
        /// </remarks>
        private void RenderSelectionHighlight(in SelectionRingGeometry ring)
        {
            var accent = ViewerTheme.Palette.Accent;

            DrawEllipseOverlay(ring.ScreenX, ring.ScreenY, ring.InnerMajor, ring.InnerMinor, ring.AngleRad, accent, 1.5f);
            DrawEllipseOverlay(ring.ScreenX, ring.ScreenY, ring.OuterMajor, ring.OuterMinor, ring.AngleRad, accent, 1.5f);

            if (!string.IsNullOrEmpty(FontPath))
            {
                DrawText(ring.Name, ring.LabelBox.X, ring.LabelBox.Y, FontSize * 0.85f, accent);
            }
        }

        /// <summary>
        /// The selection ring and its name, placed for this frame.
        /// </summary>
        /// <remarks>
        /// <see cref="Index"/> is the selected object's catalogue identity, so the overlay pass can tell
        /// which of its items the ring stands in for; a selection built without one (the panel's
        /// position-only payloads) simply has no label to step aside. <see cref="LabelBox"/> is in the
        /// overlay's own box vocabulary (top-left, width, height) because that is what it is reserved
        /// and hit-tested as.
        /// </remarks>
        private readonly record struct SelectionRingGeometry(
            CatalogIndex? Index,
            float ScreenX,
            float ScreenY,
            float InnerMajor,
            float InnerMinor,
            float OuterMajor,
            float OuterMinor,
            float AngleRad,
            string Name,
            (float X, float Y, float W, float H) LabelBox);

        /// <summary>
        /// Where the selection ring and its name land this frame, or null when there is no selection
        /// or it does not project.
        /// </summary>
        /// <remarks>
        /// An extended object is ringed by its OWN outline; only a star or a shapeless entry falls
        /// back to the circle pair, which is the atlas's rule and now this one's. The name sits to the
        /// right of the ring's widest point on the screen's X axis, which for a rotated ellipse is
        /// neither semi-axis but the projection of both.
        /// </remarks>
        private SelectionRingGeometry? SolveSelectionRing(ViewerState state, WCS wcs)
        {
            if (state.SelectedObject is not { } selection || ImageWidth <= 0 || ImageHeight <= 0)
            {
                return null;
            }

            if (wcs.SkyToPixel(selection.RA, selection.Dec) is not { } px)
            {
                return null;
            }

            var layout = CurrentViewportLayout(state);

            // Through the one mapping every WCS-drawn thing uses, so the ring lands on the object's
            // marker AND on the object's light -- the second is what the test measures.
            var (sx, sy) = WcsAnnotationLayer.ImageToScreen(px.X, px.Y, layout);
            var screenX = (float)sx;
            var screenY = (float)sy;

            float innerMajor, innerMinor, outerMajor, outerMinor, angleRad, halfWidth;
            if (TrySolveSelectionEllipse(in selection, in wcs, in layout) is { } ellipse)
            {
                (innerMajor, innerMinor, outerMajor, outerMinor, angleRad, halfWidth) = ellipse;
            }
            else
            {
                // The two concentric circles a shapeless selection gets.
                innerMajor = innerMinor = 9f * DpiScale;
                outerMajor = outerMinor = innerMajor + (3f * DpiScale);
                angleRad = 0f;
                halfWidth = outerMajor;
            }

            var labelSize = FontSize * 0.85f;
            var labelBox = (
                X: screenX + halfWidth + (4f * DpiScale),
                Y: screenY - (FontSize * 0.5f),
                W: MeasureText(selection.Name, labelSize),
                H: labelSize * 1.2f);

            return new SelectionRingGeometry(
                selection.Index, screenX, screenY,
                innerMajor, innerMinor, outerMajor, outerMinor, angleRad,
                selection.Name, labelBox);
        }

        /// <summary>
        /// The selection's ring as the object's OWN projected ellipse -- true axis ratio, true
        /// position angle -- or null for anything the catalogue gives no usable shape for, which
        /// then takes the circle pair.
        /// </summary>
        /// <returns>
        /// Both rings' semi-axes, their screen angle, and the outer ring's half-width on the screen's
        /// X axis, which is what the name has to clear.
        /// </returns>
        /// <remarks>
        /// <para><b>Every input is the one the [O] overlay already uses for the same object</b> --
        /// <see cref="OverlayEngine.ChooseMarkerKind"/>, the same arcmin-to-pixel conversion off
        /// <see cref="WCS.PixelScaleArcsec"/>, and <see cref="OverlayEngine.ComputeScreenPA"/> for
        /// the angle. That is what makes the ring sit concentric with the outline the overlay draws
        /// underneath it instead of merely near it, and it is why the shape is not re-derived from
        /// the CD matrix here: the overlay probes the WCS and so does this.</para>
        /// <para><b>The classifier gate is load-bearing, not defensive.</b> A star can carry a stray
        /// or cross-linked shape -- Antares sits inside the rho Ophiuchi dark-cloud complex -- and
        /// must still ring as a star rather than acquire a nebula's ellipse. Asking the same
        /// classifier the overlay markers ask is what keeps the two answers the same one.</para>
        /// <para><b>A pair, like the circles.</b> The outer ring is a UNIFORM scale of the inner, not
        /// a constant pixel offset, so an edge-on galaxy's 10:1 ratio survives it -- the same rule
        /// <see cref="OverlayEngine.EllipseLegibilityScale"/> exists to protect at the small end.</para>
        /// </remarks>
        private (float InnerMajor, float InnerMinor, float OuterMajor, float OuterMinor, float AngleRad, float HalfWidth)?
            TrySolveSelectionEllipse(in SkyMapInfoPanelData selection, in WCS wcs, in ViewportLayout layout)
        {
            if (selection.Shape is not { } shape
                || OverlayEngine.ChooseMarkerKind(selection.ObjType, hasShape: true)
                    != OverlayMarkerKind.Ellipse)
            {
                return null;
            }

            var majorArcmin = (double)shape.MajorAxis;
            if (double.IsNaN(majorArcmin) || majorArcmin <= 0.0)
            {
                return null;
            }

            // A catalogue entry with a major axis and no minor one is round, not degenerate.
            var minorArcmin = (double)shape.MinorAxis;
            var effectiveMinor = double.IsNaN(minorArcmin) || minorArcmin <= 0.0 ? majorArcmin : minorArcmin;

            var pixelScaleArcsec = wcs.PixelScaleArcsec;
            if (!double.IsFinite(pixelScaleArcsec) || pixelScaleArcsec <= 0.0)
            {
                return null;
            }

            var arcminToPixels = layout.Zoom / (pixelScaleArcsec / 60.0);
            var semiMajorPx = (float)(majorArcmin * 0.5 * arcminToPixels);
            var semiMinorPx = (float)(effectiveMinor * 0.5 * arcminToPixels);
            if (!float.IsFinite(semiMajorPx) || semiMajorPx <= 0f)
            {
                return null;
            }

            // Grows a too-small ellipse to a legibility floor and holds the slack that keeps the ring
            // outside the overlay's own outline -- one uniform factor, so the ratio is untouched.
            var inflate = OverlayEngine.EllipseLegibilityScale(
                semiMajorPx,
                OverlayEngine.SelectionMinSemiMajorPx * DpiScale,
                OverlayEngine.SelectionSlack);
            semiMajorPx *= inflate;
            semiMinorPx *= inflate;

            var angleRad = OverlayEngine.ComputeScreenPA(wcs, selection.RA, selection.Dec, shape.PositionAngle);

            // The outer ring is the inner one scaled so its MAJOR axis gains the same 3 px the circle
            // fallback's outer gains; the minor axis follows proportionally rather than by the same
            // absolute amount, which is what keeps an elongated object from rounding off.
            var outerScale = 1f + (3f * DpiScale / semiMajorPx);
            var outerMajorPx = semiMajorPx * outerScale;
            var outerMinorPx = semiMinorPx * outerScale;

            // The label clears the ring's widest point on the screen's X axis, which for a rotated
            // ellipse is neither semi-axis but the projection of both.
            var (sin, cos) = MathF.SinCos(angleRad);
            var halfWidth = MathF.Sqrt(
                (outerMajorPx * cos * (outerMajorPx * cos)) + (outerMinorPx * sin * (outerMinorPx * sin)));
            return (semiMajorPx, semiMinorPx, outerMajorPx, outerMinorPx, angleRad,
                float.IsFinite(halfWidth) ? halfWidth : outerMajorPx);
        }

        private static RGBAColor32 FloatToColor(float r, float g, float b, float a)
            => RGBAColor32.FromFloat(r, g, b, a);

    }
}
