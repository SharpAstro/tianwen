using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
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
        private const float BaseFontSize           = 14f;
        private const float BaseHeaderHeight       = 28f;
        private const float BaseItemHeight         = 22f;
        private const float BasePadding            = 6f;

        // Colors
        private static readonly RGBAColor32 PanelBgOpaque   = new RGBAColor32(0x1a, 0x1a, 0x22, 0xff);
        private static readonly RGBAColor32 HeaderBg        = new RGBAColor32(0x22, 0x22, 0x30, 0xff);
        private static readonly RGBAColor32 HeaderText      = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 ItemText        = new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff);
        private static readonly RGBAColor32 SelectedBg      = new RGBAColor32(0x20, 0x30, 0x50, 0xff);
        private static readonly RGBAColor32 SelectedText    = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 PinnedBg        = new RGBAColor32(0x18, 0x2a, 0x28, 0xff);
        private static readonly RGBAColor32 PinnedText      = new RGBAColor32(0x66, 0xdd, 0xcc, 0xff);
        private static readonly RGBAColor32 RemoveBtnBg     = new RGBAColor32(0x55, 0x22, 0x22, 0xff);
        private static readonly RGBAColor32 RemoveBtnText   = new RGBAColor32(0xff, 0x88, 0x88, 0xff);
        private static readonly RGBAColor32 DimText         = new RGBAColor32(0x77, 0x77, 0x88, 0xff);
        private static readonly RGBAColor32 DetailsBg       = new RGBAColor32(0x14, 0x14, 0x1e, 0xff);
        private static readonly RGBAColor32 DetailsNameText = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 DetailsInfoText = new RGBAColor32(0xaa, 0xaa, 0xaa, 0xff);
        private static readonly RGBAColor32 SeparatorColor  = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
        private static readonly RGBAColor32 ScrollBarBg     = new RGBAColor32(0x22, 0x22, 0x2a, 0xff);
        private static readonly RGBAColor32 ScrollBarFg     = new RGBAColor32(0x44, 0x44, 0x55, 0xff);
        private static readonly RGBAColor32 FilterBtnBg     = new RGBAColor32(0x35, 0x35, 0x48, 0xff);
        private static readonly RGBAColor32 ActiveFilterBg  = new RGBAColor32(0x30, 0x50, 0x30, 0xff);
        private static readonly RGBAColor32 FilterBtnText   = new RGBAColor32(0xdd, 0xdd, 0xdd, 0xff);
        private static readonly RGBAColor32 DropdownBg       = new RGBAColor32(0x22, 0x22, 0x35, 0xff);
        private static readonly RGBAColor32 DropdownSelBg    = new RGBAColor32(0x20, 0x30, 0x50, 0xff);
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
        private float _itemHeight;
        private float _searchBarBottom;
        private float _searchBarLeft;
        private float _searchBarWidth;

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
            TimeProvider timeProvider,
            (float X, float Y) mouseScreenPosition = default)
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

            var now = timeProvider.GetLocalNow();
            _currentTime = now;

            // Only pass current time to chart if planning for tonight
            var chartCurrentTime = state.PlanningDate.HasValue ? (DateTimeOffset?)null : now;
            // Only show mouse follower when not dragging and mouse is inside the chart area
            (float, float)? mousePos = null;
            if (state.DraggingSliderIndex < 0 && _appMousePosition is var (mx, my)
                && _chartRect.Contains(mx, my))
            {
                mousePos = (mx, my);
            }

            AltitudeChartRenderer.Render(Renderer, state, fontPath,
                (int)_chartRect.X, (int)_chartRect.Y, (int)_chartRect.Width, (int)_chartRect.Height,
                selectedIndex, chartCurrentTime, mousePos);

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

        private void RegisterSliderHitRegions(PlannerState state, float dpiScale)
        {
            if (state.HandoffSliders.Count == 0)
            {
                return;
            }

            var (tStart, tEnd, plotX, plotW) = AltitudeChartRenderer.GetChartTimeLayout(
                state, (int)_chartRect.X, (int)_chartRect.Width);
            var tRange = (tEnd - tStart).TotalHours;

            var hitW = 10f * dpiScale;
            for (var i = 0; i < state.HandoffSliders.Count; i++)
            {
                var fraction = (state.HandoffSliders[i] - tStart).TotalHours / tRange;
                var sliderX = plotX + (float)(fraction * plotW);

                var capturedIdx = i;
                RegisterClickable(sliderX - hitW / 2, _chartRect.Y, hitW, _chartRect.Height,
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
            var scrollBarWidth = 6f * dpiScale;
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
            var removeBtnW = fontSize * 1.5f;
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

                FillRect(rect.X, rowY, listW, itemHeight, rowBg);
                var capturedIdx = i;
                RegisterClickable(rect.X, rowY, listW - removeBtnW, itemHeight,
                    new HitResult.ListItemHit("TargetList", i),
                    _ => { state.SelectedTargetIndex = capturedIdx; state.NeedsRedraw = true; });

                // Pin/unpin button on the right
                var btnX = rect.X + listW - removeBtnW;
                if (isPinned)
                {
                    FillRect(btnX, rowY, removeBtnW, itemHeight, RemoveBtnBg);
                    DrawText("\u2212".AsSpan(), fontPath,
                        btnX, rowY, removeBtnW, itemHeight,
                        fontSize, RemoveBtnText, TextAlign.Center, TextAlign.Center);

                    var capturedPinIdx = state.Proposals.FindIndex(p => p.Target == scored.Target);
                    if (capturedPinIdx >= 0)
                    {
                        RegisterClickable(btnX, rowY, removeBtnW, itemHeight,
                            new HitResult.ButtonHit("RemoveProposal"),
                            _ =>
                            {
                                PlannerActions.RemoveProposal(state, capturedPinIdx);
                                if (state.SelectedTargetIndex >= state.PinnedCount)
                                {
                                    state.SelectedTargetIndex = Math.Max(0, state.SelectedTargetIndex - 1);
                                }
                            });
                    }
                }
                else
                {
                    // Unpinned: [+] pin button
                    FillRect(btnX, rowY, removeBtnW, itemHeight, PinnedBg);
                    DrawText("+".AsSpan(), fontPath,
                        btnX, rowY, removeBtnW, itemHeight,
                        fontSize, PinnedText, TextAlign.Center, TextAlign.Center);

                    var capturedTarget = scored.Target;
                    RegisterClickable(btnX, rowY, removeBtnW, itemHeight,
                        new HitResult.ButtonHit("AddProposal"),
                        _ => { PlannerActions.ToggleProposal(state, capturedTarget); });
                }

                // Target name
                var nameX = rect.X + padding;
                var typeW = fontSize * 3.2f; // fixed width for 3-4 char abbreviations
                var nameW = listW - typeW - padding * 2f - removeBtnW - fontSize * 3.5f; // remainder after type + info + button
                DrawText(scored.Target.Name.AsSpan(), fontPath,
                    nameX, rowY, nameW, itemHeight,
                    fontSize, rowTextColor, TextAlign.Near, TextAlign.Center);

                // Object type abbreviation (Gx, OC, PN, etc.)
                var typeX = nameX + nameW;
                DrawText(scored.ObjectType.ToAbbreviation().AsSpan(), fontPath,
                    typeX, rowY, typeW, itemHeight,
                    fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);

                // Altitude / peak time right-aligned
                string infoStr;
                if (isPinned)
                {
                    var startTime = i == 0 || state.HandoffSliders.Count == 0
                        ? state.AstroDark
                        : i - 1 < state.HandoffSliders.Count
                            ? state.HandoffSliders[i - 1]
                            : scored.OptimalStart;
                    infoStr = startTime.ToOffset(state.SiteTimeZone).ToString("HH:mm");
                }
                else
                {
                    infoStr = $"{scored.OptimalAltitude:F0}°";
                }
                var infoX = typeX + typeW;
                var infoW = listW - nameW - typeW - padding * 2f - removeBtnW;
                DrawText(infoStr.AsSpan(), fontPath,
                    infoX, rowY, infoW, itemHeight,
                    fontSize, isSelected ? SelectedText : DimText, TextAlign.Far, TextAlign.Center);
            }

            // Scrollbar thumb
            if (totalItems > visibleRows && maxScroll > 0)
            {
                var sbX = rect.X + listW;
                FillRect(sbX, _listItemsRect.Y, scrollBarWidth, _listItemsRect.Height, ScrollBarBg);

                var thumbH = Math.Max(20f * dpiScale, _listItemsRect.Height * visibleRows / (float)totalItems);
                var thumbY = _listItemsRect.Y + (_listItemsRect.Height - thumbH) * ScrollOffset / (float)maxScroll;
                FillRect(sbX + 1f, thumbY, scrollBarWidth - 2f, thumbH, ScrollBarFg);
            }

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
            InputEvent.KeyDown(var key, var modifiers) => HandlePlannerKey(key, modifiers),
            _ => false
        };

        private bool HandleTargetListScroll(float scrollY)
        {
            ScrollOffset = Math.Max(0, ScrollOffset - (int)scrollY * 3);
            if (_state is not null)
            {
                _state.NeedsRedraw = true;
            }
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
                    PlannerActions.ToggleProposal(state, filtered[state.SelectedTargetIndex].Target);
                    return true;

                case InputKey.P when state.SelectedTargetIndex >= 0 && state.SelectedTargetIndex < filtered.Count:
                    var propIdx = state.Proposals.FindIndex(p => p.Target == filtered[state.SelectedTargetIndex].Target);
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

                default:
                    return false;
            }
        }

        // Drawing helpers inherited from VkTabBase: FillRect, DrawText, RenderButton, RegisterClickable, etc.
    }
}
