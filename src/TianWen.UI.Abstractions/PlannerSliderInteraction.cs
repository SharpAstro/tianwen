using System;
using System.Collections.Immutable;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The handoff-slider (divider) mouse interaction on the planner altitude chart: grab a
    /// handle (or click-to-place the nearest one), drag it along the time axis, release.
    /// Single source of truth shared by every host - the SDL GUI routes through
    /// <see cref="GuiEventHandlerBase"/> and the Blazor/WebGL host calls these directly -
    /// so the drag state machine can never fork per host.
    /// </summary>
    public static class PlannerSliderInteraction
    {
        /// <summary>
        /// Hit-band width for a divider handle, in design units. A divider is drawn as a thin line, so the
        /// band is deliberately wider than the line -- grabbing a 1px target with a mouse is not reasonable.
        /// </summary>
        public const float DefaultBandWidth = 10f;

        /// <summary>
        /// The hit bands for the handoff dividers of a chart drawn into a given rect: one band per divider,
        /// centred where that divider was drawn and spanning ONLY the plot rows.
        /// <para>
        /// Shared by every host that hit-tests the chart, because the alternative -- each host re-deriving
        /// divider positions from the chart layout -- is a second source of truth for where something was
        /// drawn, and it had already drifted. The TUI's copy hit-tested X only, with no plot-Y bound at all,
        /// so a click on the weather band above the plot (or the legend below it) selected a divider; the
        /// GUI's copy bounded it and carried a comment explaining why. One of them was a bug and the shape
        /// of the code is what hid it.
        /// </para>
        /// <para>
        /// A struct with an indexer rather than an array of rects: the hosts want to loop and register, and a
        /// per-frame array for a handful of bands is an allocation on the render path for nothing.
        /// </para>
        /// </summary>
        public readonly struct HitBands(
            ImmutableArray<DateTimeOffset> sliders, DateTimeOffset start, double rangeHours,
            float plotX, float plotY, float plotW, float plotH, float bandWidth)
        {
            /// <summary>
            /// How many bands there are. Zero for a <c>default</c> instance, so an unusable chart layout
            /// reports "nothing to register" rather than throwing on the default <see cref="ImmutableArray{T}"/>.
            /// </summary>
            public int Count => sliders.IsDefaultOrEmpty ? 0 : sliders.Length;

            /// <summary>
            /// The band for divider <paramref name="index"/>, in the same coordinate space as the chart rect
            /// it was built from.
            /// </summary>
            public RectF32 this[int index]
            {
                get
                {
                    var fraction = (sliders[index] - start).TotalHours / rangeHours;
                    var x = plotX + (float)(fraction * plotW);
                    return new RectF32(x - bandWidth * 0.5f, plotY, bandWidth, plotH);
                }
            }
        }

        /// <summary>
        /// Builds the divider hit bands for a chart occupying <paramref name="chartRect"/>.
        /// <para>
        /// <paramref name="bandWidth"/> is in the chart rect's own units: device pixels for a GUI (so scale
        /// it by DPI), terminal pixels for the Sixel canvas -- where it wants to be at least one cell wide,
        /// since a terminal cannot report a click finer than a cell.
        /// </para>
        /// </summary>
        public static HitBands GetHitBands(PlannerState state, RectF32 chartRect, float bandWidth)
        {
            var (tStart, tEnd, plotX, plotY, plotW, plotH) = AltitudeChartRenderer.GetChartPlotLayout(
                state, (int)chartRect.X, (int)chartRect.Y, (int)chartRect.Width, (int)chartRect.Height);
            var rangeHours = (tEnd - tStart).TotalHours;

            // A degenerate time range would make every fraction NaN. Report no bands instead: a region
            // registered at NaN can never be hit, so it would look like a dead handle with no clue why.
            return rangeHours > 0
                ? new HitBands(state.HandoffSliders, tStart, rangeHours, plotX, plotY, plotW, plotH, bandWidth)
                : default;
        }

        /// <summary>
        /// Handles a primary-button press after hit testing. A press directly on a slider
        /// handle (<see cref="HitResult.SliderHit"/>) selects it and starts a drag; a press on
        /// empty chart plot area moves the nearest slider there (click-to-place) and starts a
        /// drag so the same press can refine it. Any other press deselects a selected slider.
        /// Returns true when a drag started (the press is consumed).
        /// </summary>
        /// <param name="allowClickToPlace">
        /// False when the planner chart is not the active surface (e.g. another GUI tab is
        /// shown), so a stray press cannot move a slider through a stale chart rect.
        /// </param>
        public static bool HandleMouseDown(
            PlannerState state, HitResult? hit, RectF32 chartRect, float px, float py,
            bool allowClickToPlace = true)
        {
            // Drag start + selection (clicked directly on a slider handle)
            if (hit is HitResult.SliderHit { SliderIndex: var sliderIdx })
            {
                state.DraggingSliderIndex = sliderIdx;
                PlannerActions.SelectSlider(state, sliderIdx);
                return true;
            }

            // Click-to-place: a click anywhere in the planner chart (but not directly on a
            // slider handle) moves the nearest handoff slider to that time and begins a drag,
            // so the same press can refine it. Selecting it also makes Left/Right step the
            // slider (which trumps date-switching).
            if (allowClickToPlace && hit is null && state.HandoffSliders.Length > 0)
            {
                var (tStart, tEnd, plotX, plotY, plotW, plotH) = AltitudeChartRenderer.GetChartPlotLayout(
                    state, (int)chartRect.X, (int)chartRect.Y, (int)chartRect.Width, (int)chartRect.Height);
                // Only inside the PLOT area -- a click on the weather band / icons above the plot
                // (or the legend / axis below it) must NOT move a handoff divider.
                if (px >= plotX && px <= plotX + plotW && py >= plotY && py <= plotY + plotH)
                {
                    var clickedTime = AltitudeChartRenderer.XToTime(px, tStart, tEnd, plotX, plotW);
                    if (PlannerActions.PlaceNearestSlider(state, clickedTime) is var moved && moved >= 0)
                    {
                        state.DraggingSliderIndex = moved;
                        return true;
                    }
                }
            }

            // Clicking outside a slider and outside the chart -> deselect
            if (state.SelectedSliderIndex >= 0)
            {
                PlannerActions.SelectSlider(state, -1);
            }

            return false;
        }

        /// <summary>
        /// Handles a mouse move while a slider drag may be active. Returns true when a drag is
        /// active and the move was consumed (the caller must NOT forward the move to the tab);
        /// false when no drag is active.
        /// </summary>
        public static bool HandleMouseMove(PlannerState state, RectF32 chartRect, float px)
        {
            var idx = state.DraggingSliderIndex;
            if (idx < 0)
            {
                return false;
            }

            if (idx >= state.HandoffSliders.Length)
            {
                // Sliders were rebuilt mid-drag (recompute) - abandon the drag but still own the move.
                state.DraggingSliderIndex = -1;
                return true;
            }

            var (tStart, tEnd, plotX, plotW) = AltitudeChartRenderer.GetChartTimeLayout(
                state, (int)chartRect.X, (int)chartRect.Width);

            var newTime = AltitudeChartRenderer.XToTime(px, tStart, tEnd, plotX, plotW);
            PlannerActions.MoveSlider(state, idx, newTime);
            return true;
        }

        /// <summary>Ends an active slider drag. Returns true when a drag was in progress.</summary>
        public static bool HandleMouseUp(PlannerState state)
        {
            if (state.DraggingSliderIndex >= 0)
            {
                state.DraggingSliderIndex = -1;
                return true;
            }

            return false;
        }
    }
}
