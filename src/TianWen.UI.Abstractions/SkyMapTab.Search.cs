using System;
using System.Collections.Immutable;
using System.Numerics;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// F3 search modal overlay + selected-object info panel. Split into a partial
    /// file so the core sky-map rendering stays focused on catalog projection.
    /// </summary>
    public partial class SkyMapTab<TSurface>
    {
        // ── Modal colours ──
        private static readonly RGBAColor32 SearchBackdrop   = new(0x00, 0x00, 0x00, 0x80);
        private static readonly RGBAColor32 SearchPanelBg    = new(0x22, 0x22, 0x28, 0xF0);
        private static readonly RGBAColor32 SearchPanelBorder = new(0x50, 0x50, 0x60, 0xFF);
        private static readonly RGBAColor32 SearchHeaderBg   = new(0x30, 0x30, 0x38, 0xFF);
        private static readonly RGBAColor32 SearchRowHover   = new(0xC0, 0x90, 0x30, 0xD0);
        private static readonly RGBAColor32 SearchText       = new(0xDD, 0xDD, 0xDD, 0xFF);
        private static readonly RGBAColor32 SearchDimText    = new(0x80, 0x80, 0x88, 0xFF);
        private static readonly RGBAColor32 SelectionMarker  = new(0xFF, 0xEE, 0x60, 0xFF);
        private static readonly RGBAColor32 PinButtonBg      = new(0x3A, 0x5A, 0x3A, 0xFF);
        private static readonly RGBAColor32 UnpinButtonBg    = new(0x5A, 0x3A, 0x3A, 0xFF);
        private static readonly RGBAColor32 ViewButtonBg     = new(0x3A, 0x4A, 0x5A, 0xFF);
        private static readonly RGBAColor32 GotoButtonBg     = new(0x5A, 0x3A, 0x5A, 0xFF);
        private static readonly RGBAColor32 GotoDisabledBg   = new(0x38, 0x38, 0x3C, 0xFF);

        private const float SearchPanelWidth  = 480f;
        private const float SearchPanelHeight = 500f;
        private const float SearchRowHeight   = 28f;

        // Click-vs-drag threshold in screen pixels. Under this = click (select an
        // object); over this = drag-pan. Matches Stellarium and OS conventions.
        private const float ClickDragThresholdPx = 4f;

        // Saved mouse-down position for click-vs-drag detection, plus a flag that
        // records whether the MouseDown actually reached this tab. Without the flag
        // a sidebar click (chrome consumes MouseDown) followed by chrome forwarding
        // MouseUp(0,0) to the now-active tab would fire a spurious click-select on
        // the top-left corner of the sky map.
        private float _mouseDownX, _mouseDownY;
        private bool _mouseDownOnMap;
        private InputModifier _mouseDownModifiers;

        /// <summary>
        /// Draws the search modal and/or the info panel. Called last in <see cref="Render"/>
        /// so its clickable regions take priority over map drag gestures.
        /// </summary>
        protected void DrawSearchAndInfoPanel(
            PlannerState plannerState,
            RectF32 contentRect, string fontPath, float dpiScale,
            ICelestialObjectDB db,
            double siteLat, double siteLon,
            DateTimeOffset viewingTime,
            in SiteContext site,
            double pixelsPerRadian, float cx, float cy)
        {
            // Draw info panel regardless of modal state — a selected object should
            // remain visible after the user closes the search window.
            if (State.Search.InfoPanel is { } info)
            {
                DrawInfoPanel(plannerState, info, contentRect, fontPath, dpiScale,
                    pixelsPerRadian, cx, cy);
            }

            if (State.Search.IsOpen)
            {
                DrawSearchModal(contentRect, fontPath, dpiScale, db,
                    siteLat, siteLon, viewingTime, site);
            }
        }

        private void DrawSearchModal(
            RectF32 contentRect, string fontPath, float dpiScale,
            ICelestialObjectDB db,
            double siteLat, double siteLon,
            DateTimeOffset viewingTime,
            in SiteContext site)
        {
            // Full-content backdrop -- swallows clicks outside the panel. One draw==hit Box
            // leaf (fill and hit are the same arranged rect) instead of a FillRect +
            // RegisterClickable pair whose rects could silently drift apart.
            RenderLayout(
                new LayoutNode.Leaf(new LayoutContent.Box(0f, 0f))
                {
                    Width = Sizing.Star(),
                    Height = Sizing.Star(),
                    Background = SearchBackdrop,
                    Hit = new HitResult.ButtonHit("SearchBackdrop"),
                    OnClick = _ => PostSignal(new CloseSkyMapSearchSignal()),
                },
                contentRect, fontPath, dpiScale);

            var pw = SearchPanelWidth * dpiScale;
            var ph = SearchPanelHeight * dpiScale;
            var px = contentRect.X + (contentRect.Width - pw) * 0.5f;
            var py = contentRect.Y + (contentRect.Height - ph) * 0.35f;
            var fontSize = 14f * dpiScale;

            FillRect(px - 1, py - 1, pw + 2, ph + 2, SearchPanelBorder);
            FillRect(px, py, pw, ph, SearchPanelBg);

            // Title bar
            var headerH = 32f * dpiScale;
            FillRect(px, py, pw, headerH, SearchHeaderBg);
            DrawText("Search window".AsSpan(), fontPath,
                px, py, pw, headerH, fontSize, SearchText, TextAlign.Center, TextAlign.Center);

            // Close X button -- draw==hit Text leaf (the "X" glyph box and the click
            // surface are the same arranged rect). Font is a raw design unit
            // (fontSize / dpiScale); RenderLayout re-applies dpiScale.
            var closeW = headerH;
            RenderLayout(
                new LayoutNode.Leaf(new LayoutContent.Text("X", fontSize / dpiScale) { Color = SearchText, HAlign = TextAlign.Center, VAlign = TextAlign.Center })
                {
                    Width = Sizing.Star(),
                    Height = Sizing.Star(),
                    Hit = new HitResult.ButtonHit("SearchClose"),
                    OnClick = _ => PostSignal(new CloseSkyMapSearchSignal()),
                },
                new RectF32(px + pw - closeW, py, closeW, headerH), fontPath, dpiScale);

            // Search input
            var inputY = py + headerH + 12f * dpiScale;
            var inputH = 30f * dpiScale;
            var inputPadX = 12f * dpiScale;
            RenderTextInput(State.Search.SearchInput,
                (int)(px + inputPadX), (int)inputY,
                (int)(pw - inputPadX * 2f), (int)inputH,
                fontPath, fontSize);

            // Results list
            var listY = inputY + inputH + 8f * dpiScale;
            var listH = py + ph - listY - 12f * dpiScale;
            DrawResults(State.Search.Results, State.Search.SelectedResultIndex,
                px + inputPadX, listY, pw - inputPadX * 2f, listH,
                SearchRowHeight * dpiScale, fontPath, fontSize, dpiScale);

            // db + site are passed to keep the hot path closure-free; right now they
            // are only needed for click-to-select on the map, not inside the modal.
            _ = db;
            _ = siteLat; _ = siteLon; _ = viewingTime;
            _ = site;
        }

        private void DrawResults(
            ImmutableArray<SkyMapSearchResult> results,
            int selectedIndex,
            float x, float y, float w, float h,
            float rowH, string fontPath, float fontSize, float dpiScale)
        {
            if (results.IsDefaultOrEmpty)
            {
                DrawText("Type to search catalog...".AsSpan(), fontPath,
                    x, y, w, h, fontSize, SearchDimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var visibleRows = (int)(h / rowH);
            var count = Math.Min(results.Length, visibleRows);

            // Raw design-unit fonts; RenderLayout re-applies dpiScale to the row tree.
            var nameFont = fontSize / dpiScale;
            var magFont = nameFont * 0.9f;
            var selectedTextColor = new RGBAColor32(0x00, 0x00, 0x00, 0xFF);

            for (var i = 0; i < count; i++)
            {
                var rowY = y + i * rowH;
                var entry = results[i];
                var isSelected = i == selectedIndex;
                var capturedIndex = i;

                // Each row is one draw==hit Stack: the selected-row highlight, the name +
                // optional V-mag text, and the click surface are all the same arranged rect,
                // so the hit region can no longer drift away from what's drawn.
                var rowChildren = ImmutableArray.CreateBuilder<LayoutNode>();
                rowChildren.Add(new LayoutNode.Leaf(new LayoutContent.Box(0f, 0f)) { Width = Sizing.Fixed(12f), Height = Sizing.Star() });
                rowChildren.Add(new LayoutNode.Leaf(new LayoutContent.Text(entry.Display, nameFont) { Color = isSelected ? selectedTextColor : SearchText, HAlign = TextAlign.Near, VAlign = TextAlign.Center })
                {
                    Width = Sizing.Star(),
                    Height = Sizing.Star(),
                });
                if (!float.IsNaN(entry.VMag))
                {
                    rowChildren.Add(new LayoutNode.Leaf(new LayoutContent.Text($"{entry.VMag:F1}m", magFont) { Color = isSelected ? selectedTextColor : SearchDimText, HAlign = TextAlign.Far, VAlign = TextAlign.Center })
                    {
                        Width = Sizing.Fixed(52f),
                        Height = Sizing.Star(),
                    });
                }
                rowChildren.Add(new LayoutNode.Leaf(new LayoutContent.Box(0f, 0f)) { Width = Sizing.Fixed(10f), Height = Sizing.Star() });

                var rowNode = new LayoutNode.Stack(rowChildren.ToImmutable(), LayoutAxis.Horizontal)
                {
                    Width = Sizing.Star(),
                    Height = Sizing.Star(),
                    Background = isSelected ? SearchRowHover : (RGBAColor32?)null,
                    Hit = new HitResult.ListItemHit("SearchResult", capturedIndex),
                    OnClick = _ =>
                    {
                        State.Search.SelectedResultIndex = capturedIndex;
                        PostSignal(new SkyMapSearchCommitSignal());
                    },
                };
                RenderLayout(rowNode, new RectF32(x, rowY, w, rowH), fontPath, dpiScale);
            }
        }

        private void DrawInfoPanel(
            PlannerState plannerState,
            in SkyMapInfoPanelData info,
            RectF32 contentRect, string fontPath, float dpiScale,
            double pixelsPerRadian, float cx, float cy)
        {
            // Widened (300 -> 320 -> 348) to fit three action buttons (Goto / View in
            // Planner / Pin) without the longer "View in Planner" label clipping.
            var pw = 348f * dpiScale;
            var ph = 205f * dpiScale;
            var px = contentRect.X + 12f * dpiScale;
            var py = contentRect.Y + contentRect.Height - ph - 32f * dpiScale; // above status strip
            var fontSize = 12f * dpiScale;

            FillRect(px - 1, py - 1, pw + 2, ph + 2, SearchPanelBorder);
            FillRect(px, py, pw, ph, InfoPanelBg);

            var rowH = fontSize * 1.35f;
            var textX = px + 10f;
            var textW = pw - 40f;
            var row = 0f;

            DrawText(info.Name.AsSpan(), fontPath,
                textX, py + row, textW, rowH, fontSize * 1.1f, SearchText, TextAlign.Near, TextAlign.Near);
            row += rowH * 1.15f;

            // Designation + constellation + type — show whichever pieces are known, joined by two
            // spaces. Planets carry no catalog designation but DO have a type (Planet) and a current
            // constellation, so an empty designation must NOT suppress those (nor show the misleading
            // "(no designation)" placeholder when there's still real info to show).
            var canon = info.Canonical ?? "";
            var constell = info.Constellation != default ? info.Constellation.ToString() : "";
            var objType = info.ObjType != ObjectType.Unknown ? info.ObjType.ToString() : "";
            var subtitle = canon;
            if (constell.Length > 0) subtitle = subtitle.Length > 0 ? $"{subtitle}  {constell}" : constell;
            if (objType.Length > 0) subtitle = subtitle.Length > 0 ? $"{subtitle}  {objType}" : objType;
            if (subtitle.Length == 0) subtitle = "(no designation)";
            DrawText(subtitle.AsSpan(), fontPath,
                textX, py + row, textW, rowH, fontSize * 0.9f, SearchDimText, TextAlign.Near, TextAlign.Center);
            row += rowH;

            // RA / Dec line
            var raDec = $"RA {CoordinateUtils.HoursToHMS(info.RA)}   Dec {CoordinateUtils.DegreesToDMS(info.Dec)}";
            DrawText(raDec.AsSpan(), fontPath,
                textX, py + row, textW, rowH, fontSize, SearchText, TextAlign.Near, TextAlign.Center);
            row += rowH;

            // Magnitude + B-V + size
            var magPart = float.IsNaN(info.VMag) ? "mag -" : $"mag {info.VMag:F2}";
            var bvPart = float.IsNaN(info.BMinusV) ? "" : $"   B-V {info.BMinusV:F2}";
            var sizePart = info.AngularSizeDeg is { } s
                ? $"   size {s * 60:F1}'"
                : "";
            DrawText($"{magPart}{bvPart}{sizePart}".AsSpan(), fontPath,
                textX, py + row, textW, rowH, fontSize, SearchText, TextAlign.Near, TextAlign.Center);
            row += rowH;

            // Alt / Az
            var altAz = double.IsNaN(info.AltDeg)
                ? "Alt -  Az -"
                : $"Alt {info.AltDeg:+0.0;-0.0}\u00B0   Az {info.AzDeg:F1}\u00B0";
            DrawText(altAz.AsSpan(), fontPath,
                textX, py + row, textW, rowH, fontSize, SearchText, TextAlign.Near, TextAlign.Center);
            row += rowH;

            // Rise / Transit / Set — three lines or one combined line when space is tight.
            var tz = plannerState.SiteTimeZone;
            var rtsLine = info.NeverRises ? "Never rises (below horizon)"
                        : info.Circumpolar ? $"Circumpolar   Transit {FormatHHMM(info.TransitTime, tz)}"
                        : $"Rise {FormatHHMM(info.RiseTime, tz)}   Transit {FormatHHMM(info.TransitTime, tz)}   Set {FormatHHMM(info.SetTime, tz)}";
            DrawText(rtsLine.AsSpan(), fontPath,
                textX, py + row, textW, rowH, fontSize, SearchText, TextAlign.Near, TextAlign.Center);

            // Action buttons along the bottom of the panel.
            // Copy the in-parameter fields into locals so the click lambda can capture
            // them (can't close over 'in' parameters directly).
            var pinName = info.Name;
            var pinRA = info.RA;
            var pinDec = info.Dec;
            var pinIndex = info.Index;
            var pinType = info.ObjType;
            var isPinned = pinIndex is { } catIdx && IsPinned(plannerState, catIdx);
            var btnW = 90f * dpiScale;
            var btnH = 24f * dpiScale;
            var btnY = py + ph - btnH - 8f * dpiScale;

            if (info.IsMount)
            {
                // Mount entry: the one meaningful action is Solve & Sync (a goto to
                // the mount's own reported position is a no-op, and pinning it makes
                // no sense). The handler is the source of truth for the session /
                // camera / CanSync gates. While a solve is in flight the button shows
                // "Solving ..." (dimmed, no click handler) so it reads as busy and can't
                // be re-triggered mid-solve.
                var solving = State.SolveSyncInProgress;
                var ssBtnW = 110f * dpiScale;
                var ssBtnX = px + pw - ssBtnW - 10f * dpiScale;
                Action<InputModifier>? ssOnClick = null;
                if (!solving)
                {
                    ssOnClick = _ => PostSignal(new SkyMapSolveSyncSignal());
                }
                RenderButton(
                    solving ? "Solving ..." : "Solve & Sync",
                    ssBtnX, btnY, ssBtnW, btnH, fontPath, fontSize,
                    solving ? GotoDisabledBg : GotoButtonBg,
                    SearchText,
                    "SkyMapSolveSync",
                    ssOnClick);
            }
            else
            {
                // Pin / Unpin (right edge).
                var pinBtnX = px + pw - btnW - 10f * dpiScale;
                RenderButton(
                    isPinned ? "Unpin" : "Pin",
                    pinBtnX, btnY, btnW, btnH, fontPath, fontSize,
                    isPinned ? UnpinButtonBg : PinButtonBg,
                    SearchText,
                    "SkyMapPinToggle",
                    _ => PostSignal(new SkyMapPinObjectSignal(
                        pinName, pinRA, pinDec, pinIndex, pinType)));

                // View-in-Planner (left of the Pin button). Jumps to the planner tab
                // with this target scored, selected, and scrolled into view. Wider than
                // the short Goto / Pin buttons so the longer label doesn't clip.
                var viewBtnW = 116f * dpiScale;
                var viewBtnX = pinBtnX - viewBtnW - 8f * dpiScale;
                RenderButton(
                    "View in Planner",
                    viewBtnX, btnY, viewBtnW, btnH, fontPath, fontSize * 0.9f,
                    ViewButtonBg,
                    SearchText,
                    "SkyMapViewInPlanner",
                    _ => PostSignal(new ViewInPlannerSignal(
                        pinName, pinRA, pinDec, pinIndex, pinType)));

                // Goto (left of View-in-Planner). Slews the connected mount to the
                // object. Grayed when the target never rises from the current site;
                // the handler is still the source of truth for the actual horizon /
                // connection gate.
                var gotoBtnX = viewBtnX - btnW - 8f * dpiScale;
                var canGoto = !info.NeverRises;
                RenderButton(
                    "Goto",
                    gotoBtnX, btnY, btnW, btnH, fontPath, fontSize,
                    canGoto ? GotoButtonBg : GotoDisabledBg,
                    SearchText,
                    "SkyMapGoto",
                    _ => PostSignal(new SkyMapSlewToObjectSignal(
                        pinName, pinRA, pinDec, pinIndex, pinType)));
            }

            // Close button -- top-right of the info panel. Draw==hit Text leaf so the
            // glyph box and the click surface are the same arranged rect.
            var closeSize = 20f * dpiScale;
            RenderLayout(
                new LayoutNode.Leaf(new LayoutContent.Text("X", fontSize / dpiScale * 0.9f) { Color = SearchDimText, HAlign = TextAlign.Center, VAlign = TextAlign.Center })
                {
                    Width = Sizing.Star(),
                    Height = Sizing.Star(),
                    Hit = new HitResult.ButtonHit("InfoPanelClose"),
                    OnClick = _ =>
                    {
                        State.Search.InfoPanel = null;
                        State.NeedsRedraw = true;
                    },
                },
                new RectF32(px + pw - closeSize, py, closeSize, closeSize), fontPath, dpiScale);

            // Selection marker on the map itself. For objects with a known shape
            // (nebulae, galaxies, clusters) we trace the projected ellipse so the
            // marker actually hugs the object; for stars and shapeless entries we
            // fall back to the crosshair circle so there is still a clear indicator.
            if (SkyMapProjection.ProjectWithMatrix(info.RA, info.Dec, State.CurrentViewMatrix,
                pixelsPerRadian, cx, cy, out var sx, out var sy)
                && sx >= contentRect.X && sx < contentRect.X + contentRect.Width
                && sy >= contentRect.Y && sy < contentRect.Y + contentRect.Height)
            {
                if (!TryDrawShapeMarker(info, pixelsPerRadian, sx, sy, dpiScale))
                {
                    DrawCircle(sx, sy, 14f * dpiScale, SelectionMarker, 1.5f);
                    DrawLine(sx - 18f * dpiScale, sy, sx - 8f * dpiScale, sy, SelectionMarker);
                    DrawLine(sx + 8f * dpiScale, sy, sx + 18f * dpiScale, sy, SelectionMarker);
                    DrawLine(sx, sy - 18f * dpiScale, sx, sy - 8f * dpiScale, SelectionMarker);
                    DrawLine(sx, sy + 8f * dpiScale, sx, sy + 18f * dpiScale, SelectionMarker);
                }
            }
        }

        // Trace a rotated ellipse approximating the catalog object's shape. Returns
        // false when there is no usable shape or the projected size is too small to
        // distinguish from a crosshair — the caller falls back to the circle marker.
        //
        // Orientation uses the object's actual sky-projected north/east directions
        // (sampled via SkyMapProjection) rather than assuming north = screen up.
        // That stays correct under view rotation (Horizon mode, near-pole pointing)
        // and under stereographic distortion at edges of the viewport.
        private bool TryDrawShapeMarker(in SkyMapInfoPanelData info, double pixelsPerRadian,
            float centerX, float centerY, float dpiScale)
        {
            if (info.Shape is not { } shape) return false;

            // A star can carry a stray/cross-linked shape (e.g. Antares sits inside the
            // rho-Oph dark-cloud complex), but it must still draw as the crosshair, never
            // an extended-object ellipse. Gate on the same classifier the overlay markers
            // use so all three marker paths agree.
            if (Overlays.OverlayEngine.ChooseMarkerKind(info.ObjType, hasShape: true)
                != Overlays.OverlayMarkerKind.Ellipse)
            {
                return false;
            }

            var majorArcmin = (double)shape.MajorAxis;
            var minorArcmin = (double)shape.MinorAxis;
            if (double.IsNaN(majorArcmin) || majorArcmin <= 0) return false;

            var effectiveMinor = double.IsNaN(minorArcmin) || minorArcmin <= 0
                ? majorArcmin
                : minorArcmin;

            const double ArcminToRad = Math.PI / (180.0 * 60.0);
            var semiMajorPx = (float)(majorArcmin * 0.5 * ArcminToRad * pixelsPerRadian);
            var semiMinorPx = (float)(effectiveMinor * 0.5 * ArcminToRad * pixelsPerRadian);

            if (semiMajorPx < 10f * dpiScale) return false;

            // Sample screen-space north unit vector by projecting a point 1' north
            // of the object and subtracting the projected centre. We only care about
            // direction, so magnitude is renormalised. Bail if any projection fails
            // (object at antipode, etc.) — crosshair fallback still works.
            if (!SkyMapProjection.ProjectWithMatrix(info.RA, info.Dec, State.CurrentViewMatrix,
                    pixelsPerRadian, centerX, centerY, out var ox, out var oy)
                || !SkyMapProjection.ProjectWithMatrix(info.RA, info.Dec + 1.0 / 60.0,
                    State.CurrentViewMatrix, pixelsPerRadian, centerX, centerY,
                    out var nx, out var ny))
            {
                return false;
            }

            var dnx = nx - ox;
            var dny = ny - oy;
            if (dnx * dnx + dny * dny < 1e-10f) return false;

            var paDeg = (double)shape.PositionAngle;
            if (double.IsNaN(paDeg)) paDeg = 0;
            var paRad = (float)double.DegreesToRadians(paDeg);

            // Major / minor axis screen directions come from the single shared helper so
            // this selection ellipse and the [O] overlay ellipse for the same object are
            // oriented identically (true sky position angle). The GPU overlay shader is a
            // hand-maintained mirror of the same convention -- see
            // Overlays.OverlayEngine.ComputeEllipseScreenAxes.
            var (majorX, majorY, minorX, minorY) =
                Overlays.OverlayEngine.ComputeEllipseScreenAxes(dnx, dny, paRad);

            // Trace the ellipse.
            const int Segments = 36;
            var prevValid = false;
            float prevX = 0, prevY = 0;
            for (var i = 0; i <= Segments; i++)
            {
                var theta = i * (2.0 * Math.PI / Segments);
                var (sinT, cosT) = Math.SinCos(theta);
                var ex = (float)(semiMajorPx * cosT);
                var ey = (float)(semiMinorPx * sinT);
                var plotX = centerX + ex * majorX + ey * minorX;
                var plotY = centerY + ex * majorY + ey * minorY;
                if (prevValid)
                {
                    DrawLine(prevX, prevY, plotX, plotY, SelectionMarker);
                }
                prevX = plotX;
                prevY = plotY;
                prevValid = true;
            }

            // Tiny centre cross so users can still see the centroid for large shapes.
            var tick = 4f * dpiScale;
            DrawLine(centerX - tick, centerY, centerX + tick, centerY, SelectionMarker);
            DrawLine(centerX, centerY - tick, centerX, centerY + tick, SelectionMarker);
            return true;
        }

        // Rise/Transit/Set are UTC instants; render them in the site timezone, never the
        // machine timezone (.LocalDateTime would show UTC on a UTC-set machine).
        private static string FormatHHMM(DateTimeOffset? t, TimeSpan siteTimeZone)
            => t is { } dt ? $"{dt.ToOffset(siteTimeZone):HH:mm}" : "--:--";

        // Walk PlannerState.Proposals to see if this catalog index is pinned.
        // Called once per frame while the info panel is visible — proposals are few
        // so O(n) is fine. ImmutableArray enumerator is zero-alloc.
        private static bool IsPinned(PlannerState plannerState, CatalogIndex catIdx)
        {
            foreach (var p in plannerState.Proposals)
            {
                if (p.Target.CatalogIndex == catIdx) return true;
            }
            return false;
        }

        /// <summary>
        /// Handle the F3 shortcut. Call from the tab's key handler.
        /// Returns true when the key was consumed.
        /// </summary>
        protected bool TryHandleSearchKey(InputKey key)
        {
            if (key == InputKey.F3)
            {
                if (State.Search.IsOpen)
                {
                    PostSignal(new CloseSkyMapSearchSignal());
                }
                else
                {
                    PostSignal(new OpenSkyMapSearchSignal());
                }
                State.NeedsRedraw = true;
                return true;
            }

            // Arrow key navigation inside the result list (only when modal open and text
            // input hasn't already handled the key — i.e. this is a fallback).
            if (!State.Search.IsOpen) return false;

            switch (key)
            {
                case InputKey.Down when State.Search.Results.Length > 0:
                    State.Search.SelectedResultIndex = Math.Min(
                        State.Search.SelectedResultIndex + 1, State.Search.Results.Length - 1);
                    State.NeedsRedraw = true;
                    return true;
                case InputKey.Up when State.Search.Results.Length > 0:
                    State.Search.SelectedResultIndex = Math.Max(
                        State.Search.SelectedResultIndex - 1, 0);
                    State.NeedsRedraw = true;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Detect click-vs-drag on the map and emit a <see cref="SkyMapClickSelectSignal"/>
        /// when the mouse didn't move far enough to count as a pan.
        /// Call from the tab's <c>MouseUp</c> handler before <see cref="HandleDragEndInternal"/>.
        /// </summary>
        protected bool TryEmitClickSelect(float upX, float upY)
        {
            if (State.Search.IsOpen) return false;
            if (State.IsPinching) return false;
            // Gate: only treat this MouseUp as a click if the matching MouseDown
            // landed on the map. Sidebar / tab-switch clicks don't qualify.
            var hadDown = _mouseDownOnMap;
            _mouseDownOnMap = false; // consume the flag regardless of outcome

            if (!hadDown) return false;

            var dx = upX - _mouseDownX;
            var dy = upY - _mouseDownY;
            if (dx * dx + dy * dy > ClickDragThresholdPx * ClickDragThresholdPx)
            {
                return false;
            }

            PostSignal(new SkyMapClickSelectSignal(upX, upY, _mouseDownModifiers));
            return true;
        }

        /// <summary>
        /// Remember the mouse-down location (and modifiers) for click-vs-drag detection.
        /// Call from the tab's <c>MouseDown</c> handler. The modifiers are captured here
        /// because <see cref="InputEvent.MouseUp"/> does not carry them — only
        /// <see cref="InputEvent.MouseDown"/> does — and the click-select fires on mouse-up.
        /// </summary>
        protected void RememberMouseDown(float x, float y, InputModifier modifiers = InputModifier.None)
        {
            _mouseDownX = x;
            _mouseDownY = y;
            _mouseDownModifiers = modifiers;
            _mouseDownOnMap = true;
        }

        /// <summary>
        /// Project ppr for click-handling code outside this partial.
        /// </summary>
        internal (double Ppr, float CenterX, float CenterY) GetProjectionParams()
        {
            var ppr = SkyMapProjection.PixelsPerRadian(_contentHeight, State.FieldOfViewDeg);
            var cx = _contentX + _contentWidth * 0.5f;
            var cy = _contentY + _contentHeight * 0.5f;
            return (ppr, cx, cy);
        }

        /// <summary>
        /// Expose the cached viewing time + site for click-select handlers routed
        /// through <see cref="AppSignalHandler"/>.
        /// </summary>
        internal (double SiteLat, double SiteLon, DateTimeOffset ViewingUtc, Matrix4x4 ViewMatrix) GetClickContext(PlannerState plannerState)
        {
            var siteLat = plannerState.SiteLatitude;
            var siteLon = plannerState.SiteLongitude;
            var viewingUtc = plannerState.PlanningDate?.ToUniversalTime() ?? _cachedLiveTime;
            return (siteLat, siteLon, viewingUtc, State.CurrentViewMatrix);
        }
    }
}
