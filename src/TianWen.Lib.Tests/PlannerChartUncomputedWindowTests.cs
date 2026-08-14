using DIR.Lib;
using Shouldly;
using System;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The chart's time axis must survive being asked for BEFORE a night window exists.
    /// <para>
    /// <see cref="PlannerState.AstroDark"/> / <see cref="PlannerState.AstroTwilight"/> are non-nullable
    /// and so default to <see cref="DateTimeOffset.MinValue"/>. The axis is derived by subtracting an
    /// hour and then fifteen minutes, which UNDERFLOWS from MinValue and throws
    /// <see cref="ArgumentOutOfRangeException"/> out of the <c>DateTime</c> operator (parameter <c>t</c>).
    /// </para>
    /// <para>
    /// This shipped: the web planner threw <c>ArgumentOutOfRange_DateArithmetic</c> on every pointer move
    /// during startup, because the pointer reaches the chart before the first sweep resolves a window.
    /// The guard existed SEVEN times at call sites and the axis expression three times, but neither lived
    /// in the two public layout helpers that do the arithmetic -- so a caller was safe only if its author
    /// happened to know. <c>PlannerSliderInteraction</c>, which every pointer event goes through, did not.
    /// </para>
    /// <para>
    /// <b>The crashing path is RENDER, not the pointer handler.</b> Worth stating, because the stack
    /// named <c>OnPointerMove</c> and that is misleading: <c>PlannerTab.RegisterSliderHitRegions</c>
    /// calls <see cref="PlannerSliderInteraction.GetHitBands"/> on every frame, and the web's
    /// <c>OnPointerMove</c> ends in a <c>RenderFrame()</c>. The two pointer tests below pass with or
    /// without the fix, because both handlers early-return before the arithmetic on a fresh state
    /// (no drag in progress, no sliders to place); they are kept as guards on those early returns, not
    /// as the reproduction. The three that actually go red are the two layout helpers and the hit bands.
    /// </para>
    /// <para>
    /// It is web-only for a scheduling reason rather than a platform one: the desktop renders the same
    /// shared <c>PlannerTab</c>, but not until the user switches to that tab, by which time the sweep
    /// has resolved a window. On the showcase the planner is the landing view.
    /// </para>
    /// <para>
    /// These tests therefore drive the PUBLIC entry points, not <c>TimeAxis</c> directly: the bug was
    /// never that the arithmetic was wrong, it was that the callers were trusted to guard it.
    /// </para>
    /// </summary>
    public class PlannerChartUncomputedWindowTests
    {
        /// <summary>A chart rect at a non-zero origin, as the hosts give it.</summary>
        private static readonly RectF32 Chart = new RectF32(120f, 40f, 900f, 500f);

        /// <summary>Exactly what the planner looks like before the first sweep completes.</summary>
        private static PlannerState Uncomputed() => new PlannerState();

        [Fact]
        public void GetChartTimeLayout_WithNoNightWindow_ReportsAnEmptyRangeInsteadOfThrowing()
        {
            var (tStart, tEnd, _, _) = AltitudeChartRenderer.GetChartTimeLayout(
                Uncomputed(), (int)Chart.X, (int)Chart.Width);

            tEnd.ShouldBe(tStart);
        }

        [Fact]
        public void GetChartPlotLayout_WithNoNightWindow_ReportsAnEmptyRangeInsteadOfThrowing()
        {
            var (tStart, tEnd, _, _, _, _) = AltitudeChartRenderer.GetChartPlotLayout(
                Uncomputed(), (int)Chart.X, (int)Chart.Y, (int)Chart.Width, (int)Chart.Height);

            tEnd.ShouldBe(tStart);
        }

        /// <summary>
        /// A pointer move over the planner during startup. Passes even unfixed -- the handler returns
        /// early with no drag in progress -- so this guards that early return rather than reproducing
        /// the crash. Remove the early return and this is the test that catches it.
        /// </summary>
        [Fact]
        public void PointerMove_BeforeTheFirstSweep_DoesNotThrow()
        {
            var state = Uncomputed();

            Should.NotThrow(() => PlannerSliderInteraction.HandleMouseMove(state, Chart, px: 400f));
        }

        /// <summary>
        /// The same for a press. Click-to-place would ask for the plot layout to convert X to a time,
        /// but is skipped while there are no sliders -- so, like the move above, this guards an early
        /// return rather than reproducing the crash.
        /// </summary>
        [Fact]
        public void PointerDown_BeforeTheFirstSweep_DoesNotThrow()
        {
            var state = Uncomputed();

            Should.NotThrow(() => PlannerSliderInteraction.HandleMouseDown(
                state, hit: null, Chart, px: 400f, py: 200f));
        }

        /// <summary>
        /// A degenerate axis must yield NO grab bands rather than bands at NaN: a region registered at NaN
        /// can never be hit, which reads as a dead handle with no clue why (the reason
        /// <see cref="PlannerSliderInteraction.GetHitBands"/> already tested for a positive range).
        /// </summary>
        [Fact]
        public void HitBands_BeforeTheFirstSweep_AreEmpty()
        {
            var bands = PlannerSliderInteraction.GetHitBands(Uncomputed(), Chart, bandWidth: 8f);

            bands.Count.ShouldBe(0);
        }

        /// <summary>
        /// With a window present the axis must still be the real one -- the guard must not have been
        /// bought by flattening the normal case.
        /// </summary>
        [Fact]
        public void WithANightWindow_TheAxisSpansDuskToDawnPlusMargins()
        {
            var dusk = new DateTimeOffset(2026, 7, 20, 18, 0, 0, TimeSpan.Zero);
            var dawn = new DateTimeOffset(2026, 7, 21, 6, 0, 0, TimeSpan.Zero);
            var state = new PlannerState { AstroDark = dusk, AstroTwilight = dawn };

            var (tStart, tEnd, _, _) = AltitudeChartRenderer.GetChartTimeLayout(
                state, (int)Chart.X, (int)Chart.Width);

            // No civil times set, so the astro window widens by an hour each side, then 15 min of margin.
            tStart.ShouldBe(dusk - TimeSpan.FromHours(1) - TimeSpan.FromMinutes(15));
            tEnd.ShouldBe(dawn + TimeSpan.FromHours(1) + TimeSpan.FromMinutes(15));
        }
    }
}
