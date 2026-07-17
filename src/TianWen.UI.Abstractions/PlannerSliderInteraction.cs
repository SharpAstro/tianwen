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
