using System;
using System.Collections.Generic;
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

        // Click-vs-drag classification (DIR.Lib TapOrDragGesture, 4px slop at 1x -- the Stellarium/OS
        // convention -- now DPI-scaled). Arming only when the MouseDown actually reached this tab
        // replaces the old _mouseDownOnMap flag: a sidebar click (chrome consumes MouseDown) followed
        // by chrome forwarding MouseUp to the now-active tab releases an IDLE gesture -> None, never a
        // spurious click-select. Update() latches a drag, so a pan that wanders back over its start no
        // longer misclassifies as a click (the old total-displacement check did). The press modifiers
        // ride on the gesture (MouseUp doesn't carry them; click-select fires on release).
        private TapOrDragGesture _mapGesture;

        /// <summary>
        /// Draws the search modal and/or the info panel. Called last in <see cref="Render"/>
        /// so its clickable regions take priority over map drag gestures.
        /// </summary>
        protected void DrawSearchAndInfoPanel(
            PlannerState plannerState,
            RectF32 contentRect,
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
                // The zenith is horizon-relative: its RA (= LST) and Dec (= latitude) advance with the
                // viewing time, so a selection made earlier must re-resolve to the CURRENT overhead point
                // each frame -- the same live treatment the solar-system bodies get below -- or the
                // crosshair drifts off the amber Zenith marker as the map is date-/time-scrubbed.
                // Projecting (RA = LST, Dec = latitude) reproduces the marker's own unit vector exactly,
                // so the re-resolved crosshair lands right on it.
                if (info.FixedPoint == SkyFixedPoint.Zenith && site.IsValid)
                {
                    var zenithRa = site.LST;
                    var zenithDec = double.RadiansToDegrees(Math.Asin(site.SinLat));
                    info = SkyMapInfoPanelData.FromPosition("Zenith", zenithRa, zenithDec,
                        siteLat, siteLon, viewingTime, site) with { FixedPoint = SkyFixedPoint.Zenith };
                }
                // A solar-system body's true RA/Dec is viewing-time dependent, so a selection made on an
                // earlier date/time would otherwise freeze the crosshair + RA/Dec/Alt at that instant while
                // the live planet dot moves on (the "Venus moved but the crosshair didn't" bug). Only a
                // Pl-catalog body can move, so gate on IsSolarSystemObject FIRST (O(1) on the index) -- a
                // fixed star/DSO or the mount marker never enters the ephemeris path at all. For a planet,
                // re-resolve from the per-frame cache the dot/label draws from (keyed on the SAME
                // viewingTime) and rebuild the panel via the shared builder, so stepping the date keeps the
                // marker on the planet and its Alt/RA/Dec live.
                else if (info.Index is { } selIdx && selIdx.IsSolarSystemObject)
                {
                    if (selIdx.ToCatalog() == Catalog.Comet)
                    {
                        // A comet moves AND brightens with time; re-resolve its live position + magnitude
                        // from the same per-frame cache the marker draws from, keyed on the SAME viewing
                        // time, so stepping the date keeps the panel (and the sparkline "now" point) live.
                        if (plannerState.Comets is { } comets)
                        {
                            foreach (var m in State.GetCometPositionsCached(comets, viewingTime))
                            {
                                if (m.Index == selIdx)
                                {
                                    info = SkyMapSearchActions.CometInfoPanel(
                                        comets, selIdx, m.RA, m.Dec, m.VMag, siteLat, siteLon, viewingTime, in site);
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (var (planetIdx, pRa, pDec) in State.GetPlanetPositionsCached(viewingTime))
                        {
                            if (planetIdx == selIdx)
                            {
                                info = SkyMapSearchActions.PlanetInfoPanel(
                                    db, selIdx, pRa, pDec, siteLat, siteLon, viewingTime, in site);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // Fixed-RA/Dec selection (star / DSO / plain position / mount marker). Its RA/Dec don't
                    // move with time, so the reticle already tracks (it projects through the live view
                    // matrix), but Alt/Az are horizon coordinates that DO swing with the hour angle -- so
                    // refresh just those from the current site each frame. Cheap (O(1) trig); rise/transit/
                    // set stay date-resolved (see WithLiveHorizontal).
                    info = info.WithLiveHorizontal(site);
                }

                DrawInfoPanel(plannerState, info, contentRect,
                    viewingTime, pixelsPerRadian, cx, cy);
            }

            if (State.Search.IsOpen)
            {
                DrawSearchModal(contentRect, db,
                    siteLat, siteLon, viewingTime, site);
            }
        }

        private void DrawSearchModal(
            RectF32 contentRect,
            ICelestialObjectDB db,
            double siteLat, double siteLon,
            DateTimeOffset viewingTime,
            in SiteContext site)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            // Full-content backdrop -- swallows clicks outside the panel. One draw==hit Box
            // leaf (fill and hit are the same arranged rect) instead of a FillRect +
            // RegisterClickable pair whose rects could silently drift apart.
            RenderLayout(
                Layout.Builder.Spacer().Stretch().Bg(SearchBackdrop)
                    .Clickable(new HitResult.ButtonHit("SearchBackdrop"), _ => PostSignal(new CloseSkyMapSearchSignal())),
                contentRect, dpiScale: dpiScale);

            var pw = SearchPanelWidth * dpiScale;
            var ph = SearchPanelHeight * dpiScale;
            var px = contentRect.X + (contentRect.Width - pw) * 0.5f;
            var py = contentRect.Y + (contentRect.Height - ph) * 0.35f;
            var fontSize = 14f * dpiScale;

            const float headerHDesign = 32f;
            const float inputHDesign = 30f;
            const float bodyPad = 12f;
            const float inputGap = 8f;

            // Title bar: centred label + a right-pinned close X (draw==hit leaf), full panel width.
            var titleBar = Layout.Builder.HStack(
                    Layout.Builder.Text("Search window", 14f, SearchText, TextAlign.Center, TextAlign.Center).WStar().HStar(),
                    Layout.Builder.Text("X", 14f, SearchText, TextAlign.Center, TextAlign.Center).WFixed(headerHDesign).HStar()
                        .Clickable(new HitResult.ButtonHit("SearchClose"), _ => PostSignal(new CloseSkyMapSearchSignal())))
                .RowH(headerHDesign).Bg(SearchHeaderBg);

            // Results area design height (panel minus title + body padding + input + gap), for the row cap.
            var resultsDesignH = SearchPanelHeight - headerHDesign - bodyPad * 2f - inputHDesign - inputGap;
            var visibleRows = Math.Max(0, (int)(resultsDesignH / SearchRowHeight));

            // Body: search input (keyed Fill = the interactive text-input control) + results list, padded.
            // MUST be .Stretch() -- an Auto-width/height VStack collapses to intrinsic (the input + rows
            // starve to their text width instead of filling the panel).
            var body = Layout.Builder.VStack(
                    Layout.Builder.Fill(key: "searchInput").RowH(inputHDesign),
                    Layout.Builder.Spacer().RowH(inputGap),
                    BuildSearchResults(State.Search.Results, State.Search.SelectedResultIndex, visibleRows).Stretch())
                .Stretch().Pad(bodyPad);

            // Panel (bg) inside a 1px border frame (an outer Box.Bg + Pad(1)); the whole card is ONE tree.
            var panel = Layout.Builder.VStack(titleBar, body).Bg(SearchPanelBg);
            var framed = Layout.Builder.VStack(panel.Stretch()).Bg(SearchPanelBorder).Pad(1f);

            RenderLayout(framed, new RectF32(px - 1, py - 1, pw + 2, ph + 2), dpiScale: dpiScale,
                drawFill: (fill, r) =>
                {
                    if (fill.Key == "searchInput")
                    {
                        RenderTextInput(State.Search.SearchInput, r, fontPath, fontSize);
                    }
                });

            // db + site are passed to keep the hot path closure-free; right now they
            // are only needed for click-to-select on the map, not inside the modal.
            _ = db;
            _ = siteLat; _ = siteLon; _ = viewingTime;
            _ = site;
        }

        /// <summary>
        /// Builds the search results list as a VStack of draw==hit rows (or the empty-state hint), capped to
        /// <paramref name="visibleRows"/>. Each row's selected-highlight, name + optional V-mag, and click
        /// surface are the same arranged rect. Was a <c>y + i*rowH</c> per-row cursor.
        /// </summary>
        private Layout.Node BuildSearchResults(
            ImmutableArray<SkyMapSearchResult> results, int selectedIndex, int visibleRows)
        {
            if (results.IsDefaultOrEmpty)
            {
                return Layout.Builder.Text("Type to search catalog...", 14f, SearchDimText, TextAlign.Center, TextAlign.Center).Stretch();
            }

            var count = Math.Min(results.Length, visibleRows);
            var selectedTextColor = new RGBAColor32(0x00, 0x00, 0x00, 0xFF);
            var rows = new List<Layout.Node>(count);

            for (var i = 0; i < count; i++)
            {
                var entry = results[i];
                var isSelected = i == selectedIndex;
                var capturedIndex = i;

                var rowChildren = new List<Layout.Node>
                {
                    Layout.Builder.Spacer().ColW(12f),
                    Layout.Builder.Text(entry.Display, 14f, isSelected ? selectedTextColor : SearchText).Stretch(),
                };
                if (!float.IsNaN(entry.VMag))
                {
                    rowChildren.Add(Layout.Builder.Text($"{entry.VMag:F1}m", 14f * 0.9f, isSelected ? selectedTextColor : SearchDimText, TextAlign.Far).WFixed(52f).HStar());
                }
                rowChildren.Add(Layout.Builder.Spacer().ColW(10f));

                var rowNode = Layout.Builder.HStack([.. rowChildren])
                    .RowH(SearchRowHeight)
                    .Clickable(new HitResult.ListItemHit("SearchResult", capturedIndex), _ =>
                    {
                        State.Search.SelectedResultIndex = capturedIndex;
                        PostSignal(new SkyMapSearchCommitSignal());
                    });
                if (isSelected) rowNode = rowNode.Bg(SearchRowHover);
                rows.Add(rowNode);
            }

            return Layout.Builder.VStack([.. rows]);
        }

        private void DrawInfoPanel(
            PlannerState plannerState,
            in SkyMapInfoPanelData info,
            RectF32 contentRect,
            DateTimeOffset viewingTime,
            double pixelsPerRadian, float cx, float cy)
        {
            var dpiScale = DpiScale;
            // A comet carries a vmag sparkline under the text rows; fetch the state-cached curve (recomputed
            // only when the comet / viewing day changes) and grow the panel to make room for it.
            var isComet = info.Index is { } ci && ci.ToCatalog() == Catalog.Comet;
            var magCurve = isComet
                ? State.GetCometMagnitudeCurveCached(plannerState.Comets, info.Index!.Value, viewingTime)
                : default;
            var hasCurve = magCurve.Length > 0;

            // Widened (300 -> 320 -> 348) to fit three action buttons (Goto / View in
            // Planner / Pin) without the longer "View in Planner" label clipping.
            var pw = 348f * dpiScale;
            var ph = (hasCurve ? 250f : 205f) * dpiScale;
            var px = contentRect.X + 12f * dpiScale;
            var py = contentRect.Y + contentRect.Height - ph - 32f * dpiScale; // above status strip
            var fontSize = 12f * dpiScale;

            RenderLayout(Layout.Builder.Spacer().Bg(SearchPanelBorder), new RectF32(px - 1, py - 1, pw + 2, ph + 2));
            RenderLayout(Layout.Builder.Spacer().Bg(InfoPanelBg), new RectF32(px, py, pw, ph));

            var rowH = fontSize * 1.35f;
            var textX = px + 10f;
            var textW = pw - 40f;
            const float dFont = 12f; // design font (fontSize = 12 * dpiScale here); RenderLayout re-applies dpiScale
            var dRow = dFont * 1.35f;

            // Designation + constellation + type -- show whichever pieces are known, joined by two
            // spaces. Planets carry no catalog designation but DO have a type (Planet) and a current
            // constellation, so an empty designation must NOT suppress those (nor show the misleading
            // "(no designation)" placeholder when there's still real info to show).
            var canon = info.Canonical ?? "";
            var constell = info.Constellation != default ? info.Constellation.ToIAUAbbreviation() : "";
            var objType = info.ObjType != ObjectType.Unknown ? info.ObjType.ToName() : "";
            var subtitle = canon;
            if (constell.Length > 0) subtitle = subtitle.Length > 0 ? $"{subtitle}  {constell}" : constell;
            if (objType.Length > 0) subtitle = subtitle.Length > 0 ? $"{subtitle}  {objType}" : objType;
            if (subtitle.Length == 0) subtitle = "(no designation)";

            var raDec = $"RA {CoordinateUtils.HoursToHMS(info.RA)}   Dec {CoordinateUtils.DegreesToDMS(info.Dec)}";

            // Magnitude + surface brightness + B-V + size (SB compact, no unit -- the panel is narrow
            // and mag/arcsec² is the astronomer default; the planner details panel spells the unit out).
            var magPart = float.IsNaN(info.VMag) ? "mag -" : $"mag {info.VMag:F2}";
            var sbPart = float.IsNaN(info.SurfaceBrightness) ? "" : $"   SB {info.SurfaceBrightness:F2}";
            var bvPart = float.IsNaN(info.BMinusV) ? "" : $"   B-V {info.BMinusV:F2}";
            var sizePart = info.AngularSizeDeg is { } s ? $"   size {s * 60:F1}'" : "";
            var magLine = $"{magPart}{sbPart}{bvPart}{sizePart}";

            var altAz = double.IsNaN(info.AltDeg)
                ? "Alt -  Az -"
                : $"Alt {info.AltDeg:+0.0;-0.0}°   Az {info.AzDeg:F1}°";

            // Rise / Transit / Set -- three fields or one combined line when space is tight.
            var tz = plannerState.SiteTimeZone;
            var rtsLine = info.NeverRises ? "Never rises (below horizon)"
                        : info.Circumpolar ? $"Circumpolar   Transit {FormatHHMM(info.TransitTime, tz)}"
                        : $"Rise {FormatHHMM(info.RiseTime, tz)}   Transit {FormatHHMM(info.TransitTime, tz)}   Set {FormatHHMM(info.SetTime, tz)}";

            // The six text rows as one VStack (was a `row +=` cursor threaded through six DrawTexts).
            var textTree = Layout.Builder.VStack(
                Layout.Builder.Text(info.Name, dFont * 1.1f, SearchText, TextAlign.Near, TextAlign.Near).RowH(dRow * 1.15f),
                Layout.Builder.Text(subtitle, dFont * 0.9f, SearchDimText).RowH(dRow),
                Layout.Builder.Text(raDec, dFont, SearchText).RowH(dRow),
                Layout.Builder.Text(magLine, dFont, SearchText).RowH(dRow),
                Layout.Builder.Text(altAz, dFont, SearchText).RowH(dRow),
                Layout.Builder.Text(rtsLine, dFont, SearchText).RowH(dRow));
            var textBlockH = rowH * 1.15f + rowH * 5f;
            RenderLayout(textTree, new RectF32(textX, py, textW, textBlockH), dpiScale: dpiScale);
            var row = textBlockH;

            // Comet vmag sparkline (brighter = up), auto-scaled to the sampled +/-45-day window. The "now"
            // sample (window centre) is dotted, so the user reads the current trend at a glance.
            if (hasCurve)
            {
                DrawMagnitudeSparkline(magCurve,
                    textX, py + row + 2f * dpiScale, textW, 30f * dpiScale,
                    fontSize);
            }

            // Action buttons along the bottom of the panel, as ONE right-aligned HStack tree (was a
            // per-button `x -= btnW + gap` right-to-left cursor). Leading Star spacer pushes the row
            // right; a trailing fixed spacer holds the 10px right margin; buttons are Clickable Text
            // nodes (draw == hit). Widths/gaps are DESIGN units (RenderLayout re-applies dpiScale).
            // Copy the in-parameter fields into locals so the click lambdas can capture them (can't
            // close over 'in' parameters directly).
            var pinName = info.Name;
            var pinRA = info.RA;
            var pinDec = info.Dec;
            var pinIndex = info.Index;
            var pinType = info.ObjType;
            var isPinned = pinIndex is { } catIdx && IsPinned(plannerState, catIdx);
            var btnH = 24f * dpiScale;
            var btnY = py + ph - btnH - 8f * dpiScale;

            Layout.Node buttonRow;
            if (info.IsMount)
            {
                // Mount entry: the one meaningful action is Solve & Sync (a goto to the mount's own
                // reported position is a no-op, and pinning it makes no sense). The handler is the
                // source of truth for the session / camera / CanSync gates. While a solve is in flight
                // the button shows "Solving ..." (dimmed, no click handler) so it reads as busy and
                // can't be re-triggered mid-solve.
                var solving = State.SolveSyncInProgress;
                var ssNode = Layout.Builder.Text(solving ? "Solving ..." : "Solve & Sync", dFont, SearchText,
                        TextAlign.Center, TextAlign.Center)
                    .WFixed(110f).HStar().Bg(solving ? GotoDisabledBg : GotoButtonBg);
                if (!solving)
                {
                    ssNode = ssNode.Clickable(new HitResult.ButtonHit("SkyMapSolveSync"),
                        _ => PostSignal(new SkyMapSolveSyncSignal()));
                }
                buttonRow = Layout.Builder.HStack(
                    Layout.Builder.Spacer().WStar(),
                    ssNode,
                    Layout.Builder.Spacer().WFixed(10f).HStar());
            }
            else
            {
                // Goto (leftmost). Slews the connected mount to the object. Grayed when the target
                // never rises from the current site; the handler is still the source of truth for the
                // actual horizon / connection gate.
                var canGoto = !info.NeverRises;
                var gotoNode = Layout.Builder.Text("Goto", dFont, SearchText, TextAlign.Center, TextAlign.Center)
                    .WFixed(90f).HStar().Bg(canGoto ? GotoButtonBg : GotoDisabledBg);
                if (canGoto)
                {
                    gotoNode = gotoNode.Clickable(new HitResult.ButtonHit("SkyMapGoto"),
                        _ => PostSignal(new SkyMapSlewToObjectSignal(pinName, pinRA, pinDec, pinIndex, pinType)));
                }

                // View-in-Planner (middle). Jumps to the planner tab with this target scored, selected,
                // and scrolled into view. Wider than the short Goto / Pin buttons so the longer label
                // doesn't clip (and a smaller font).
                var viewNode = Layout.Builder.Text("View in Planner", dFont * 0.9f, SearchText,
                        TextAlign.Center, TextAlign.Center)
                    .WFixed(116f).HStar().Bg(ViewButtonBg)
                    .Clickable(new HitResult.ButtonHit("SkyMapViewInPlanner"),
                        _ => PostSignal(new ViewInPlannerSignal(pinName, pinRA, pinDec, pinIndex, pinType)));

                // Pin / Unpin (right edge).
                var pinNode = Layout.Builder.Text(isPinned ? "Unpin" : "Pin", dFont, SearchText,
                        TextAlign.Center, TextAlign.Center)
                    .WFixed(90f).HStar().Bg(isPinned ? UnpinButtonBg : PinButtonBg)
                    .Clickable(new HitResult.ButtonHit("SkyMapPinToggle"),
                        _ => PostSignal(new SkyMapPinObjectSignal(pinName, pinRA, pinDec, pinIndex, pinType)));

                buttonRow = Layout.Builder.HStack(
                    Layout.Builder.Spacer().WStar(),
                    gotoNode,
                    Layout.Builder.Spacer().WFixed(8f).HStar(),
                    viewNode,
                    Layout.Builder.Spacer().WFixed(8f).HStar(),
                    pinNode,
                    Layout.Builder.Spacer().WFixed(10f).HStar());
            }

            RenderLayout(buttonRow, new RectF32(px, btnY, pw, btnH), dpiScale: dpiScale);

            // Close button -- top-right of the info panel. Draw==hit Text leaf so the
            // glyph box and the click surface are the same arranged rect.
            var closeSize = 20f * dpiScale;
            RenderLayout(
                Layout.Builder.Text("X", fontSize / dpiScale * 0.9f, SearchDimText, TextAlign.Center, TextAlign.Center)
                    .Stretch()
                    .Clickable(new HitResult.ButtonHit("InfoPanelClose"), _ =>
                    {
                        State.Search.InfoPanel = null;
                        State.NeedsRedraw = true;
                    }),
                new RectF32(px + pw - closeSize, py, closeSize, closeSize), dpiScale: dpiScale);

            // Path across the sky for a selected solar-system body (planet / comet): a thin polyline of its
            // motion over a body-appropriate window + labelled event markers (stations, elongation,
            // perihelion), drawn UNDER the reticle so "now" stays on top.
            DrawSelectedObjectPath(info, plannerState, viewingTime, pixelsPerRadian, cx, cy, contentRect);

            // Selection marker on the map itself. For objects with a known shape
            // (nebulae, galaxies, clusters) we trace the projected ellipse so the
            // marker actually hugs the object; for stars and shapeless entries we
            // fall back to the crosshair circle so there is still a clear indicator.
            if (SkyMapProjection.ProjectWithMatrix(info.RA, info.Dec, State.CurrentViewMatrix,
                pixelsPerRadian, cx, cy, out var sx, out var sy)
                && sx >= contentRect.X && sx < contentRect.X + contentRect.Width
                && sy >= contentRect.Y && sy < contentRect.Y + contentRect.Height)
            {
                if (!TryDrawShapeMarker(info, pixelsPerRadian, sx, sy))
                {
                    DrawCircle(sx, sy, 14f * dpiScale, SelectionMarker, 1.5f);
                    DrawLine(sx - 18f * dpiScale, sy, sx - 8f * dpiScale, sy, SelectionMarker);
                    DrawLine(sx + 8f * dpiScale, sy, sx + 18f * dpiScale, sy, SelectionMarker);
                    DrawLine(sx, sy - 18f * dpiScale, sx, sy - 8f * dpiScale, SelectionMarker);
                    DrawLine(sx, sy + 8f * dpiScale, sx, sy + 18f * dpiScale, SelectionMarker);
                }
            }
        }

        // Draw the selected solar-system body's sky path (planet or comet) as a thin polyline over its
        // cached RA/Dec samples. Projection is per-frame (the view pans/zooms/scrubs); the samples come
        // from the day-keyed cache so no ephemeris runs here. Segments that fail to project or that span an
        // implausibly long screen distance (projection wrap / behind-camera) are skipped.
        private static readonly RGBAColor32 PathEventColor = new(0xFF, 0xE0, 0x60, 0xFF);

        private void DrawSelectedObjectPath(
            in SkyMapInfoPanelData info, PlannerState plannerState, DateTimeOffset viewingTime,
            double pixelsPerRadian, float cx, float cy, RectF32 contentRect)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            if (info.Index is not { } idx || !idx.IsSolarSystemObject)
            {
                return;
            }

            var path = State.GetSelectedPathCached(plannerState.Comets, idx, viewingTime);
            if (path.Length < 2)
            {
                return;
            }

            var baseColor = idx.ToCatalog() == Catalog.Comet ? CometColor : SkyMapRenderer.GetPlanetColor(idx);
            var pathColor = new RGBAColor32(baseColor.Red, baseColor.Green, baseColor.Blue, 0xA0);
            var maxSegSq = contentRect.Width * contentRect.Width + contentRect.Height * contentRect.Height;

            var prevValid = false;
            var prevX = 0f;
            var prevY = 0f;
            foreach (var (ra, dec) in path)
            {
                if (SkyMapProjection.ProjectWithMatrix(ra, dec, State.CurrentViewMatrix,
                        pixelsPerRadian, cx, cy, out var sx, out var sy))
                {
                    if (prevValid)
                    {
                        var dx = sx - prevX;
                        var dy = sy - prevY;
                        if (dx * dx + dy * dy < maxSegSq)
                        {
                            DrawLine(prevX, prevY, sx, sy, pathColor);
                        }
                    }
                    prevX = sx;
                    prevY = sy;
                    prevValid = true;
                }
                else
                {
                    prevValid = false;
                }
            }

            // Event markers along the path: a small ring + short label (R/D station, GE/Opp, q perihelion),
            // computed with the path (SkyMapState.SelectedPathEvents) so no ephemeris runs here.
            var eventFont = 11f * dpiScale;
            foreach (var ev in State.SelectedPathEvents)
            {
                if (!SkyMapProjection.ProjectWithMatrix(ev.RaJ2000Hours, ev.DecJ2000Deg, State.CurrentViewMatrix,
                        pixelsPerRadian, cx, cy, out var ex, out var ey)
                    || ex < contentRect.X || ex >= contentRect.X + contentRect.Width
                    || ey < contentRect.Y || ey >= contentRect.Y + contentRect.Height)
                {
                    continue;
                }
                DrawCircle(ex, ey, 4f * dpiScale, PathEventColor, 1.5f);
                DrawText(ev.Label.AsSpan(), fontPath,
                    ex + 6f * dpiScale, ey - eventFont, 60f, eventFont * 1.2f,
                    eventFont, PathEventColor, TextAlign.Near, TextAlign.Center);
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
            float centerX, float centerY)
        {
            if (info.Shape is not { } shape) return false;
            var dpiScale = DpiScale;

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

        private static readonly RGBAColor32 SparklineAxis = new(0x50, 0x50, 0x58, 0xFF);
        private static readonly RGBAColor32 SparklineLine = new(0x88, 0xEE, 0xCC, 0xFF);
        private static readonly RGBAColor32 SparklineNow  = new(0xFF, 0xEE, 0x60, 0xFF);

        /// <summary>
        /// Draw a small vmag sparkline: magnitude vs time with the axis INVERTED (brighter/lower magnitude
        /// at the top), auto-scaled to the finite-sample range, with a left gutter carrying the bright/faint
        /// magnitude bounds and a dotted "now" marker at the window centre. Uses the tab's shared
        /// <c>DrawLine</c> / <c>FillCircle</c> / <c>DrawText</c> primitives so it works on GPU and TUI alike.
        /// </summary>
        private void DrawMagnitudeSparkline(
            ReadOnlySpan<float> mags, float x, float y, float w, float h,
            float fontSize)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            var min = float.MaxValue;
            var max = float.MinValue;
            var finite = 0;
            foreach (var m in mags)
            {
                if (!float.IsNaN(m))
                {
                    if (m < min) min = m;
                    if (m > max) max = m;
                    finite++;
                }
            }

            if (finite < 2 || max - min < 1e-3f)
            {
                // Flat / degenerate curve (e.g. a distant comet whose brightness barely moves): a bare
                // label reads clearer than a flat line pinned to an arbitrary edge.
                DrawText("vmag ~flat".AsSpan(), fontPath, x, y, w, h,
                    fontSize * 0.8f, SearchDimText, TextAlign.Near, TextAlign.Center);
                return;
            }

            var gutter = 30f * dpiScale;
            var plotX = x + gutter;
            var plotW = w - gutter;
            var range = max - min;

            // Axis frame (left + bottom).
            DrawLine(plotX, y, plotX, y + h, SparklineAxis);
            DrawLine(plotX, y + h, plotX + plotW, y + h, SparklineAxis);

            // Bright (min mag) at top, faint (max mag) at bottom.
            var labelFont = fontSize * 0.7f;
            DrawText($"{min:F1}".AsSpan(), fontPath, x, y - labelFont * 0.2f, gutter - 3f, labelFont * 1.4f,
                labelFont, SearchDimText, TextAlign.Far, TextAlign.Near);
            DrawText($"{max:F1}".AsSpan(), fontPath, x, y + h - labelFont * 1.2f, gutter - 3f, labelFont * 1.4f,
                labelFont, SearchDimText, TextAlign.Far, TextAlign.Near);

            var n = mags.Length;
            var prevX = 0f;
            var prevY = 0f;
            var prevValid = false;
            for (var i = 0; i < n; i++)
            {
                if (float.IsNaN(mags[i]))
                {
                    prevValid = false;
                    continue;
                }
                var fx = plotX + plotW * (n == 1 ? 0.5f : i / (float)(n - 1));
                var fy = y + h * ((mags[i] - min) / range); // min -> top, max -> bottom
                if (prevValid)
                {
                    DrawLine(prevX, prevY, fx, fy, SparklineLine);
                }
                prevX = fx;
                prevY = fy;
                prevValid = true;
            }

            // "Now" marker: the centre sample (the curve is centred on the viewing instant).
            var mid = n / 2;
            if (!float.IsNaN(mags[mid]))
            {
                var nx = plotX + plotW * (n == 1 ? 0.5f : mid / (float)(n - 1));
                var ny = y + h * ((mags[mid] - min) / range);
                DrawLine(nx, y, nx, y + h, new RGBAColor32(SparklineNow.Red, SparklineNow.Green, SparklineNow.Blue, 0x40));
                FillCircle(nx, ny, 2.5f, SparklineNow);
            }
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

            // Modifiers must be read BEFORE Release resets the gesture to idle.
            var downModifiers = _mapGesture.DownModifiers;
            if (_mapGesture.Release(upX, upY) != GestureOutcome.Tap)
            {
                return false; // never armed (press didn't reach the map) or classified as a drag-pan
            }

            PostSignal(new SkyMapClickSelectSignal(upX, upY, downModifiers));
            return true;
        }

        /// <summary>
        /// Arm the click-vs-drag gesture at the mouse-down location (and modifiers).
        /// Call from the tab's <c>MouseDown</c> handler. The modifiers are captured here
        /// because <see cref="InputEvent.MouseUp"/> does not carry them — only
        /// <see cref="InputEvent.MouseDown"/> does — and the click-select fires on mouse-up.
        /// </summary>
        protected void RememberMouseDown(float x, float y, InputModifier modifiers = InputModifier.None)
            => _mapGesture.Arm(x, y, modifiers, DpiScale);

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
