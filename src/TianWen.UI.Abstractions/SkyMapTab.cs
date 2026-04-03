using System;
using System.Collections.Generic;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;

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
        private static readonly RGBAColor32 ConstellLabel = new(0x50, 0x70, 0xA0, 0xC0);
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
            TimeProvider timeProvider)
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
            if (!double.IsNaN(siteLat) && !double.IsNaN(siteLon))
            {
                if (!State.Initialized || siteLat != _lastSiteLat || siteLon != _lastSiteLon)
                {
                    _lastSiteLat = siteLat;
                    _lastSiteLon = siteLon;
                    State.Initialized = true;
                    // Home position: look at the visible celestial pole (SCP for southern hemisphere, NCP for northern)
                    State.CenterRA = SkyMapRenderer.ComputeLST(timeProvider.GetUtcNow(), siteLon);
                    State.CenterDec = siteLat < 0 ? -89.0 : 89.0;
                    State.NeedsRedraw = true;
                }
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

            if (State.ShowConstellationNames)
            {
                DrawConstellationLabels(db, contentRect, fontPath, fontSize * 0.9f, ppr, cx, cy);
            }

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
            TimeProvider timeProvider, double siteLat, double siteLon)
        {
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                new RGBAColor32(0x06, 0x06, 0x10, 0xFF));
        }

        // ── Text overlay methods (use native GPU DrawText, drawn on top of cached texture) ──

        private void DrawGridLabels(RectF32 rect, string fontPath, float fontSize, double ppr, float cx, float cy)
        {
            var fov = State.FieldOfViewDeg;
            var halfFov = fov * 0.6; // slight margin beyond viewport

            // RA labels: only for RA values within the visible FOV range
            var raStep = fov switch { < 5 => 0.5, < 15 => 1.0, < 40 => 2.0, < 90 => 3.0, _ => 6.0 };
            // RA range visible: depends on Dec (cos correction)
            var cosDec = Math.Max(0.1, Math.Cos(State.CenterDec * Math.PI / 180.0));
            var raHalfRange = halfFov / 15.0 / cosDec; // hours

            for (var ra = 0.0; ra < 24.0; ra += raStep)
            {
                // Angular distance in RA from center
                var dRA = ra - State.CenterRA;
                if (dRA > 12) dRA -= 24;
                if (dRA < -12) dRA += 24;
                if (Math.Abs(dRA) > raHalfRange)
                {
                    continue;
                }

                if (SkyMapProjection.Project(ra, State.CenterDec, State.CenterRA, State.CenterDec,
                    ppr, cx, cy, out var lx, out _)
                    && lx >= rect.X && lx < rect.X + rect.Width - 30)
                {
                    var label = raStep < 1 ? $"{ra:F1}h" : $"{ra:F0}h";
                    DrawText(label.AsSpan(), fontPath,
                        lx + 2, rect.Y + 2, 50, fontSize * 1.2f,
                        fontSize, GridLabelColor, TextAlign.Near, TextAlign.Near);
                }
            }

            // Dec labels: only for Dec values within the visible FOV range
            var decStep = fov switch { < 5 => 5.0, < 15 => 10.0, < 40 => 15.0, _ => 30.0 };

            for (var dec = -90.0; dec <= 90.0; dec += decStep)
            {
                if (Math.Abs(dec - State.CenterDec) > halfFov)
                {
                    continue;
                }

                if (SkyMapProjection.Project(State.CenterRA, dec, State.CenterRA, State.CenterDec,
                    ppr, cx, cy, out _, out var ly)
                    && ly >= rect.Y && ly < rect.Y + rect.Height - 14)
                {
                    var label = dec >= 0 ? $"+{dec:F0}\u00B0" : $"{dec:F0}\u00B0";
                    DrawText(label.AsSpan(), fontPath,
                        rect.X + 2, ly + 2, 50, fontSize * 1.2f,
                        fontSize, GridLabelColor, TextAlign.Near, TextAlign.Near);
                }
            }
        }

        private void DrawConstellationLabels(
            ICelestialObjectDB db, RectF32 rect, string fontPath, float fontSize, double ppr, float cx, float cy)
        {
            var seen = new HashSet<Constellation>();
            foreach (var seg in ConstellationLines.Segments)
            {
                if (!seen.Add(seg.Constellation))
                {
                    continue;
                }

                if (!db.TryLookupByIndex(seg.Constellation.GetBrighestStar(), out var starObj))
                {
                    continue;
                }

                if (SkyMapProjection.Project(starObj.RA, starObj.Dec, State.CenterRA, State.CenterDec,
                    ppr, cx, cy, out var sx, out var sy)
                    && sx >= rect.X && sx < rect.X + rect.Width
                    && sy >= rect.Y && sy < rect.Y + rect.Height)
                {
                    DrawText(seg.Constellation.ToIAUAbbreviation().AsSpan(), fontPath,
                        sx + 10, sy - fontSize, 100, fontSize * 1.2f,
                        fontSize, ConstellLabel, TextAlign.Near, TextAlign.Center);
                }
            }
        }

        private void DrawPlanetLabels(
            ICelestialObjectDB db, TimeProvider timeProvider, double siteLat, double siteLon,
            RectF32 rect, string fontPath, float fontSize, double ppr, float cx, float cy)
        {
            var now = timeProvider.GetUtcNow();
            ReadOnlySpan<CatalogIndex> planets =
            [
                CatalogIndex.Mercury, CatalogIndex.Venus, CatalogIndex.Mars,
                CatalogIndex.Jupiter, CatalogIndex.Saturn, CatalogIndex.Uranus,
                CatalogIndex.Neptune, CatalogIndex.Moon
            ];

            foreach (var planetIdx in planets)
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
            var info = $"RA: {State.CenterRA:F2}h  Dec: {State.CenterDec:F1}\u00B0    {fovText}    [G]rid [C]onst [P]lanets [N]ames";

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
                case InputKey.C:
                    State.ShowConstellationLines = !State.ShowConstellationLines;
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.P:
                    State.ShowPlanets = !State.ShowPlanets;
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.N:
                    State.ShowConstellationNames = !State.ShowConstellationNames;
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
