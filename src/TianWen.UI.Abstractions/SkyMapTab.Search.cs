using System;
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
        // ── Modal colours (Stellarium-inspired dark chrome) ──
        private static readonly RGBAColor32 SearchBackdrop   = new(0x00, 0x00, 0x00, 0x80);
        private static readonly RGBAColor32 SearchPanelBg    = new(0x22, 0x22, 0x28, 0xF0);
        private static readonly RGBAColor32 SearchPanelBorder = new(0x50, 0x50, 0x60, 0xFF);
        private static readonly RGBAColor32 SearchHeaderBg   = new(0x30, 0x30, 0x38, 0xFF);
        private static readonly RGBAColor32 SearchTabActive  = new(0x3A, 0x3A, 0x45, 0xFF);
        private static readonly RGBAColor32 SearchTabInactive = new(0x20, 0x20, 0x28, 0xFF);
        private static readonly RGBAColor32 SearchRowHover   = new(0xC0, 0x90, 0x30, 0xD0);
        private static readonly RGBAColor32 SearchText       = new(0xDD, 0xDD, 0xDD, 0xFF);
        private static readonly RGBAColor32 SearchDimText    = new(0x80, 0x80, 0x88, 0xFF);
        private static readonly RGBAColor32 SelectionMarker  = new(0xFF, 0xEE, 0x60, 0xFF);

        private const float SearchPanelWidth  = 480f;
        private const float SearchPanelHeight = 500f;
        private const float SearchRowHeight   = 28f;

        // Click-vs-drag threshold in screen pixels. Under this = click (select an
        // object); over this = drag-pan. Matches Stellarium and OS conventions.
        private const float ClickDragThresholdPx = 4f;

        // Saved mouse-down position for click-vs-drag detection.
        private float _mouseDownX, _mouseDownY;

        /// <summary>
        /// Draws the search modal and/or the info panel. Called last in <see cref="Render"/>
        /// so its clickable regions take priority over map drag gestures.
        /// </summary>
        protected void DrawSearchAndInfoPanel(
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
                DrawInfoPanel(info, contentRect, fontPath, dpiScale,
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
            // Full-content backdrop — swallows clicks outside the panel.
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, SearchBackdrop);
            RegisterClickable(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                new HitResult.ButtonHit("SearchBackdrop"),
                _ => PostSignal(new CloseSkyMapSearchSignal()));

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

            // Close X button
            var closeW = headerH;
            RegisterClickable(px + pw - closeW, py, closeW, headerH,
                new HitResult.ButtonHit("SearchClose"),
                _ => PostSignal(new CloseSkyMapSearchSignal()));
            DrawText("X".AsSpan(), fontPath,
                px + pw - closeW, py, closeW, headerH, fontSize, SearchText, TextAlign.Center, TextAlign.Center);

            // Tab row — Phase 1 only wires Object. Others render dim so the layout
            // shows the final shape without pretending they work yet.
            var tabRowY = py + headerH;
            var tabH = 28f * dpiScale;
            var tabW = pw / 5f;
            DrawTab(px + tabW * 0f, tabRowY, tabW, tabH, "Object",   SkyMapSearchTab.Object,   fontPath, fontSize, enabled: true);
            DrawTab(px + tabW * 1f, tabRowY, tabW, tabH, "SIMBAD",   SkyMapSearchTab.Simbad,   fontPath, fontSize, enabled: false);
            DrawTab(px + tabW * 2f, tabRowY, tabW, tabH, "Position", SkyMapSearchTab.Position, fontPath, fontSize, enabled: false);
            DrawTab(px + tabW * 3f, tabRowY, tabW, tabH, "Lists",    SkyMapSearchTab.Lists,    fontPath, fontSize, enabled: false);
            DrawTab(px + tabW * 4f, tabRowY, tabW, tabH, "Options",  SkyMapSearchTab.Options,  fontPath, fontSize, enabled: false);

            // Search input
            var inputY = tabRowY + tabH + 12f * dpiScale;
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
                SearchRowHeight * dpiScale, fontPath, fontSize);

            // db + site are passed to keep the hot path closure-free; right now they
            // are only needed for click-to-select on the map, not inside the modal.
            _ = db;
            _ = siteLat; _ = siteLon; _ = viewingTime;
            _ = site;
        }

        private void DrawTab(float x, float y, float w, float h, string label,
            SkyMapSearchTab tab, string fontPath, float fontSize, bool enabled)
        {
            var active = State.Search.ActiveTab == tab;
            FillRect(x, y, w, h, active ? SearchTabActive : SearchTabInactive);
            DrawText(label.AsSpan(), fontPath, x, y, w, h, fontSize,
                enabled ? SearchText : SearchDimText, TextAlign.Center, TextAlign.Center);
            if (enabled)
            {
                RegisterClickable(x, y, w, h, new HitResult.ButtonHit($"SearchTab:{tab}"));
            }
        }

        private void DrawResults(
            System.Collections.Immutable.ImmutableArray<SkyMapSearchResult> results,
            int selectedIndex,
            float x, float y, float w, float h,
            float rowH, string fontPath, float fontSize)
        {
            if (results.IsDefaultOrEmpty)
            {
                DrawText("Type to search catalog...".AsSpan(), fontPath,
                    x, y, w, h, fontSize, SearchDimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var visibleRows = (int)(h / rowH);
            var count = Math.Min(results.Length, visibleRows);

            for (var i = 0; i < count; i++)
            {
                var rowY = y + i * rowH;
                var entry = results[i];
                var isSelected = i == selectedIndex;

                if (isSelected)
                {
                    FillRect(x, rowY, w, rowH, SearchRowHover);
                }

                // Row contents: name + optional V-mag in smaller dim text on the right.
                var namePad = 12f;
                DrawText(entry.Display.AsSpan(), fontPath,
                    x + namePad, rowY, w - 80f, rowH, fontSize,
                    isSelected ? new RGBAColor32(0x00, 0x00, 0x00, 0xFF) : SearchText,
                    TextAlign.Near, TextAlign.Center);

                if (!float.IsNaN(entry.VMag))
                {
                    var magText = $"{entry.VMag:F1}m";
                    DrawText(magText.AsSpan(), fontPath,
                        x + w - 60f, rowY, 50f, rowH, fontSize * 0.9f,
                        isSelected ? new RGBAColor32(0x00, 0x00, 0x00, 0xFF) : SearchDimText,
                        TextAlign.Far, TextAlign.Center);
                }

                var capturedIndex = i;
                RegisterClickable(x, rowY, w, rowH,
                    new HitResult.ListItemHit("SearchResult", capturedIndex),
                    _ =>
                    {
                        State.Search.SelectedResultIndex = capturedIndex;
                        PostSignal(new SkyMapSearchCommitSignal());
                    });
            }
        }

        private void DrawInfoPanel(
            in SkyMapInfoPanelData info,
            RectF32 contentRect, string fontPath, float dpiScale,
            double pixelsPerRadian, float cx, float cy)
        {
            var pw = 300f * dpiScale;
            var ph = 170f * dpiScale;
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

            // Canonical designation + constellation + type
            var canon = string.IsNullOrEmpty(info.Canonical) ? "(no designation)" : info.Canonical;
            var constell = info.Constellation != default ? info.Constellation.ToString() : "";
            var objType = info.ObjType != ObjectType.Unknown ? info.ObjType.ToString() : "";
            var subtitle = constell.Length > 0 && objType.Length > 0
                ? $"{canon}  {constell}  {objType}"
                : canon;
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
            var rtsLine = info.NeverRises ? "Never rises (below horizon)"
                        : info.Circumpolar ? $"Circumpolar   Transit {FormatHHMM(info.TransitTime)}"
                        : $"Rise {FormatHHMM(info.RiseTime)}   Transit {FormatHHMM(info.TransitTime)}   Set {FormatHHMM(info.SetTime)}";
            DrawText(rtsLine.AsSpan(), fontPath,
                textX, py + row, textW, rowH, fontSize, SearchText, TextAlign.Near, TextAlign.Center);

            // Close button — top-right of the info panel.
            var closeSize = 20f * dpiScale;
            RegisterClickable(px + pw - closeSize, py, closeSize, closeSize,
                new HitResult.ButtonHit("InfoPanelClose"),
                _ =>
                {
                    State.Search.InfoPanel = null;
                    State.NeedsRedraw = true;
                });
            DrawText("X".AsSpan(), fontPath,
                px + pw - closeSize, py, closeSize, closeSize, fontSize * 0.9f,
                SearchDimText, TextAlign.Center, TextAlign.Center);

            // Selection marker on the map itself (crosshair circle at the object).
            if (SkyMapProjection.ProjectWithMatrix(info.RA, info.Dec, State.CurrentViewMatrix,
                pixelsPerRadian, cx, cy, out var sx, out var sy)
                && sx >= contentRect.X && sx < contentRect.X + contentRect.Width
                && sy >= contentRect.Y && sy < contentRect.Y + contentRect.Height)
            {
                DrawCircle(sx, sy, 14f * dpiScale, SelectionMarker, 1.5f);
                DrawLine(sx - 18f * dpiScale, sy, sx - 8f * dpiScale, sy, SelectionMarker);
                DrawLine(sx + 8f * dpiScale, sy, sx + 18f * dpiScale, sy, SelectionMarker);
                DrawLine(sx, sy - 18f * dpiScale, sx, sy - 8f * dpiScale, SelectionMarker);
                DrawLine(sx, sy + 8f * dpiScale, sx, sy + 18f * dpiScale, SelectionMarker);
            }
        }

        private static string FormatHHMM(DateTimeOffset? t)
            => t is { } dt ? $"{dt.LocalDateTime:HH:mm}" : "--:--";

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

            var dx = upX - _mouseDownX;
            var dy = upY - _mouseDownY;
            if (dx * dx + dy * dy > ClickDragThresholdPx * ClickDragThresholdPx)
            {
                return false;
            }

            PostSignal(new SkyMapClickSelectSignal(upX, upY));
            return true;
        }

        /// <summary>
        /// Remember the mouse-down location for click-vs-drag detection.
        /// Call from the tab's <c>MouseDown</c> handler.
        /// </summary>
        protected void RememberMouseDown(float x, float y)
        {
            _mouseDownX = x;
            _mouseDownY = y;
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
