using System;
using System.Linq;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui
{
    /// <summary>
    /// Renders the Planner tab content inside the content area provided by <see cref="VkGuiRenderer"/>.
    /// Layout: altitude chart fills the full renderer (drawn first), then target list and details panel
    /// are painted on top with opaque backgrounds.
    /// </summary>
    public sealed class VkPlannerTab : VkTabBase
    {

        // Layout constants (at 1x scale)
        private const float BaseTargetListWidth    = 300f;
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
        private static readonly RGBAColor32 ProposedMarker  = new RGBAColor32(0x00, 0xdd, 0xcc, 0xff);
        private static readonly RGBAColor32 ProposedBg      = new RGBAColor32(0x18, 0x2a, 0x28, 0xff);
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

        private IReadOnlyList<ScoredTarget> _lastFilteredTargets = [];

        /// <summary>The filtered target list from the last render pass.</summary>
        public IReadOnlyList<ScoredTarget> FilteredTargets => _lastFilteredTargets;

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

        public VkPlannerTab(VkRenderer renderer) : base(renderer)
        {
        }

        /// <summary>
        /// Renders the planner tab into the given content area.
        /// The altitude chart is drawn to fill the full renderer; all panels paint on top.
        /// </summary>
        public void Render(
            PlannerState state,
            float contentLeft,
            float contentTop,
            float contentWidth,
            float contentHeight,
            float dpiScale,
            string fontPath,
            TimeProvider timeProvider)
        {
            var targetListWidth  = BaseTargetListWidth * dpiScale;
            var detailsHeight    = BaseDetailsPanelHeight * dpiScale;
            var headerHeight     = BaseHeaderHeight * dpiScale;
            var itemHeight       = BaseItemHeight * dpiScale;
            var fontSize         = BaseFontSize * dpiScale;
            var padding          = BasePadding * dpiScale;

            BeginFrame();

            // Compute filtered target list (respects rating filter, always includes proposed)
            var filteredTargets = PlannerActions.GetFilteredTargets(state);
            _lastFilteredTargets = filteredTargets;

            // --- 1. Altitude chart in the right portion of the content area ---
            var selectedIndex = state.SelectedTargetIndex >= 0
                                && state.SelectedTargetIndex < filteredTargets.Count
                ? state.SelectedTargetIndex
                : (int?)null;

            var chartX = (int)(contentLeft + targetListWidth);
            var chartY = (int)contentTop;
            var chartW = (int)(contentWidth - targetListWidth);
            var chartH = (int)(contentHeight - detailsHeight);

            AltitudeChartRenderer.Render(Renderer, state, fontPath, chartX, chartY, chartW, chartH, selectedIndex);

            // --- 2. Target list panel (opaque background, left side of content area) ---
            RenderTargetList(
                state, fontPath, dpiScale,
                contentLeft, contentTop, targetListWidth, contentHeight,
                headerHeight, itemHeight, fontSize, padding);

            // --- 3. Details panel (opaque background, bottom-right of content area) ---
            RenderDetailsPanel(
                state, fontPath,
                contentLeft + targetListWidth,
                contentTop + contentHeight - detailsHeight,
                contentWidth - targetListWidth,
                detailsHeight,
                fontSize, padding);
        }

        /// <summary>
        /// Hit-tests the target list for a click.
        /// Returns the index in <see cref="PlannerState.TonightsBest"/>, or -1 if outside the list.
        /// </summary>
        public int HitTestTargetList(
            float x, float y,
            float contentLeft, float contentTop,
            float dpiScale)
        {
            var targetListWidth = BaseTargetListWidth * dpiScale;
            var headerHeight    = BaseHeaderHeight * dpiScale;
            var itemHeight      = BaseItemHeight * dpiScale;
            var searchH         = itemHeight * 1.1f + 4f; // matches render: searchH + 4 gap

            var listLeft = contentLeft;
            var listTop  = contentTop + headerHeight + searchH;
            var listRight = contentLeft + targetListWidth;

            if (x < listLeft || x >= listRight || y < listTop)
            {
                return -1;
            }

            var relY = y - listTop;
            return (int)(relY / itemHeight) + ScrollOffset;
        }

        // -----------------------------------------------------------------------
        // Target list
        // -----------------------------------------------------------------------

        private void RenderTargetList(
            PlannerState state,
            string fontPath,
            float dpiScale,
            float x, float y, float w, float h,
            float headerHeight, float itemHeight, float fontSize, float padding)
        {
            var scrollBarWidth = 6f * dpiScale;
            var listW = w - scrollBarWidth;

            // Opaque background covers the chart behind the list
            FillRect(x, y, w, h, PanelBgOpaque);

            // Header row with clickable filter button
            FillRect(x, y, w, headerHeight, HeaderBg);
            DrawText("Tonight's Best".AsSpan(), fontPath,
                x + padding, y, listW * 0.6f, headerHeight,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

            // Filter button on the right side of header
            var filterBtnLabel = state.MinRatingFilter > 0f
                ? $"\u2605{state.MinRatingFilter:F0}+"
                : "All";
            var filterBtnW = MeasureButtonWidth(filterBtnLabel, fontPath, fontSize * 0.9f, padding * 1.5f);
            var filterBtnH = headerHeight * 0.75f;
            var filterBtnX = x + listW - filterBtnW - padding;
            var filterBtnY = y + (headerHeight - filterBtnH) / 2f;
            var filterBtnBg = state.MinRatingFilter > 0f ? ActiveFilterBg : FilterBtnBg;
            RenderButton(filterBtnLabel, filterBtnX, filterBtnY, filterBtnW, filterBtnH,
                fontPath, fontSize * 0.9f, filterBtnBg, FilterBtnText, "CycleFilter");

            // Search input below header
            var searchH = (int)(itemHeight * 1.1f);
            RenderTextInput(state.SearchInput, (int)(x + padding), (int)(y + headerHeight + 2),
                (int)(listW - padding * 2f), searchH, fontPath, fontSize * 0.9f);

            var filtered = _lastFilteredTargets;
            var totalItems = filtered.Count;
            var listTop      = y + headerHeight + searchH + 4;
            var listH        = h - headerHeight;
            var visibleRows  = Math.Max(1, (int)(listH / itemHeight));
            VisibleRows = visibleRows;
            var maxScroll    = Math.Max(0, totalItems - visibleRows);

            // Clamp scroll
            if (ScrollOffset < 0)       ScrollOffset = 0;
            if (ScrollOffset > maxScroll) ScrollOffset = maxScroll;

            for (var i = ScrollOffset; i < totalItems; i++)
            {
                var rowY = listTop + (i - ScrollOffset) * itemHeight;
                if (rowY + itemHeight > y + h)
                {
                    break;
                }

                var scored     = filtered[i];
                var isSelected = i == state.SelectedTargetIndex;
                var isProposed = state.Proposals.Any(p => p.Target == scored.Target);

                var rowBg = isSelected ? SelectedBg
                          : isProposed ? ProposedBg
                                       : PanelBgOpaque;
                var rowTextColor = isSelected ? SelectedText : ItemText;

                FillRect(x, rowY, listW, itemHeight, rowBg);

                // Proposed marker "*" in cyan on left
                if (isProposed)
                {
                    var markerW = padding * 2f;
                    DrawText("*".AsSpan(), fontPath,
                        x + 1f, rowY, markerW, itemHeight,
                        fontSize, ProposedMarker, TextAlign.Near, TextAlign.Center);
                }

                // Target name
                var nameX = x + padding * 2f;
                var nameW = listW * 0.60f;
                DrawText(scored.Target.Name.AsSpan(), fontPath,
                    nameX, rowY, nameW, itemHeight,
                    fontSize, rowTextColor, TextAlign.Near, TextAlign.Center);

                // Altitude right-aligned
                var altStr = $"{scored.OptimalAltitude:F0}°";
                var altX   = x + padding * 2f + nameW;
                var altW   = listW - nameW - padding * 3f;
                DrawText(altStr.AsSpan(), fontPath,
                    altX, rowY, altW, itemHeight,
                    fontSize, isSelected ? SelectedText : DimText, TextAlign.Far, TextAlign.Center);
            }

            // Scrollbar thumb
            if (totalItems > visibleRows && maxScroll > 0)
            {
                var sbX    = x + listW;
                FillRect(sbX, listTop, scrollBarWidth, listH, ScrollBarBg);

                var thumbH = Math.Max(20f * dpiScale, listH * visibleRows / (float)totalItems);
                var thumbY = listTop + (listH - thumbH) * ScrollOffset / (float)maxScroll;
                FillRect(sbX + 1f, thumbY, scrollBarWidth - 2f, thumbH, ScrollBarFg);
            }
        }

        // -----------------------------------------------------------------------
        // Details panel
        // -----------------------------------------------------------------------

        private void RenderDetailsPanel(
            PlannerState state,
            string fontPath,
            float x, float y, float w, float h,
            float fontSize, float padding)
        {
            FillRect(x, y, w, h, DetailsBg);

            // Separator line at top
            FillRect(x, y, w, 1f, SeparatorColor);

            var idx = state.SelectedTargetIndex;
            var filtered = _lastFilteredTargets;
            if (idx < 0 || idx >= filtered.Count)
            {
                DrawText("Select a target to see details.".AsSpan(), fontPath,
                    x + padding, y, w - padding * 2f, h,
                    fontSize, DimText, TextAlign.Near, TextAlign.Center);
                return;
            }

            var scored      = filtered[idx];
            var isProposed  = state.Proposals.Any(p => p.Target == scored.Target);
            var statusSuffix = isProposed ? " [Proposed]" : "";
            var lineH       = h / 3f;

            // Line 1: target name (larger font)
            var nameText = scored.Target.Name + statusSuffix;
            DrawText(nameText.AsSpan(), fontPath,
                x + padding, y, w - padding * 2f, lineH,
                fontSize * 1.25f, DetailsNameText, TextAlign.Near, TextAlign.Center);

            // Line 2: coordinates + altitude + window
            var window = FormatWindow(scored, state);
            var line2 = $"RA {scored.Target.RA:F3}h  Dec {scored.Target.Dec:+0.0;-0.0}°" +
                        $"  Alt {scored.OptimalAltitude:F0}°  Window {window}";
            DrawText(line2.AsSpan(), fontPath,
                x + padding, y + lineH, w - padding * 2f, lineH,
                fontSize, DetailsInfoText, TextAlign.Near, TextAlign.Center);

            // Line 3: log-scale rating (0.5-5.0 stars)
            var maxScore = state.TonightsBest.Count > 0 ? state.TonightsBest[0].CombinedScore : 1.0;
            var rating = PlannerActions.ScoreToRating(scored.CombinedScore, maxScore);
            var line3 = $"Rating: {rating:F1}\u2605";
            DrawText(line3.AsSpan(), fontPath,
                x + padding, y + lineH * 2f, w - padding * 2f, lineH,
                fontSize, DetailsInfoText, TextAlign.Near, TextAlign.Center);
        }

        private static string FormatWindow(ScoredTarget scored, PlannerState state)
        {
            var start = scored.OptimalStart.ToOffset(state.SiteTimeZone);
            var end   = (scored.OptimalStart + scored.OptimalDuration).ToOffset(state.SiteTimeZone);
            return $"{start:HH:mm}\u2013{end:HH:mm}";
        }

        // Drawing helpers inherited from VkTabBase: FillRect, DrawText, RenderButton, RegisterClickable, etc.
    }
}
