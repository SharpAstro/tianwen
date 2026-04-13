using System;
using System.Collections.Generic;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic Sky Map tab. The heavy rendering (stars, lines, grid, horizon)
    /// is delegated to <see cref="RenderSkyMap"/> which the Vulkan subclass overrides
    /// to cache as a GPU texture. Text labels and overlays are drawn natively on top.
    /// </summary>
    public class SkyMapTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        private static readonly RGBAColor32 InfoPanelBg   = new(0x10, 0x10, 0x1C, 0xE0);
        private static readonly RGBAColor32 InfoText      = new(0xCC, 0xCC, 0xCC, 0xFF);
        private static readonly RGBAColor32 PlanetLabel   = new(0xFF, 0xEE, 0x88, 0xFF);
        private static readonly RGBAColor32 GridLabelColor = new(0x60, 0x80, 0xA0, 0xCC);

        private const float BaseFontSize = 12f;

        public SkyMapState State { get; } = new SkyMapState();
        private float _contentHeight;
        private float _contentX;
        private float _contentY;
        private float _contentWidth;
        private double _lastSiteLat = double.NaN;
        private double _lastSiteLon = double.NaN;

        public void Render(
            PlannerState plannerState,
            RectF32 contentRect,
            float dpiScale,
            string fontPath,
            ITimeProvider timeProvider)
        {
            BeginFrame();

            var db = plannerState.ObjectDb;
            if (db is null)
            {
                FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                    new RGBAColor32(0x06, 0x06, 0x10, 0xFF));
                DrawText("Loading star catalog...".AsSpan(), fontPath,
                    contentRect.X, contentRect.Y + contentRect.Height * 0.45f,
                    contentRect.Width, 30,
                    BaseFontSize * dpiScale, InfoText, TextAlign.Center, TextAlign.Center);
                return;
            }

            _contentHeight = contentRect.Height;
            _contentWidth = contentRect.Width;
            _contentX = contentRect.X;
            _contentY = contentRect.Y;
            var siteLat = plannerState.SiteLatitude;
            var siteLon = plannerState.SiteLongitude;

            // Initialize view to zenith on first valid site, or re-center on profile switch
            var site = SiteContext.Create(siteLat, siteLon, timeProvider);
            if (site.IsValid && (!State.Initialized || siteLat != _lastSiteLat || siteLon != _lastSiteLon))
            {
                _lastSiteLat = siteLat;
                _lastSiteLon = siteLon;
                State.Initialized = true;
                // Home position: look at the visible celestial pole (SCP for southern hemisphere, NCP for northern)
                State.CenterRA = site.LST;
                State.CenterDec = siteLat < 0 ? -89.0 : 89.0;
                State.NeedsRedraw = true;
            }

            // Delegate pixel rendering to virtual method (overridden by VkSkyMapTab for GPU caching)
            RenderSkyMap(db, contentRect, fontPath, timeProvider, siteLat, siteLon);

            // Draw text overlays natively (GPU text rendering on top of cached texture)
            var ppr = SkyMapProjection.PixelsPerRadian(contentRect.Height, State.FieldOfViewDeg);
            var cx = contentRect.X + contentRect.Width * 0.5f;
            var cy = contentRect.Y + contentRect.Height * 0.5f;
            var fontSize = BaseFontSize * dpiScale;

            if (State.ShowGrid)
            {
                DrawGridLabels(contentRect, fontPath, fontSize * 0.8f, ppr, cx, cy);
            }

            // Constellation names at boundary centroids (always shown)
            DrawConstellationNames(contentRect, fontPath, fontSize * 0.85f, ppr, cx, cy);

            if (State.ShowPlanets)
            {
                DrawPlanetLabels(db, timeProvider, siteLat, siteLon, contentRect, fontPath, fontSize, ppr, cx, cy);
            }

            DrawInfoStrip(contentRect, fontPath, fontSize, dpiScale, cx, cy);

            // Crosshair
            var crossColor = new RGBAColor32(0xFF, 0xFF, 0xFF, 0x40);
            FillRect(cx - 10, cy, 20, 1, crossColor);
            FillRect(cx, cy - 10, 1, 20, crossColor);
        }

        /// <summary>
        /// Override in GPU subclass to render to a cached texture.
        /// Base implementation fills background only.
        /// </summary>
        protected virtual void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect, string fontPath,
            ITimeProvider timeProvider, double siteLat, double siteLon)
        {
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                new RGBAColor32(0x06, 0x06, 0x10, 0xFF));
        }

        // ── Text overlay methods (use native GPU DrawText, drawn on top of cached texture) ──

        /// <summary>
        /// Place grid labels where grid lines intersect the viewport edges,
        /// matching the FITS viewer WCS overlay approach.
        /// </summary>
        private void DrawGridLabels(RectF32 rect, string fontPath, float fontSize, double ppr, float cx, float cy)
        {
            var fov = State.FieldOfViewDeg;
            var raStep = fov switch { < 5 => 0.5, < 15 => 1.0, < 40 => 2.0, < 90 => 3.0, _ => 6.0 };
            var decStep = fov switch { < 5 => 5.0, < 15 => 10.0, < 40 => 15.0, _ => 30.0 };
            var labelH = fontSize * 1.3f;
            var margin = 4f;

            // RA labels: find where each constant-RA line crosses the viewport edge
            for (var ra = 0.0; ra < 24.0; ra += raStep)
            {
                if (FindEdgeCrossing(ra, true, rect, ppr, cx, cy, out var lx, out var ly, out var edge))
                {
                    var label = raStep < 1 ? $"{ra:F1}h" : $"{ra:F0}h";
                    // Place label near the edge crossing, offset inward
                    var textX = edge == Edge.Right ? lx - 45 : lx + margin;
                    var textY = edge == Edge.Bottom ? ly - labelH : ly + margin;
                    DrawText(label.AsSpan(), fontPath,
                        textX, textY, 50, labelH,
                        fontSize, GridLabelColor, TextAlign.Near, TextAlign.Near);
                }
            }

            // Dec labels: find where each constant-Dec line crosses the viewport edge
            for (var dec = -90.0 + decStep; dec < 90.0; dec += decStep)
            {
                if (FindEdgeCrossing(dec, false, rect, ppr, cx, cy, out var lx, out var ly, out var edge))
                {
                    var label = dec >= 0 ? $"+{dec:F0}\u00B0" : $"{dec:F0}\u00B0";
                    var textX = edge == Edge.Right ? lx - 45 : lx + margin;
                    var textY = edge == Edge.Bottom ? ly - labelH : ly + margin;
                    DrawText(label.AsSpan(), fontPath,
                        textX, textY, 50, labelH,
                        fontSize, GridLabelColor, TextAlign.Near, TextAlign.Near);
                }
            }
        }

        private enum Edge { Top, Bottom, Left, Right }

        /// <summary>
        /// Trace a grid line and find where it crosses a viewport edge (entry or exit).
        /// For RA lines (isRA=true): traces Dec from -90 to +90 at constant RA.
        /// For Dec lines (isRA=false): traces RA from 0 to 24 at constant Dec.
        /// Returns the crossing point nearest to a viewport edge for label placement.
        /// </summary>
        private bool FindEdgeCrossing(double value, bool isRA, RectF32 rect, double ppr,
            float cx, float cy, out float crossX, out float crossY, out Edge edge)
        {
            crossX = 0;
            crossY = 0;
            edge = Edge.Top;

            const int steps = 80;
            var prevInside = false;
            var prevSx = 0f;
            var prevSy = 0f;
            var prevValid = false;

            for (var i = 0; i <= steps; i++)
            {
                double ra, dec;
                if (isRA)
                {
                    ra = value;
                    dec = -90.0 + i * 180.0 / steps;
                }
                else
                {
                    ra = i * 24.0 / steps;
                    dec = value;
                }

                if (!SkyMapProjection.Project(ra, dec, State.CenterRA, State.CenterDec,
                    ppr, cx, cy, out var sx, out var sy))
                {
                    prevValid = false;
                    prevInside = false;
                    continue;
                }

                var inside = sx >= rect.X && sx < rect.X + rect.Width
                          && sy >= rect.Y && sy < rect.Y + rect.Height;

                // Detect any crossing (entry or exit)
                if (prevValid && inside != prevInside)
                {
                    // Use the point that's inside the viewport
                    crossX = inside ? sx : prevSx;
                    crossY = inside ? sy : prevSy;

                    // Determine which edge based on the crossing point's position
                    var edgeX = crossX;
                    var edgeY = crossY;
                    var distTop = edgeY - rect.Y;
                    var distBot = rect.Y + rect.Height - edgeY;
                    var distLeft = edgeX - rect.X;
                    var distRight = rect.X + rect.Width - edgeX;
                    var minDist = Math.Min(Math.Min(distTop, distBot), Math.Min(distLeft, distRight));

                    if (minDist == distTop) edge = Edge.Top;
                    else if (minDist == distBot) edge = Edge.Bottom;
                    else if (minDist == distLeft) edge = Edge.Left;
                    else edge = Edge.Right;

                    return true;
                }

                prevSx = sx;
                prevSy = sy;
                prevInside = inside;
                prevValid = true;
            }

            return false;
        }


        private static readonly RGBAColor32 ConstellNameColor = new(0x60, 0x90, 0x60, 0xB0);

        /// <summary>
        /// Draw constellation names at the centroid of each constellation's boundary strips.
        /// </summary>
        private void DrawConstellationNames(RectF32 rect, string fontPath, float fontSize, double ppr, float cx, float cy)
        {
            // Compute centroid of each constellation from its boundary strips
            var centroids = new Dictionary<Constellation, (double RaSum, double DecSum, int Count)>();

            foreach (var b in ConstellationBoundary.Table)
            {
                var midRA = (b.LowerRA + b.UpperRA) * 0.5;
                var midDec = b.LowerDec + 2.0; // approximate — offset above lower dec boundary

                if (!centroids.TryGetValue(b.Constellation, out var c))
                {
                    c = (0, 0, 0);
                }
                centroids[b.Constellation] = (c.RaSum + midRA, c.DecSum + midDec, c.Count + 1);
            }

            foreach (var (constellation, (raSum, decSum, count)) in centroids)
            {
                var avgRA = raSum / count;
                var avgDec = decSum / count;

                if (SkyMapProjection.Project(avgRA, avgDec, State.CenterRA, State.CenterDec,
                    ppr, cx, cy, out var sx, out var sy)
                    && sx >= rect.X && sx < rect.X + rect.Width
                    && sy >= rect.Y && sy < rect.Y + rect.Height)
                {
                    var name = constellation.ToName();
                    var (tw, _) = Renderer.MeasureText(name, fontPath, fontSize);
                    DrawText(name.AsSpan(), fontPath,
                        sx - tw * 0.5f, sy - fontSize * 0.5f, tw + 4, fontSize * 1.2f,
                        fontSize, ConstellNameColor, TextAlign.Center, TextAlign.Center);
                }
            }
        }

        private void DrawPlanetLabels(
            ICelestialObjectDB db, ITimeProvider timeProvider, double siteLat, double siteLon,
            RectF32 rect, string fontPath, float fontSize, double ppr, float cx, float cy)
        {
            var now = timeProvider.GetUtcNow();

            foreach (var planetIdx in SkyMapRenderer.PlanetIndices)
            {
                if (!TianWen.Lib.Astrometry.VSOP87.VSOP87a.Reduce(
                    planetIdx, now, siteLat, siteLon,
                    out var ra, out var dec, out _, out _, out _))
                {
                    continue;
                }

                var raHours = ra / 15.0;
                if (SkyMapProjection.Project(raHours, dec, State.CenterRA, State.CenterDec,
                    ppr, cx, cy, out var sx, out var sy)
                    && sx >= rect.X && sx < rect.X + rect.Width
                    && sy >= rect.Y && sy < rect.Y + rect.Height)
                {
                    var name = planetIdx == CatalogIndex.Moon ? "Moon"
                        : db.TryLookupByIndex(planetIdx, out var obj) ? obj.DisplayName : "?";
                    DrawText(name.AsSpan(), fontPath,
                        sx + 10, sy - fontSize, 100, fontSize * 1.2f,
                        fontSize, PlanetLabel, TextAlign.Near, TextAlign.Center);
                }
            }
        }

        private void DrawInfoStrip(RectF32 rect, string fontPath, float fontSize, float dpiScale, float cx, float cy)
        {
            var stripH = 24f * dpiScale;
            var stripY = rect.Y + rect.Height - stripH;
            FillRect(rect.X, stripY, rect.Width, stripH, InfoPanelBg);

            var fovText = State.FieldOfViewDeg < 1
                ? $"FOV: {State.FieldOfViewDeg * 60:F0}'"
                : $"FOV: {State.FieldOfViewDeg:F1}\u00B0";
            var info = $"RA: {State.CenterRA:F2}h  Dec: {State.CenterDec:F1}\u00B0    {fovText}    [H]orizon [G]rid [B]oundaries [C]onst [P]lanets";

            DrawText(info.AsSpan(), fontPath,
                rect.X + 8, stripY, rect.Width - 16, stripH,
                fontSize * 0.85f, InfoText, TextAlign.Near, TextAlign.Center);
        }

        // ── Input handling ──

        public override bool HandleInput(InputEvent evt) => evt switch
        {
            InputEvent.Scroll(var scrollY, _, _, _) => HandleZoom(scrollY),
            InputEvent.MouseDown(var x, var y, _, _, _) => HandleDragStart(x, y),
            InputEvent.MouseUp(_, _, _) => HandleDragEnd(),
            InputEvent.MouseMove(var x, var y) when State.IsDragging => HandleDrag(x, y),
            InputEvent.KeyDown(var key, _) => HandleKey(key),
            _ => false
        };

        private bool HandleZoom(float scrollY)
        {
            var factor = scrollY > 0 ? 0.85 : 1.0 / 0.85;
            State.FieldOfViewDeg = Math.Clamp(State.FieldOfViewDeg * factor, 0.5, 180.0);
            State.NeedsRedraw = true;
            return true;
        }

        private bool HandleDragStart(float x, float y)
        {
            State.IsDragging = true;
            State.DragStart = (x, y);
            State.DragStartCenter = (State.CenterRA, State.CenterDec);
            return true;
        }

        private bool HandleDragEnd()
        {
            State.IsDragging = false;
            State.NeedsRedraw = true;
            return true;
        }

        private bool HandleDrag(float x, float y)
        {
            // Stellarium approach: unproject both mouse positions to sky coordinates
            var ppr = SkyMapProjection.PixelsPerRadian(_contentHeight, State.FieldOfViewDeg);
            var screenCx = _contentX + _contentWidth * 0.5f;
            var screenCy = _contentY + _contentHeight * 0.5f;

            var (startX, startY) = State.DragStart;
            var (startRA, startDec) = State.DragStartCenter;

            var (ra1, dec1) = SkyMapProjection.Unproject(startX, startY, startRA, startDec, ppr, screenCx, screenCy);
            var (ra2, dec2) = SkyMapProjection.Unproject(x, y, startRA, startDec, ppr, screenCx, screenCy);

            State.CenterRA = startRA + (ra1 - ra2);
            State.CenterDec = startDec + (dec1 - dec2);
            State.NormalizeCenter();
            State.NeedsRedraw = true;
            return true;
        }

        private bool HandleKey(InputKey key)
        {
            switch (key)
            {
                case InputKey.G:
                    State.ShowGrid = !State.ShowGrid;
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.H:
                    State.ShowHorizon = !State.ShowHorizon;
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.B:
                    State.ShowConstellationBoundaries = !State.ShowConstellationBoundaries;
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.C:
                    State.ShowConstellationFigures = !State.ShowConstellationFigures;
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.P:
                    State.ShowPlanets = !State.ShowPlanets;
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.Plus:
                    State.MagnitudeLimit = Math.Min(State.MagnitudeLimit + 0.5f, 12f);
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.Minus:
                    State.MagnitudeLimit = Math.Max(State.MagnitudeLimit - 0.5f, 1f);
                    State.NeedsRedraw = true;
                    return true;
                default:
                    return false;
            }
        }
    }
}
