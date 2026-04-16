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
        private PlannerState? _plannerState;
        private ITimeProvider? _timeProvider;

        // Cached live viewing time -- refreshed once per second to avoid per-frame GetUtcNow() calls.
        // GetTimestamp() is a cheap stopwatch read; GetUtcNow() is a heavier system call.
        private DateTimeOffset _cachedLiveTime;
        private long _liveTimeRefreshTicks;
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
            _plannerState = plannerState;
            _timeProvider = timeProvider;

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

            // Viewing time: use the planning date (which preserves time-of-day across
            // date shifts) when set, otherwise live wall-clock time cached with 1s refresh.
            var nowTicks = timeProvider.GetTimestamp();
            if (plannerState.PlanningDate is null
                && timeProvider.GetElapsedTime(_liveTimeRefreshTicks, nowTicks) >= TimeSpan.FromSeconds(1))
            {
                _cachedLiveTime = timeProvider.GetUtcNow();
                _liveTimeRefreshTicks = nowTicks;
            }
            var viewingTime = plannerState.PlanningDate?.ToUniversalTime() ?? _cachedLiveTime;

            // Initialize view to zenith on first valid site, or re-center on profile switch
            var site = SiteContext.Create(siteLat, siteLon, viewingTime);
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
            RenderSkyMap(db, contentRect, fontPath, viewingTime, siteLat, siteLon);

            // Draw text overlays natively (GPU text rendering on top of cached texture)
            var ppr = SkyMapProjection.PixelsPerRadian(contentRect.Height, State.FieldOfViewDeg);
            var cx = contentRect.X + contentRect.Width * 0.5f;
            var cy = contentRect.Y + contentRect.Height * 0.5f;
            var fontSize = BaseFontSize * dpiScale;

            if (State.ShowGrid)
            {
                DrawGridLabels(contentRect, fontPath, fontSize * 0.8f, ppr, cx, cy);
            }

            // Horizon dimming: when the user has horizon clipping on, sub-horizon labels
            // get their alpha cut so they clearly read as "not currently visible" without
            // being hidden entirely (user can still see where a constellation will rise).
            var dimBelowHorizon = State.ShowHorizon && site.IsValid;

            // Constellation names at boundary centroids (always shown)
            DrawConstellationNames(contentRect, fontPath, fontSize * 0.85f, ppr, cx, cy, site, dimBelowHorizon);

            DrawPlanetLabels(db, viewingTime, siteLat, siteLon, contentRect, fontPath, fontSize, ppr, cx, cy, site, dimBelowHorizon);

            // Always render the object overlay pass — when [O] is off, only pinned
            // planner targets are drawn (the user's planned observations should always
            // be visible as landmarks on the sky map). The showAll flag tells the engine
            // whether to include non-pinned catalog objects in the result.
            RenderObjectOverlay(db, contentRect, dpiScale, fontPath, BaseFontSize, site, dimBelowHorizon, plannerState, State.ShowObjectOverlay);

            // Mosaic panel grid — drawn BEHIND the mount reticle but ON TOP of catalog
            // overlays so panel outlines don't get buried under catalog markers but the
            // reticle crosshair remains the topmost element.
            if (State.MosaicPanels.Length > 0 && State.MountOverlay is { SensorFovDeg: not null })
            {
                RenderMosaicPanels(contentRect, dpiScale, ppr, cx, cy);
            }

            // Mount reticle on top of everything catalog-related so it's never buried.
            if (State.ShowMountOverlay && State.MountOverlay is { } mountOverlay)
            {
                RenderMountOverlay(mountOverlay, contentRect, dpiScale, fontPath, BaseFontSize, ppr, cx, cy);
            }

            DrawInfoStrip(contentRect, fontPath, fontSize, dpiScale, cx, cy,
                viewingTime, plannerState.SiteTimeZone, plannerState.PlanningDate.HasValue);

            // Crosshair
            var crossColor = new RGBAColor32(0xFF, 0xFF, 0xFF, 0x40);
            FillRect(cx - 10, cy, 20, 1, crossColor);
            FillRect(cx, cy - 10, 1, 20, crossColor);
        }

        /// <summary>
        /// Override in the GPU subclass to draw the <c>[O]</c> object overlay (ellipses
        /// for DSOs, crosses for named stars, plus labels with collision avoidance).
        /// Base implementation is a no-op — the software / TUI fallback does not render
        /// shape markers. Uses <see cref="Overlays.OverlayEngine.ComputeSkyMapOverlays"/>
        /// to compute items and <see cref="Overlays.OverlayEngine.PlaceLabels"/> for label
        /// placement, both shared with the FITS viewer.
        /// </summary>
        protected virtual void RenderObjectOverlay(
            ICelestialObjectDB db, RectF32 contentRect, float dpiScale, string fontPath,
            float baseFontSize, SiteContext site, bool dimBelowHorizon, PlannerState plannerState,
            bool showAllOverlays)
        {
        }

        /// <summary>
        /// Override in the GPU subclass to draw mosaic panel outlines for pinned targets
        /// whose catalog shape exceeds the sensor FOV. Each panel is a thin semi-transparent
        /// rectangle at the panel's RA/Dec, sized by the sensor FOV.
        /// </summary>
        protected virtual void RenderMosaicPanels(
            RectF32 contentRect, float dpiScale, double ppr, float cx, float cy)
        {
        }

        /// <summary>
        /// Override in the GPU subclass to draw the Stellarium-style mount reticle at
        /// the connected mount's current J2000 pointing. Base implementation is a no-op.
        /// Called after the object overlay so the reticle is drawn on top of catalog
        /// markers — the mount position should never be buried under label clutter.
        /// </summary>
        protected virtual void RenderMountOverlay(
            SkyMapMountOverlay mountOverlay, RectF32 contentRect, float dpiScale,
            string fontPath, float baseFontSize, double ppr, float cx, float cy)
        {
        }

        /// <summary>
        /// Override in GPU subclass to render to a cached texture.
        /// Base implementation fills background only.
        /// </summary>
        protected virtual void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect, string fontPath,
            DateTimeOffset viewingTime, double siteLat, double siteLon)
        {
            double sunAltDeg = State.GetSunAltitudeDegCached(viewingTime, siteLat, siteLon);
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                SkyMapState.SkyBackgroundColorForSunAltitude(sunAltDeg));
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

                if (!SkyMapProjection.ProjectWithMatrix(ra, dec, State.CurrentViewMatrix,
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
        /// <summary>
        /// Return <paramref name="color"/> scaled down to ~35% alpha if <paramref name="dim"/> is true.
        /// Used to make sub-horizon labels still readable but clearly "not tonight" visually.
        /// </summary>
        private static RGBAColor32 DimmedIf(RGBAColor32 color, bool dim)
            => dim ? new RGBAColor32(color.Red, color.Green, color.Blue, (byte)(color.Alpha * 0.35f)) : color;

        private void DrawConstellationNames(RectF32 rect, string fontPath, float fontSize, double ppr, float cx, float cy,
            SiteContext site, bool dimBelowHorizon)
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

                if (SkyMapProjection.ProjectWithMatrix(avgRA, avgDec, State.CurrentViewMatrix,
                    ppr, cx, cy, out var sx, out var sy)
                    && sx >= rect.X && sx < rect.X + rect.Width
                    && sy >= rect.Y && sy < rect.Y + rect.Height)
                {
                    var name = constellation.ToName();
                    var (tw, _) = Renderer.MeasureText(name, fontPath, fontSize);
                    var belowHorizon = dimBelowHorizon && !site.IsAboveHorizon(avgRA, avgDec);
                    DrawText(name.AsSpan(), fontPath,
                        sx - tw * 0.5f, sy - fontSize * 0.5f, tw + 4, fontSize * 1.2f,
                        fontSize, DimmedIf(ConstellNameColor, belowHorizon), TextAlign.Center, TextAlign.Center);
                }
            }
        }

        private void DrawPlanetLabels(
            ICelestialObjectDB db, DateTimeOffset viewingTime, double siteLat, double siteLon,
            RectF32 rect, string fontPath, float fontSize, double ppr, float cx, float cy,
            SiteContext site, bool dimBelowHorizon)
        {

            foreach (var planetIdx in SkyMapRenderer.PlanetIndices)
            {
                // J2000 RA/Dec — the sky map projects everything in J2000, so using the
                // regular Reduce (which precesses to JNow + applies topocentric correction)
                // would offset planets ~0.35 deg off the J2000 ecliptic line.
                if (!TianWen.Lib.Astrometry.VSOP87.VSOP87a.ReduceJ2000(
                    planetIdx, viewingTime,
                    out var ra, out var dec, out _))
                {
                    continue;
                }

                if (SkyMapProjection.ProjectWithMatrix(ra, dec, State.CurrentViewMatrix,
                    ppr, cx, cy, out var sx, out var sy)
                    && sx >= rect.X && sx < rect.X + rect.Width
                    && sy >= rect.Y && sy < rect.Y + rect.Height)
                {
                    var name = planetIdx == CatalogIndex.Moon ? "Moon"
                        : planetIdx == CatalogIndex.Sol ? "Sun"
                        : db.TryLookupByIndex(planetIdx, out var obj) ? obj.DisplayName : "?";
                    var belowHorizon = dimBelowHorizon && !site.IsAboveHorizon(ra, dec);
                    var planetColor = DimmedIf(SkyMapRenderer.GetPlanetColor(planetIdx), belowHorizon);

                    // Filled dot at the planet position — radius scales with planet type
                    var dotRadius = planetIdx is CatalogIndex.Sol or CatalogIndex.Moon ? 4f
                        : planetIdx is CatalogIndex.Jupiter or CatalogIndex.Saturn ? 3f
                        : 2f;
                    FillCircle(sx, sy, dotRadius, planetColor);

                    DrawText(name.AsSpan(), fontPath,
                        sx + 10, sy - fontSize, 100, fontSize * 1.2f,
                        fontSize, planetColor, TextAlign.Near, TextAlign.Center);
                }
            }
        }

        private static readonly RGBAColor32 TimeShiftColor = new(0x88, 0xCC, 0xFF, 0xFF);

        private void DrawInfoStrip(RectF32 rect, string fontPath, float fontSize, float dpiScale,
            float cx, float cy, DateTimeOffset viewingTime, TimeSpan siteTimeZone, bool isTimeShifted)
        {
            var stripH = 24f * dpiScale;
            var stripY = rect.Y + rect.Height - stripH;
            FillRect(rect.X, stripY, rect.Width, stripH, InfoPanelBg);

            var localTime = viewingTime.ToOffset(siteTimeZone);
            var timeText = isTimeShifted
                ? $"{localTime:yyyy-MM-dd HH:mm}"
                : $"{localTime:HH:mm:ss}";

            var fovText = State.FieldOfViewDeg < 1
                ? $"FOV: {State.FieldOfViewDeg * 60:F0}'"
                : $"FOV: {State.FieldOfViewDeg:F1}\u00B0";
            var modeLabel = State.Mode == SkyMapMode.Equatorial ? "EQ" : "AZ";
            var info = $"RA: {State.CenterRA:F2}h  Dec: {State.CenterDec:F1}\u00B0    {fovText}    [{modeLabel}]  [H]orizon [G]rid [A]lt/Az [B]oundaries [C]onst [P]roj [O]bjects [M]ount";

            DrawText(info.AsSpan(), fontPath,
                rect.X + 8, stripY, rect.Width - 16, stripH,
                fontSize * 0.85f, InfoText, TextAlign.Near, TextAlign.Center);

            // Time display on the right side of the strip -- blue when time-shifted
            var timeColor = isTimeShifted ? TimeShiftColor : InfoText;
            DrawText(timeText.AsSpan(), fontPath,
                rect.X + rect.Width - 160, stripY, 152, stripH,
                fontSize * 0.85f, timeColor, TextAlign.Far, TextAlign.Center);
        }

        // ── Input handling ──

        public override bool HandleInput(InputEvent evt) => evt switch
        {
            InputEvent.Scroll(var scrollY, var mx, var my, _) => HandleZoom(scrollY, mx, my),
            InputEvent.Pinch(var scale, var px, var py) => HandlePinchZoom(scale, px, py),
            InputEvent.PinchEnd => HandlePinchEnd(),
            InputEvent.MouseDown(var x, var y, _, _, _) => HandleDragStart(x, y),
            InputEvent.MouseUp(_, _, _) => HandleDragEnd(),
            InputEvent.MouseMove(var x, var y) when State.IsDragging && !State.IsPinching => HandleDrag(x, y),
            InputEvent.KeyDown(var key, _) => HandleKey(key),
            _ => false
        };

        private bool HandlePinchZoom(float scale, float centerX, float centerY)
        {
            if (!State.IsPinching)
            {
                // Pinch start — suppress drag, undo any drag the first finger caused
                State.IsPinching = true;
                if (State.IsDragging)
                {
                    var (startRA, startDec) = State.DragStartCenter;
                    State.CenterRA = startRA;
                    State.CenterDec = startDec;
                    State.IsDragging = false;
                }
            }

            // Convert relative per-frame pinch scale to proportional zoom
            // scale ~1.01 per frame → small zoom step
            return HandleZoomByFactor(1.0 / scale, centerX, centerY);
        }

        private bool HandlePinchEnd()
        {
            State.IsPinching = false;
            return true;
        }

        private bool HandleZoom(float scrollY, float mouseX, float mouseY)
        {
            // Scale proportionally to scroll magnitude — each unit ≈ 15% zoom
            var factor = Math.Pow(0.85, scrollY);
            return HandleZoomByFactor(factor, mouseX, mouseY);
        }

        private bool HandleZoomByFactor(double factor, float mouseX, float mouseY)
        {

            // Center-point zoom: zoom toward the sky position under the mouse cursor
            var ppr = SkyMapProjection.PixelsPerRadian(_contentHeight, State.FieldOfViewDeg);
            var screenCx = _contentX + _contentWidth * 0.5f;
            var screenCy = _contentY + _contentHeight * 0.5f;

            // Sky position under the mouse before zoom
            var (mouseRA, mouseDec) = SkyMapProjection.UnprojectWithMatrix(
                mouseX, mouseY, State.CurrentViewMatrix, ppr, screenCx, screenCy);

            // Apply zoom
            State.FieldOfViewDeg = Math.Clamp(State.FieldOfViewDeg * factor, 0.5, 180.0);

            // Recompute ppr after zoom and find where the mouse sky position would end up
            var newPpr = SkyMapProjection.PixelsPerRadian(_contentHeight, State.FieldOfViewDeg);
            SkyMapProjection.ProjectWithMatrix(mouseRA, mouseDec, State.CurrentViewMatrix,
                newPpr, screenCx, screenCy, out var newMx, out var newMy);

            // Shift the view center so the mouse sky position stays under the cursor
            // by unprojecting the delta
            if (!float.IsNaN(newMx))
            {
                var (centerRA, centerDec) = SkyMapProjection.UnprojectWithMatrix(
                    screenCx + (mouseX - newMx), screenCy + (mouseY - newMy),
                    State.CurrentViewMatrix, newPpr, screenCx, screenCy);
                State.CenterRA = centerRA;
                State.CenterDec = centerDec;
                State.NormalizeCenter();
            }

            State.NeedsRedraw = true;
            return true;
        }

        private bool HandleDragStart(float x, float y)
        {
            State.IsDragging = true;
            State.DragStart = (x, y);
            State.DragStartCenter = (State.CenterRA, State.CenterDec);
            State.DragStartViewMatrix = State.CurrentViewMatrix;
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
            // Great-circle drag: compute the rotation that maps the current mouse sky position
            // back to the drag-start sky position, and apply it to the view center.
            // This gives a natural "grab and drag" feel in any projection mode.
            var ppr = SkyMapProjection.PixelsPerRadian(_contentHeight, State.FieldOfViewDeg);
            var screenCx = _contentX + _contentWidth * 0.5f;
            var screenCy = _contentY + _contentHeight * 0.5f;

            var (startX, startY) = State.DragStart;
            var (startRA, startDec) = State.DragStartCenter;
            var startMatrix = State.DragStartViewMatrix;

            var (ra1, dec1) = SkyMapProjection.UnprojectWithMatrix(startX, startY, startMatrix, ppr, screenCx, screenCy);
            var (ra2, dec2) = SkyMapProjection.UnprojectWithMatrix(x, y, startMatrix, ppr, screenCx, screenCy);

            // Convert to unit vectors
            var v1 = SkyMapState.RaDecToUnitVec(ra1, dec1);
            var v2 = SkyMapState.RaDecToUnitVec(ra2, dec2);
            var vc = SkyMapState.RaDecToUnitVec(startRA, startDec);

            // Build quaternion rotation from v2 to v1: q = (cross(v2,v1), 1 + dot(v2,v1))
            var from = new System.Numerics.Vector3(v2.X, v2.Y, v2.Z);
            var to = new System.Numerics.Vector3(v1.X, v1.Y, v1.Z);
            var cross = System.Numerics.Vector3.Cross(from, to);
            var dot = System.Numerics.Vector3.Dot(from, to);
            var q = System.Numerics.Quaternion.Normalize(
                new System.Numerics.Quaternion(cross, 1f + dot));

            // Apply rotation to the start center
            var center = new System.Numerics.Vector3(vc.X, vc.Y, vc.Z);
            var rotated = System.Numerics.Vector3.Transform(center, q);

            // Convert back to RA/Dec
            var newDec = double.RadiansToDegrees(Math.Asin(Math.Clamp(rotated.Z, -1f, 1f)));
            var newRA = Math.Atan2(rotated.Y, rotated.X) / (Math.PI / 12.0);

            State.CenterRA = newRA;
            State.CenterDec = newDec;
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
                case InputKey.A:
                    State.ShowAltAzGrid = !State.ShowAltAzGrid;
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.O:
                    State.ShowObjectOverlay = !State.ShowObjectOverlay;
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.M:
                    State.ShowMountOverlay = !State.ShowMountOverlay;
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.P:
                    State.Mode = State.Mode == SkyMapMode.Equatorial
                        ? SkyMapMode.Horizon
                        : SkyMapMode.Equatorial;
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
                case InputKey.Left when _timeProvider is not null && _plannerState is not null:
                    PlannerActions.ShiftPlanningDate(_plannerState, _timeProvider, -1);
                    return true;
                case InputKey.Right when _timeProvider is not null && _plannerState is not null:
                    PlannerActions.ShiftPlanningDate(_plannerState, _timeProvider, 1);
                    return true;
                case InputKey.Up when _timeProvider is not null && _plannerState is not null:
                    PlannerActions.ShiftPlanningHours(_plannerState, _timeProvider, 1);
                    return true;
                case InputKey.Down when _timeProvider is not null && _plannerState is not null:
                    PlannerActions.ShiftPlanningHours(_plannerState, _timeProvider, -1);
                    return true;
                case InputKey.T when _plannerState is not null:
                    PlannerActions.ResetPlanningDate(_plannerState);
                    return true;
                default:
                    return false;
            }
        }
    }
}
