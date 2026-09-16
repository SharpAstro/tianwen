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

        /// <summary>The chart's own region: a press here that misses a handle places the nearest one.</summary>
        public const string ChartRegion = "PlannerChart";

        /// <summary>A handoff divider's grab handle, registered OVER the chart so it wins the press.</summary>
        public const string DividerRegion = "PlannerDivider";

        /// <summary>
        /// The drag a press on divider <paramref name="index"/> starts: every move re-times that divider,
        /// the release ends it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A <see cref="DragCapture"/> rather than a flag the host's move and release branches consult.
        /// The gesture is armed BY THE REGION THAT WAS PRESSED, so "draw == hit" extends to "draw ==
        /// drag": there is no second statement of where the handle was, and no way for a move to arrive
        /// while the host believes no drag is running. What this replaced was a press branch, a move
        /// branch and a release branch in every host, agreeing by convention.
        /// </para>
        /// <para>
        /// <see cref="PlannerState.DraggingSliderIndex"/> is still set, because it is not the drag's
        /// state machine any more -- it is what the chart HIGHLIGHTS
        /// (<c>AltitudeChartRenderer</c>) and what suppresses the mouse follower while a divider is being
        /// moved. The capture owns its lifetime, so it cannot be left behind by a release nobody routed.
        /// </para>
        /// </remarks>
        public static DragCapture BeginDrag(PlannerState state, int index, RectF32 chartRect)
        {
            state.DraggingSliderIndex = index;

            return new DragCapture(
                move =>
                {
                    // Rebuilt mid-drag by a recompute: abandon rather than re-time a divider that is now
                    // somebody else. The capture still owns the gesture until its release.
                    if (state.DraggingSliderIndex is var idx && idx >= 0 && idx < state.HandoffSliders.Length)
                    {
                        var (tStart, tEnd, plotX, plotW) = AltitudeChartRenderer.GetChartTimeLayout(
                            state, (int)chartRect.X, (int)chartRect.Width);
                        PlannerActions.MoveSlider(state, idx,
                            AltitudeChartRenderer.XToTime(move.X, tStart, tEnd, plotX, plotW));
                        state.NeedsRedraw = true;
                    }
                },
                _ =>
                {
                    state.DraggingSliderIndex = -1;
                    state.NeedsRedraw = true;
                });
        }

        /// <summary>
        /// A press anywhere in the chart's PLOT that was not on a handle: move the nearest divider there
        /// and keep dragging, so the same press can refine it. Null when there is nothing to move.
        /// </summary>
        /// <remarks>
        /// Registered as a region UNDER the handles (the handles register later and so win), which is what
        /// lets both gestures arm the same way. It used to be the <c>hit is null</c> arm of a press
        /// handler, i.e. click-to-place was defined by what it was NOT -- and "not on a handle" and "not
        /// on anything at all" are different questions that arm shared the answer to.
        /// </remarks>
        public static DragCapture? BeginPlaceNearest(PlannerState state, RectF32 chartRect, float px, float py)
        {
            if (state.HandoffSliders.Length == 0)
            {
                return null;
            }

            var (tStart, tEnd, plotX, plotY, plotW, plotH) = AltitudeChartRenderer.GetChartPlotLayout(
                state, (int)chartRect.X, (int)chartRect.Y, (int)chartRect.Width, (int)chartRect.Height);

            // Only inside the PLOT area -- a press on the weather band above it, or the legend below,
            // must not move a divider. The region is the whole chart, so this is the narrowing.
            if (px < plotX || px > plotX + plotW || py < plotY || py > plotY + plotH)
            {
                return null;
            }

            var moved = PlannerActions.PlaceNearestSlider(
                state, AltitudeChartRenderer.XToTime(px, tStart, tEnd, plotX, plotW));
            if (moved < 0)
            {
                return null;
            }

            state.NeedsRedraw = true;
            return BeginDrag(state, moved, chartRect);
        }

        /// <summary>
        /// A press that reached no region at all: deselect. Returns true when something changed.
        /// </summary>
        /// <remarks>
        /// All that is left of what used to be one press handler holding three cases. The other two are
        /// regions now, and this one is the only one that is genuinely about the ABSENCE of a target --
        /// which is why it belongs on the host's unhandled path and the other two do not.
        /// </remarks>
        public static bool HandlePressWithNoTarget(PlannerState state)
        {
            if (state.SelectedSliderIndex < 0)
            {
                return false;
            }

            PlannerActions.SelectSlider(state, -1);
            return true;
        }
    }
}
