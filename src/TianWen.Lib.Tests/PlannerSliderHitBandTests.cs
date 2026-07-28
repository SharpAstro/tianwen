using DIR.Lib;
using Shouldly;
using System;
using System.Collections.Immutable;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Tests for <see cref="PlannerSliderInteraction.GetHitBands"/> -- the one place that says where a
    /// handoff divider can be grabbed.
    /// <para>
    /// It exists because there used to be two places. The GUI registered a band bounded to the plot rows and
    /// carried a comment saying why; the TUI re-derived divider positions from the chart's time layout with
    /// no Y bound at all, so a click anywhere in the canvas column-aligned with a divider selected it --
    /// including the weather band above the plot and the legend below. Two derivations of "where was this
    /// drawn", and the geometry drifted in the copy whose author had not hit the bug.
    /// </para>
    /// </summary>
    public class PlannerSliderHitBandTests
    {
        private static readonly DateTimeOffset Dusk = new(2026, 7, 20, 18, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Dawn = new(2026, 7, 21, 6, 0, 0, TimeSpan.Zero);

        /// <summary>A chart rect the size the GUI gives it, at a non-zero origin so translation bugs show.</summary>
        private static readonly RectF32 Chart = new RectF32(120f, 40f, 900f, 500f);

        private static PlannerState State(params DateTimeOffset[] sliders) => new PlannerState
        {
            AstroDark = Dusk,
            AstroTwilight = Dawn,
            HandoffSliders = [.. sliders],
        };

        private static bool Contains(RectF32 r, float x, float y) =>
            x >= r.X && x <= r.X + r.Width && y >= r.Y && y <= r.Y + r.Height;

        /// <summary>The plot rect the chart actually draws into -- the oracle, not a second derivation.</summary>
        private static (float X, float Y, float W, float H) Plot(PlannerState state, RectF32 chart)
        {
            var (_, _, plotX, plotY, plotW, plotH) = AltitudeChartRenderer.GetChartPlotLayout(
                state, (int)chart.X, (int)chart.Y, (int)chart.Width, (int)chart.Height);
            return (plotX, plotY, plotW, plotH);
        }

        [Fact]
        public void ABandCoversOnlyThePlotRowsNotTheWholeChart()
        {
            var state = State(Dusk + TimeSpan.FromHours(6));
            var band = PlannerSliderInteraction.GetHitBands(
                state, Chart, PlannerSliderInteraction.DefaultBandWidth)[0];

            var plot = Plot(state, Chart);
            band.Y.ShouldBe(plot.Y, 0.5f);
            band.Height.ShouldBe(plot.H, 0.5f);

            // The defect, stated as behaviour: the weather band / title strip sits between the chart top and
            // the plot top, and a click there must not land on a divider however well it lines up in X.
            var centreX = band.X + band.Width * 0.5f;
            plot.Y.ShouldBeGreaterThan(Chart.Y, "there is a top margin to click in at all");
            Contains(band, centreX, Chart.Y + 1f).ShouldBeFalse("click in the weather band above the plot");

            // Same below: the axis labels and legend live under the plot.
            (plot.Y + plot.H).ShouldBeLessThan(Chart.Y + Chart.Height);
            Contains(band, centreX, Chart.Y + Chart.Height - 1f).ShouldBeFalse("click on the legend below");

            // And the row it IS meant to catch.
            Contains(band, centreX, plot.Y + plot.H * 0.5f).ShouldBeTrue();
        }

        [Fact]
        public void ABandIsCentredOnTheTimeItsDividerWasDrawnAt()
        {
            // tStart/tEnd bracket the night symmetrically (civil dusk/dawn fall back to astro +/- 1h15),
            // so the midpoint of the dark window is the midpoint of the plot.
            var state = State(Dusk + (Dawn - Dusk) / 2);
            var band = PlannerSliderInteraction.GetHitBands(
                state, Chart, PlannerSliderInteraction.DefaultBandWidth)[0];

            var plot = Plot(state, Chart);
            (band.X + band.Width * 0.5f).ShouldBe(plot.X + plot.W * 0.5f, 1f);
        }

        [Fact]
        public void BandsFollowTheirDividersInOrderAndDoNotOverlapWhenApart()
        {
            var state = State(
                Dusk + TimeSpan.FromHours(3),
                Dusk + TimeSpan.FromHours(6),
                Dusk + TimeSpan.FromHours(9));
            var bands = PlannerSliderInteraction.GetHitBands(
                state, Chart, PlannerSliderInteraction.DefaultBandWidth);

            bands.Count.ShouldBe(3);
            for (var i = 1; i < bands.Count; i++)
            {
                bands[i].X.ShouldBeGreaterThan(bands[i - 1].X + bands[i - 1].Width,
                    "three hours apart is far wider than a band, so they must not overlap");
            }
        }

        [Fact]
        public void TheBandWidthIsTheCallersToScale()
        {
            var state = State(Dusk + TimeSpan.FromHours(6));

            // The GUI passes DefaultBandWidth * DpiScale; the TUI passes at least one cell. Both need the
            // band centred on the divider, so widening it must grow it symmetrically rather than shift it.
            var thin = PlannerSliderInteraction.GetHitBands(state, Chart, 10f)[0];
            var wide = PlannerSliderInteraction.GetHitBands(state, Chart, 40f)[0];

            thin.Width.ShouldBe(10f);
            wide.Width.ShouldBe(40f);
            (wide.X + wide.Width * 0.5f).ShouldBe(thin.X + thin.Width * 0.5f, 0.01f);
        }

        [Fact]
        public void TranslatingTheChartTranslatesTheBands()
        {
            // What the TUI depends on: it draws the chart at the canvas's own origin but registers regions in
            // terminal-pixel space, so it passes an offset chart rect and expects the bands to move with it.
            var state = State(Dusk + TimeSpan.FromHours(4), Dusk + TimeSpan.FromHours(8));
            var moved = new RectF32(Chart.X + 300f, Chart.Y + 70f, Chart.Width, Chart.Height);

            var atOrigin = PlannerSliderInteraction.GetHitBands(state, Chart, 12f);
            var atOffset = PlannerSliderInteraction.GetHitBands(state, moved, 12f);

            atOffset.Count.ShouldBe(atOrigin.Count);
            for (var i = 0; i < atOrigin.Count; i++)
            {
                atOffset[i].X.ShouldBe(atOrigin[i].X + 300f, 1f);
                atOffset[i].Y.ShouldBe(atOrigin[i].Y + 70f, 1f);
                atOffset[i].Width.ShouldBe(atOrigin[i].Width, 0.01f);
                atOffset[i].Height.ShouldBe(atOrigin[i].Height, 1f);
            }
        }

        [Fact]
        public void NoDividersMeansNoBands()
        {
            PlannerSliderInteraction.GetHitBands(State(), Chart, 10f).Count.ShouldBe(0);
        }

        [Fact]
        public void ADegenerateTimeRangeReportsNoBandsRatherThanNaNRegions()
        {
            // Every fraction would be NaN, and a region registered at NaN can never be hit -- a dead handle
            // with nothing in the geometry to explain it. Report none instead.
            // tStart is CivilSet - 15min and tEnd is CivilRise + 15min, so a zero-width range needs civil
            // rise half an hour BEFORE civil set -- nonsense data, which is exactly the point.
            var state = new PlannerState
            {
                AstroDark = Dusk,
                AstroTwilight = Dusk,
                CivilSet = Dusk,
                CivilRise = Dusk - TimeSpan.FromMinutes(30),
                HandoffSliders = [Dusk],
            };

            PlannerSliderInteraction.GetHitBands(state, Chart, 10f).Count.ShouldBe(0);
        }

        [Fact]
        public void ADefaultBandSetIsEmptyRatherThanThrowing()
        {
            // The struct is returned by value, so a default instance is reachable; its ImmutableArray is
            // default too, and Length on that throws.
            default(PlannerSliderInteraction.HitBands).Count.ShouldBe(0);
        }
    }
}
