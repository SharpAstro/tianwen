using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic Planner tab. Layout: altitude chart fills the full renderer (drawn first),
    /// then target list and details panel are painted on top with opaque backgrounds.
    /// </summary>
    public class PlannerTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {

        // Layout constants (at 1x scale)
        private const float BaseTargetListWidth    = 330f;
        private const float BaseDetailsPanelHeight = 120f;
        private static readonly float BaseFontSize     = GuiTheme.Metrics.BaseFontSize;
        private static readonly float BaseHeaderHeight = GuiTheme.Metrics.HeaderHeight;
        private const float BaseItemHeight         = 22f;
        private const float BasePadding            = 6f;

        // Colors
        private static readonly RGBAColor32 PanelBgOpaque   = new RGBAColor32(0x1a, 0x1a, 0x22, 0xff);
        private static readonly RGBAColor32 HeaderBg        = GuiTheme.Palette.HeaderBg;
        private static readonly RGBAColor32 HeaderText      = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 ItemText        = GuiTheme.Palette.BodyText;
        private static readonly RGBAColor32 SelectedBg      = GuiTheme.Palette.Selection;
        private static readonly RGBAColor32 SelectedText    = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 PinnedBg        = new RGBAColor32(0x18, 0x2a, 0x28, 0xff);
        private static readonly RGBAColor32 PinnedText      = new RGBAColor32(0x66, 0xdd, 0xcc, 0xff);
        private static readonly RGBAColor32 RemoveBtnBg     = new RGBAColor32(0x55, 0x22, 0x22, 0xff);
        private static readonly RGBAColor32 RemoveBtnText   = new RGBAColor32(0xff, 0x88, 0x88, 0xff);
        private static readonly RGBAColor32 DimText         = new RGBAColor32(0x77, 0x77, 0x88, 0xff);
        private static readonly RGBAColor32 DetailsBg       = new RGBAColor32(0x14, 0x14, 0x1e, 0xff);
        private static readonly RGBAColor32 DetailsNameText = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 DetailsInfoText = new RGBAColor32(0xaa, 0xaa, 0xaa, 0xff);
        private static readonly RGBAColor32 SeparatorColor  = GuiTheme.Palette.Separator;
        private static readonly RGBAColor32 FilterBtnBg     = new RGBAColor32(0x35, 0x35, 0x48, 0xff);
        private static readonly RGBAColor32 ActiveFilterBg  = new RGBAColor32(0x30, 0x50, 0x30, 0xff);
        private static readonly RGBAColor32 FilterBtnText   = new RGBAColor32(0xdd, 0xdd, 0xdd, 0xff);
        private static readonly RGBAColor32 DropdownBg       = new RGBAColor32(0x22, 0x22, 0x35, 0xff);
        private static readonly RGBAColor32 DropdownSelBg    = GuiTheme.Palette.Selection;
        private static readonly RGBAColor32 DropdownBorder   = new RGBAColor32(0x44, 0x44, 0x60, 0xff);

        private IReadOnlyList<ScoredTarget> _lastFilteredTargets = [];

        /// <summary>Reference to the planner state from the last Render call.</summary>
        private PlannerState? _state;


        // Layout rects computed during Render, used by hit testing
        private RectF32 _targetListRect;
        private RectF32 _listItemsRect;
        private RectF32 _chartRect;
        private (float X, float Y)? _appMousePosition;
        private DateTimeOffset? _currentTime;
        private ITimeProvider? _timeProvider;
        private float _itemHeight;
        private float _searchBarBottom;
        private float _searchBarLeft;
        private float _searchBarWidth;
        private RectF32 _scrollBarTrackRect;    // captured during render for drag hit-testing
        private float _scrollBarDpiScale;
        private ScrollBarDragState _targetScrollDrag;

        /// <summary>The filtered target list from the last render pass.</summary>
        public IReadOnlyList<ScoredTarget> FilteredTargets => _lastFilteredTargets;

        /// <summary>The target list panel rect from the last render pass (for scroll wheel detection).</summary>
        public RectF32 TargetListRect => _targetListRect;

        /// <summary>Current scroll offset (in items) for the target list.</summary>
        public int ScrollOffset { get; set; }

        /// <summary>Number of visible rows in the last render (set during RenderTargetList).</summary>
        public int VisibleRows { get; private set; }

        /// <summary>
        /// Adjusts ScrollOffset so that the item at <paramref name="index"/> is visible.
        /// </summary>
        public void EnsureVisible(int index)
        {
            if (index < ScrollOffset)
            {
                ScrollOffset = index;
            }
            else if (VisibleRows > 0 && index >= ScrollOffset + VisibleRows)
            {
                ScrollOffset = index - VisibleRows + 1;
            }
        }

        /// <summary>
        /// Renders the planner tab into the given content area.
        /// The altitude chart is drawn to fill the full renderer; all panels paint on top.
        /// </summary>
        public void Render(
            PlannerState state,
            RectF32 contentRect,
            float dpiScale,
            string fontPath,
            ITimeProvider timeProvider,
            (float X, float Y) mouseScreenPosition = default,
            string? emojiFontPath = null)
        {
            _state = state;
            var targetListWidth  = BaseTargetListWidth * dpiScale;
            var detailsHeight    = BaseDetailsPanelHeight * dpiScale;
            var headerHeight     = BaseHeaderHeight * dpiScale;
            var itemHeight       = BaseItemHeight * dpiScale;
            var fontSize         = BaseFontSize * dpiScale;
            var padding          = BasePadding * dpiScale;

            _itemHeight = itemHeight;
            _appMousePosition = mouseScreenPosition;

            BeginFrame();

            // Compute filtered target list (respects rating filter, always includes proposed)
            var filteredTargets = PlannerActions.GetFilteredTargets(state);
            _lastFilteredTargets = filteredTargets;

            // Layout: target list left, details bottom-right, chart fills remainder
            var layout = new PixelLayout(contentRect);
            _targetListRect = layout.Dock(PixelDockStyle.Left, targetListWidth);
            var detailsRect = layout.Dock(PixelDockStyle.Bottom, detailsHeight);
            _chartRect = layout.Fill();

            // --- 1. Altitude chart ---
            var selectedIndex = state.SelectedTargetIndex >= 0
                                && state.SelectedTargetIndex < filteredTargets.Count
                ? state.SelectedTargetIndex
                : (int?)null;

            var now = timeProvider.GetUtcNow().ToOffset(state.SiteTimeZone);
            _currentTime = now;
            _timeProvider = timeProvider;

            // Always hand the chart the live "now". The renderer only draws the elapsed-time
            // shade when now falls inside the displayed night's [tStart, tEnd] window, so it
            // self-hides for any other night. Gating on PlanningDate == null was wrong: arrowing
            // back to today sets PlanningDate to a concrete (non-null) date, which used to suppress
            // the shade even though the chart is showing tonight.
            var chartCurrentTime = now;
            // Only show mouse follower when not dragging and mouse is inside the chart area
            (float, float)? mousePos = null;
            if (state.DraggingSliderIndex < 0 && _appMousePosition is var (mx, my)
                && _chartRect.Contains(mx, my))
            {
                mousePos = (mx, my);
            }

            RenderChart(state, _chartRect, fontPath, selectedIndex, chartCurrentTime, mousePos, emojiFontPath);

            // Register slider hit regions for drag interaction
            RegisterSliderHitRegions(state, dpiScale);

            // --- 2. Target list panel (opaque background, left side of content area) ---
            RenderTargetList(
                state, fontPath, dpiScale, _targetListRect,
                headerHeight, itemHeight, fontSize, padding);

            // --- 3. Details panel (opaque background, bottom-right of content area) ---
            RenderDetailsPanel(state, fontPath, detailsRect, fontSize, padding);
        }

        /// <summary>Chart rect from last render (for slider drag coordinate conversion).</summary>
        public RectF32 ChartRect => _chartRect;

        /// <summary>
        /// Renders the altitude chart. Override in GPU-backed subclasses to use cached textures.
        /// Default implementation renders directly via the renderer.
        /// </summary>
        protected virtual void RenderChart(PlannerState state, RectF32 chartRect, string fontPath,
            int? selectedIndex, DateTimeOffset? chartCurrentTime, (float, float)? mousePos,
            string? emojiFontPath = null)
        {
            AltitudeChartRenderer.Render(Renderer, state, fontPath,
                (int)chartRect.X, (int)chartRect.Y, (int)chartRect.Width, (int)chartRect.Height,
                selectedIndex, chartCurrentTime, mousePos, emojiFontPath);
        }

        private void RegisterSliderHitRegions(PlannerState state, float dpiScale)
        {
            if (state.HandoffSliders.Length == 0)
            {
                return;
            }

            var (tStart, tEnd, plotX, plotY, plotW, plotH) = AltitudeChartRenderer.GetChartPlotLayout(
                state, (int)_chartRect.X, (int)_chartRect.Y, (int)_chartRect.Width, (int)_chartRect.Height);
            var tRange = (tEnd - tStart).TotalHours;

            var hitW = 10f * dpiScale;
            for (var i = 0; i < state.HandoffSliders.Length; i++)
            {
                var fraction = (state.HandoffSliders[i] - tStart).TotalHours / tRange;
                var sliderX = plotX + (float)(fraction * plotW);

                // Hit region spans only the plot rows -- NOT the full chart height -- so clicking
                // the weather band / icons above the plot never grabs a handoff divider.
                RegisterClickable(sliderX - hitW / 2, plotY, hitW, plotH,
                    new HitResult.SliderHit(i));
            }
        }

        // -----------------------------------------------------------------------
        // Target list
        // -----------------------------------------------------------------------

        private void RenderTargetList(
            PlannerState state,
            string fontPath,
            float dpiScale,
            RectF32 rect,
            float headerHeight, float itemHeight, float fontSize, float padding)
        {
            var scrollBarWidth = ScrollBar.Width(dpiScale);
            var listW = rect.Width - scrollBarWidth;
            var searchH = (int)(itemHeight * 1.1f);

            // Sub-layout: header top, search strip below, items fill remainder
            var listLayout = new PixelLayout(rect);
            var headerRect = listLayout.Dock(PixelDockStyle.Top, headerHeight);
            var searchStripRect = listLayout.Dock(PixelDockStyle.Top, searchH + 4f);
            _listItemsRect = listLayout.Fill();

            // Save search bar geometry for dropdown overlay
            _searchBarBottom = searchStripRect.Bottom;
            _searchBarLeft = rect.X + padding;
            _searchBarWidth = listW - padding * 2f;

            // Opaque background covers the chart behind the list
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBgOpaque);

            // Header row with clickable filter button
            FillRect(headerRect.X, headerRect.Y, headerRect.Width, headerRect.Height, HeaderBg);
            DrawText("Tonight's Best".AsSpan(), fontPath,
                headerRect.X + padding, headerRect.Y, listW * 0.6f, headerRect.Height,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

            // Filter button on the right side of header
            var filterBtnLabel = state.MinRatingFilter > 0f
                ? $"\u2605{state.MinRatingFilter:F0}+"
                : "All";
            var filterBtnW = MeasureButtonWidth(filterBtnLabel, fontPath, fontSize * 0.9f, padding * 1.5f);
            var filterBtnH = headerRect.Height * 0.75f;
            var filterBtnX = headerRect.X + listW - filterBtnW - padding;
            var filterBtnY = headerRect.Y + (headerRect.Height - filterBtnH) / 2f;
            var filterBtnBg = state.MinRatingFilter > 0f ? ActiveFilterBg : FilterBtnBg;
            RenderButton(filterBtnLabel, filterBtnX, filterBtnY, filterBtnW, filterBtnH,
                fontPath, fontSize * 0.9f, filterBtnBg, FilterBtnText, "CycleFilter",
                _ =>
                {
                    PlannerActions.CycleRatingFilter(state);
                    state.SelectedTargetIndex = 0;
                    ScrollOffset = 0;
                });

            // Search input below header (within the search strip, with 2px top gap)
            RenderTextInput(state.SearchInput,
                (int)(rect.X + padding), (int)(searchStripRect.Y + 2),
                (int)(listW - padding * 2f), searchH, fontPath, fontSize * 0.9f);

            var filtered = _lastFilteredTargets;
            var totalItems = filtered.Count;
            var visibleRows  = Math.Max(1, (int)(_listItemsRect.Height / itemHeight));
            VisibleRows = visibleRows;
            var maxScroll    = Math.Max(0, totalItems - visibleRows);

            // Clamp scroll
            if (ScrollOffset < 0)       ScrollOffset = 0;
            if (ScrollOffset > maxScroll) ScrollOffset = maxScroll;

            var pinnedCount = state.PinnedCount;
            var drawnSeparator = false;

            for (var i = ScrollOffset; i < totalItems; i++)
            {
                var rowY = _listItemsRect.Y + (i - ScrollOffset) * itemHeight;

                // Draw separator line between pinned and unpinned sections
                if (!drawnSeparator && i >= pinnedCount && pinnedCount > 0)
                {
                    FillRect(rect.X + padding, rowY - 1f, listW - padding * 2f, 1f, SeparatorColor);
                    drawnSeparator = true;
                }

                if (rowY + itemHeight > _listItemsRect.Bottom)
                {
                    break;
                }

                var scored     = filtered[i];
                var isSelected = i == state.SelectedTargetIndex;
                var isPinned   = i < pinnedCount;

                var rowBg = isSelected ? SelectedBg
                          : isPinned   ? PinnedBg
                                       : PanelBgOpaque;
                var rowTextColor = isSelected ? SelectedText
                                 : isPinned   ? PinnedText
                                              : ItemText;

                var capturedIdx = i;

                // Altitude / peak time shown right-aligned (start time for pinned, peak altitude otherwise).
                string infoStr;
                if (isPinned)
                {
                    var startTime = i == 0 || state.HandoffSliders.Length == 0
                        ? state.AstroDark
                        : i - 1 < state.HandoffSliders.Length
                            ? state.HandoffSliders[i - 1]
                            : scored.OptimalStart;
                    infoStr = startTime.ToOffset(state.SiteTimeZone).ToString("HH:mm");
                }
                else
                {
                    infoStr = $"{scored.OptimalAltitude:F0}\u00b0";
                }

                // Pin/unpin button leaf: [-] removes a pinned target, [+] pins an unpinned one. Its own
                // hit wins over the row-selection hit for the button column (inner registrations win).
                Layout.Node pinLeaf;
                if (isPinned)
                {
                    var capturedPinIdx = PlannerActions.FindProposalIndex(state.Proposals, scored.Target);
                    pinLeaf = Layout.Builder.Text("\u2212", BaseFontSize, RemoveBtnText, TextAlign.Center, TextAlign.Center)
                        .WFixed(BaseFontSize * 1.5f).HStar().Bg(RemoveBtnBg)
                        .Clickable(
                            capturedPinIdx >= 0 ? new HitResult.ButtonHit("RemoveProposal") : null,
                            capturedPinIdx >= 0 ? (Action<InputModifier>)(_ =>
                            {
                                PlannerActions.RemoveProposal(state, capturedPinIdx);
                                if (state.SelectedTargetIndex >= state.PinnedCount)
                                {
                                    state.SelectedTargetIndex = Math.Max(0, state.SelectedTargetIndex - 1);
                                }
                            }) : null);
                }
                else
                {
                    var capturedTarget = scored.Target;
                    pinLeaf = Layout.Builder.Text("+", BaseFontSize, PinnedText, TextAlign.Center, TextAlign.Center)
                        .WFixed(BaseFontSize * 1.5f).HStar().Bg(PinnedBg)
                        .Clickable(new HitResult.ButtonHit("AddProposal"), _ =>
                        {
                            // Match the keyboard-pin behaviour: the selection follows the pinned target
                            // up into the pinned section, and we scroll it into view.
                            PlannerActions.ToggleProposal(state, capturedTarget, followPinnedSelection: true);
                            EnsureVisible(state.SelectedTargetIndex);
                        });
                }

                // Whole row: [pad | name * | type | info | pad | pin]. Column widths + fonts are raw design
                // units (the engine applies dpiScale); the bounds rect is listW px wide so the Star name cell
                // fills exactly what the old nameW computed. The row carries the select hit; pinLeaf its own.
                Layout.Node Spacer() => Layout.Builder.Spacer().ColW(BasePadding);
                Layout.Node Cell(string text, float fontMul, RGBAColor32 color, TextAlign halign, float widthDesign) =>
                    widthDesign > 0f
                        ? Layout.Builder.Text(text, BaseFontSize * fontMul, color, halign, TextAlign.Center).WFixed(widthDesign).HStar()
                        : Layout.Builder.Text(text, BaseFontSize * fontMul, color, halign, TextAlign.Center).Stretch();
                var row = Layout.Builder.HStack(
                    Spacer(),
                    Cell(scored.Target.Name, 1f, rowTextColor, TextAlign.Near, 0f),
                    Cell(scored.ObjectType.ToAbbreviation(), 0.85f, DimText, TextAlign.Near, BaseFontSize * 3.2f),
                    Cell(infoStr, 1f, isSelected ? SelectedText : DimText, TextAlign.Far, BaseFontSize * 3.5f),
                    Spacer(),
                    pinLeaf)
                    .Bg(rowBg)
                    .Clickable(new HitResult.ListItemHit("TargetList", i), _ => { state.SelectedTargetIndex = capturedIdx; state.NeedsRedraw = true; });
                RenderLayout(row, new RectF32(rect.X, rowY, listW, itemHeight), fontPath, dpiScale);
            }

            var sbX = rect.X + listW;
            _scrollBarTrackRect = new RectF32(sbX, _listItemsRect.Y, scrollBarWidth, _listItemsRect.Height);
            _scrollBarDpiScale = dpiScale;
            ScrollBar.Draw(FillRect, sbX, _listItemsRect.Y, _listItemsRect.Height,
                totalItems, visibleRows, ScrollOffset, dpiScale);

            // Autocomplete dropdown (overlay, painted last so it's on top)
            if (state.Suggestions.Count > 0 && state.SearchInput.IsActive)
            {
                RenderSuggestionDropdown(state, fontPath, itemHeight, fontSize, padding);
            }
        }

        // -----------------------------------------------------------------------
        // Autocomplete dropdown
        // -----------------------------------------------------------------------

        private void RenderSuggestionDropdown(
            PlannerState state,
            string fontPath,
            float itemHeight, float fontSize, float padding)
        {
            var suggestions = state.Suggestions;
            var dropdownH = suggestions.Count * itemHeight;
            var dropdownY = _searchBarBottom;

            // Clamp to not extend past the target list panel
            var maxH = _targetListRect.Bottom - dropdownY;
            if (dropdownH > maxH)
            {
                dropdownH = maxH;
            }

            // Background + border
            FillRect(_searchBarLeft - 1f, dropdownY - 1f, _searchBarWidth + 2f, dropdownH + 2f, DropdownBorder);
            FillRect(_searchBarLeft, dropdownY, _searchBarWidth, dropdownH, DropdownBg);

            for (var i = 0; i < suggestions.Count; i++)
            {
                var rowY = dropdownY + i * itemHeight;
                if (rowY + itemHeight > dropdownY + dropdownH)
                {
                    break;
                }

                var isHighlighted = i == state.SuggestionIndex;
                if (isHighlighted)
                {
                    FillRect(_searchBarLeft, rowY, _searchBarWidth, itemHeight, DropdownSelBg);
                }

                DrawText(suggestions[i].AsSpan(), fontPath,
                    _searchBarLeft + padding, rowY, _searchBarWidth - padding * 2f, itemHeight,
                    fontSize * 0.9f, isHighlighted ? SelectedText : ItemText, TextAlign.Near, TextAlign.Center);

                RegisterClickable(_searchBarLeft, rowY, _searchBarWidth, itemHeight,
                    new HitResult.ListItemHit("Suggestion", i));
            }
        }

        // -----------------------------------------------------------------------
        // Details panel
        // -----------------------------------------------------------------------

        private void RenderDetailsPanel(
            PlannerState state,
            string fontPath,
            RectF32 rect,
            float fontSize, float padding)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, DetailsBg);

            // Separator line at top
            FillRect(rect.X, rect.Y, rect.Width, 1f, SeparatorColor);

            var lines = PlannerDetails.GetLines(state, _lastFilteredTargets, _currentTime);
            if (lines.Count == 0)
            {
                DrawText("Select a target to see details.".AsSpan(), fontPath,
                    rect.X + padding, rect.Y, rect.Width - padding * 2f, rect.Height,
                    fontSize, DimText, TextAlign.Near, TextAlign.Center);
                return;
            }

            var lineH = rect.Height / lines.Count;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var y = rect.Y + lineH * i;

                // First line is the name (larger, white), rest are info (normal, grey)
                var fs = i == 0 ? fontSize * 1.25f : fontSize;
                var color = i == 0 ? DetailsNameText
                    : line.StartsWith("Imaging:") && line.Contains("m)") && !line.Contains("h ")
                        ? new RGBAColor32(0xff, 0xd7, 0x00, 0xff) // warning color for short imaging windows
                        : line.StartsWith("Also:") ? DimText
                        : DetailsInfoText;

                DrawText(line.AsSpan(), fontPath,
                    rect.X + padding, y, rect.Width - padding * 2f, lineH,
                    fs, color, TextAlign.Near, TextAlign.Center);
            }
        }

        // -----------------------------------------------------------------------
        // Input handling
        // -----------------------------------------------------------------------

        public override bool HandleInput(InputEvent evt) => evt switch
        {
            InputEvent.Scroll(var scrollY, var mouseX, var mouseY, _)
                when _targetListRect.Contains(mouseX, mouseY) => HandleTargetListScroll(scrollY),
            InputEvent.MouseDown(var dx, var dy, _, _, _) when HandleTargetListMouseDown(dx, dy) => true,
            // Mouse move consumed while dragging the scrollbar, otherwise flags redraw for follower overlay.
            InputEvent.MouseMove(_, var my) when _targetScrollDrag.IsDragging && HandleTargetListMouseMove(my) => true,
            InputEvent.MouseMove(var mx, var my) => _chartRect.Contains(mx, my),
            InputEvent.MouseUp(_, _, _) when _targetScrollDrag.IsDragging => HandleTargetListMouseUp(),
            InputEvent.KeyDown(var key, var modifiers) => HandlePlannerKey(key, modifiers),
            _ => false
        };

        private bool HandleTargetListScroll(float scrollY)
        {
            ScrollOffset = ScrollBar.HandleWheel(scrollY, ScrollOffset, _lastFilteredTargets.Count, VisibleRows);
            if (_state is not null)
            {
                _state.NeedsRedraw = true;
            }
            return true;
        }

        private bool HandleTargetListMouseDown(float mx, float my)
        {
            var next = ScrollBar.HandleMouseDown(
                ref _targetScrollDrag, mx, my,
                _scrollBarTrackRect.X, _scrollBarTrackRect.Y, _scrollBarTrackRect.Height,
                _lastFilteredTargets.Count, VisibleRows, ScrollOffset, _scrollBarDpiScale);
            if (next is not { } offset) return false;
            ScrollOffset = offset;
            if (_state is not null) _state.NeedsRedraw = true;
            return true;
        }

        private bool HandleTargetListMouseMove(float my)
        {
            var next = ScrollBar.HandleMouseMove(
                in _targetScrollDrag, my,
                _scrollBarTrackRect.Y, _scrollBarTrackRect.Height,
                _lastFilteredTargets.Count, VisibleRows, _scrollBarDpiScale);
            if (next is not { } offset || offset == ScrollOffset) return false;
            ScrollOffset = offset;
            if (_state is not null) _state.NeedsRedraw = true;
            return true;
        }

        private bool HandleTargetListMouseUp()
        {
            ScrollBar.HandleMouseUp(ref _targetScrollDrag);
            return true;
        }

        private bool HandlePlannerKey(InputKey key, InputModifier modifiers)
        {
            if (_state is not { } state)
            {
                return false;
            }

            // Slider keyboard takes priority (Left/Right/Enter/Escape/Tab when a slider is selected)
            if (PlannerActions.HandleSliderKeyboard(state, key, modifiers))
            {
                return true;
            }

            var filtered = _lastFilteredTargets;

            switch (key)
            {
                case InputKey.Up:
                    if (state.SelectedTargetIndex > 0)
                    {
                        state.SelectedTargetIndex--;
                        EnsureVisible(state.SelectedTargetIndex);
                        state.NeedsRedraw = true;
                    }
                    return true;

                case InputKey.Down:
                    if (state.SelectedTargetIndex < filtered.Count - 1)
                    {
                        state.SelectedTargetIndex++;
                        EnsureVisible(state.SelectedTargetIndex);
                        state.NeedsRedraw = true;
                    }
                    return true;

                case InputKey.Enter when state.SelectedTargetIndex >= 0 && state.SelectedTargetIndex < filtered.Count:
                    PlannerActions.ToggleProposal(state, filtered[state.SelectedTargetIndex].Target, followPinnedSelection: true);
                    // On a pin the selection followed the target into the pinned section at the top;
                    // scroll it into view (a no-op when it's already visible).
                    EnsureVisible(state.SelectedTargetIndex);
                    return true;

                case InputKey.P when state.SelectedTargetIndex >= 0 && state.SelectedTargetIndex < filtered.Count:
                    var propIdx = PlannerActions.FindProposalIndex(state.Proposals, filtered[state.SelectedTargetIndex].Target);
                    if (propIdx >= 0)
                    {
                        PlannerActions.CyclePriority(state, propIdx);
                    }
                    return true;

                case InputKey.F:
                    PlannerActions.CycleRatingFilter(state);
                    state.SelectedTargetIndex = 0;
                    ScrollOffset = 0;
                    return true;

                case InputKey.M:
                    // Cycle min altitude and trigger recompute via the existing loop
                    state.MinHeightAboveHorizon = state.MinHeightAboveHorizon switch
                    {
                        15 => 20, 20 => 25, 25 => 30, 30 => 35, _ => 15
                    };
                    state.NeedsRecompute = true;
                    state.NeedsRedraw = true;
                    return true;

                case InputKey.T:
                    PlannerActions.ResetPlanningDate(state);
                    return true;

                case InputKey.Left when _timeProvider is not null:
                    PlannerActions.ShiftPlanningDate(state, _timeProvider, -1);
                    return true;

                case InputKey.Right when _timeProvider is not null:
                    PlannerActions.ShiftPlanningDate(state, _timeProvider, 1);
                    return true;

                // PageUp/PageDown mirror Right/Left (date +1 / -1), matching the sky-map tab's
                // convention so the two tabs step the night identically. No scrub fold here —
                // the planner has no visual scrub of its own.
                case InputKey.PageUp when _timeProvider is not null:
                    PlannerActions.ShiftPlanningDate(state, _timeProvider, 1);
                    return true;

                case InputKey.PageDown when _timeProvider is not null:
                    PlannerActions.ShiftPlanningDate(state, _timeProvider, -1);
                    return true;

                default:
                    return false;
            }
        }

        // Drawing helpers inherited from VkTabBase: FillRect, DrawText, RenderButton, RegisterClickable, etc.
    }
}
