using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Offline render tests for <see cref="PlannerTab{TSurface}"/> over the CPU
    /// <see cref="RgbaImageRenderer"/> -- no GPU/device needed. These pin the responsive frame layout
    /// (see <c>PlannerTab.BuildFrameLayout</c>): landscape keeps the shipped left-list / bottom-details /
    /// chart-fill dock, portrait (phones, narrow windows) stacks chart / compact details / list
    /// vertically. Regression guard for the portrait bug where the fixed 330-unit side list ate most
    /// of a phone screen and squashed the chart to a sliver.
    /// </summary>
    [Collection("UI")]
    public class PlannerTabLayoutTests
    {
        private static readonly DateTimeOffset NightStart = new(2025, 12, 15, 18, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset NightEnd = new(2025, 12, 16, 6, 0, 0, TimeSpan.Zero);

        /// <summary>A planner state with two selectable targets so the chart, list rows, and details
        /// panel all have real content to paint.</summary>
        private static PlannerState BuildState()
        {
            var state = new PlannerState
            {
                AstroDark = NightStart,
                AstroTwilight = NightEnd,
                MinHeightAboveHorizon = 0,
                SelectedTargetIndex = 0,
            };

            var scoredBuilder = ImmutableDictionary.CreateBuilder<Target, ScoredTarget>();
            var profilesBuilder = ImmutableDictionary.CreateBuilder<Target, List<(DateTimeOffset Time, double Alt)>>();
            var tonightsBuilder = ImmutableArray.CreateBuilder<ScoredTarget>();
            for (var i = 0; i < 2; i++)
            {
                var target = new Target(i * 2.0, 45, $"T{i}", null);
                var peak = NightStart + TimeSpan.FromHours(3 + i * 2);
                var scored = new ScoredTarget(target, (Half)1.0, (Half)1.0,
                    new Dictionary<RaDecEventTime, RaDecEventInfo>(),
                    OptimalStart: NightStart + TimeSpan.FromHours(1 + i), OptimalDuration: TimeSpan.FromHours(1),
                    OptimalAltitude: 60.0);
                scoredBuilder[target] = scored;
                profilesBuilder[target] = [(NightStart, 0), (peak, 80), (NightEnd, 0)];
                tonightsBuilder.Add(scored);
            }

            state.ScoredTargets = scoredBuilder.ToImmutable();
            state.AltitudeProfiles = profilesBuilder.ToImmutable();
            state.TonightsBest = tonightsBuilder.ToImmutable();
            return state;
        }

        private static PlannerTab<RgbaImage> RenderTab(RgbaImageRenderer renderer, float dpiScale = 1f)
        {
            var tab = new PlannerTab<RgbaImage>(renderer);
            // 22:00 is inside the displayed night, so the elapsed-time shade paints too.
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero));
            // A real font: the chart's axis labels rasterize glyphs on the CPU renderer (an empty
            // path throws in OpenTypeFont), same as ObservationScheduleVisualizationTests.
            var fontPath = FontResolver.ResolveSystemFont();
            tab.Render(BuildState(), new RectF32(0, 0, renderer.Width, renderer.Height), dpiScale,
                fontPath, time);
            return tab;
        }

        [Fact]
        public void Landscape_KeepsLeftListBottomDetailsChartFill()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = RenderTab(renderer);

            // The shipped desktop geometry, unchanged: full-height 330-unit list docked left,
            // 120-unit details strip docked bottom-right, chart fills the remainder.
            tab.TargetListRect.X.ShouldBe(0f, 0.5f);
            tab.TargetListRect.Width.ShouldBe(330f, 0.5f);
            tab.TargetListRect.Height.ShouldBe(1000f, 0.5f);
            tab.ChartRect.X.ShouldBe(330f, 0.5f);
            tab.ChartRect.Width.ShouldBe(1270f, 0.5f);
            tab.ChartRect.Height.ShouldBe(880f, 0.5f); // 1000 - 120 details
        }

        [Fact]
        public void Landscape_NarrowWindow_ListWidthCapped_ChartNeverStarves()
        {
            using var renderer = new RgbaImageRenderer(600, 400);
            var tab = RenderTab(renderer);

            // A fixed 330-unit list would leave the chart 270 of 600; the cap holds it to 42%.
            tab.TargetListRect.Width.ShouldBe(600f * 0.42f, 0.5f);
            tab.ChartRect.Width.ShouldBe(600f - 600f * 0.42f, 0.5f);
            tab.ChartRect.Width.ShouldBeGreaterThan(tab.TargetListRect.Width);
        }

        [Fact]
        public void Portrait_StacksChartDetailsList_FullWidthEach()
        {
            // iPhone 12 Pro logical viewport -- the layout that used to squash the chart beside the list.
            using var renderer = new RgbaImageRenderer(390, 844);
            var tab = RenderTab(renderer);

            // Chart on top, full width, at its natural aspect (390 x 0.72 = 280.8 < 45% of 844).
            tab.ChartRect.X.ShouldBe(0f, 0.5f);
            tab.ChartRect.Y.ShouldBe(0f, 0.5f);
            tab.ChartRect.Width.ShouldBe(390f, 0.5f);
            tab.ChartRect.Height.ShouldBe(390f * 0.72f, 0.5f);

            // Compact details strip under the chart, capped at the landscape panel height (max-clamped
            // star: the 1:2 split would give it ~188, the cap frees the surplus to the list).
            var detailsHeight = tab.TargetListRect.Y - (tab.ChartRect.Y + tab.ChartRect.Height);
            detailsHeight.ShouldBe(120f, 0.5f);

            // Target list fills the rest, full width.
            tab.TargetListRect.X.ShouldBe(0f, 0.5f);
            tab.TargetListRect.Width.ShouldBe(390f, 0.5f);
            tab.TargetListRect.Height.ShouldBe(844f - 390f * 0.72f - 120f, 0.5f);
        }

        [Fact]
        public void Portrait_HighDpi_ScalesTheSameDesignLayout()
        {
            // Same 390x844 design space at dpiScale 2 (a 780x1688 physical surface): every rect is
            // exactly the 1x layout scaled -- design units are the single source of truth.
            using var renderer = new RgbaImageRenderer(780, 1688);
            var tab = RenderTab(renderer, dpiScale: 2f);

            tab.ChartRect.Height.ShouldBe(390f * 0.72f * 2f, 1f);
            tab.TargetListRect.Y.ShouldBe((390f * 0.72f + 120f) * 2f, 1f);
            tab.TargetListRect.Width.ShouldBe(780f, 1f);
        }

        [Fact]
        public void Portrait_TightWindow_DetailsStripCollapsesAway()
        {
            // 200x260: after the chart takes min(200 x 0.72, 260 x 0.45) = 117, the details strip's
            // star share lands under its 48-unit collapse threshold -> it drops out entirely and the
            // list sits directly under the chart with all the freed space.
            using var renderer = new RgbaImageRenderer(200, 260);
            var tab = RenderTab(renderer);

            tab.ChartRect.Height.ShouldBe(117f, 0.5f);
            tab.TargetListRect.Y.ShouldBe(tab.ChartRect.Y + tab.ChartRect.Height, 0.5f);
            tab.TargetListRect.Height.ShouldBe(143f, 0.5f);
        }

        // Sentinel the planner never paints: the chart fills its area with the chart background and the
        // list/details panels paint opaque backgrounds, so after a render (in EITHER orientation) almost
        // no sentinel survives. The pre-fix portrait failure mode -- a zero/negative-width region painting
        // nothing -- leaves its whole rect sentinel-coloured, which this catches.
        private static bool IsSentinel(byte r, byte g, byte b) => r == 0xff && g == 0x00 && b == 0xff;

        [Theory]
        [InlineData(390, 844, "portrait")]    // iPhone 12 Pro logical -- the reflowed layout
        [InlineData(1600, 1000, "landscape")] // desktop -- the shipped dock (must stay fully painted)
        public void Planner_PaintsTheFullContent_OnBothOrientations(int width, int height, string label)
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var pixels = renderer.Surface.Pixels;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                pixels[i] = 0xff; pixels[i + 1] = 0x00; pixels[i + 2] = 0xff; pixels[i + 3] = 0xff;
            }

            RenderTab(renderer);

            long sentinel = 0;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                if (IsSentinel(pixels[i], pixels[i + 1], pixels[i + 2]))
                {
                    sentinel++;
                }
            }

            // Emit a PNG beside the test binary so the render can be eyeballed.
            var pngPath = Path.Combine(AppContext.BaseDirectory, $"plannertab-{label}.png");
            File.WriteAllBytes(pngPath, PngWriter.Encode(pixels, renderer.Surface.Width, renderer.Surface.Height));

            var sentinelFraction = (double)sentinel / ((long)width * height);
            sentinelFraction.ShouldBeLessThan(0.02,
                $"{label} ({width}x{height}) left unpainted regions; PNG at {pngPath}");
        }
    }
}
