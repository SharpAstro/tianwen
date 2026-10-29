using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
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
        /// panel all have real content to paint. <paramref name="firstIndex"/> optionally gives the first
        /// target a catalog index (so the details name line becomes a Wikipedia link).</summary>
        private static PlannerState BuildState(CatalogIndex? firstIndex = null, ICelestialObjectDB? db = null)
        {
            var state = new PlannerState
            {
                ObjectDb = db,
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
                var target = new Target(i * 2.0, 45, $"T{i}", i == 0 ? firstIndex : null);
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
            // DPI is the widget-owned property now (host-set), not a Render argument.
            // A real font: the chart's axis labels rasterize glyphs on the CPU renderer (an empty
            // path throws in OpenTypeFont), same as ObservationScheduleVisualizationTests. DPI + font
            // are widget-owned properties now (host-set), not Render arguments.
            var tab = new PlannerTab<RgbaImage>(renderer) { DpiScale = dpiScale, FontPath = FontResolver.ResolveSystemFont() };
            // 22:00 is inside the displayed night, so the elapsed-time shade paints too.
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero));
            tab.Render(BuildState(), new RectF32(0, 0, renderer.Width, renderer.Height), time);
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

        [Theory]
        [InlineData(390, 844, 1f)]
        [InlineData(780, 1688, 2f)] // the same design space at 2x
        public void Portrait_DetailsLines_EachFitTheirRect_WindowDroppedFirst(int width, int height, float dpiScale)
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var tab = RenderTab(renderer, dpiScale);

            tab.DetailsRect.Width.ShouldBeGreaterThan(0f);
            tab.PaintedDetailLines.ShouldNotBeEmpty();

            var fontPath = FontResolver.ResolveSystemFont();
            var fontSize = GuiTheme.Metrics.BaseFontSize * dpiScale;
            foreach (var line in tab.PaintedDetailLines.Skip(1))
            {
                renderer.MeasureText(line.AsSpan(), fontPath, fontSize).Width
                    .ShouldBeLessThanOrEqualTo(tab.DetailsRect.Width - 2f * PlannerTab<RgbaImage>.BasePadding * dpiScale, line);
            }

            var coords = tab.PaintedDetailLines.Single(l => l.StartsWith("RA "));
            coords.ShouldNotContain("Window", Case.Sensitive, "the window is the first field to go");
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

        // A phone's chart: every twilight zone, the weather strip with its seeing row, two pinned-less
        // targets. The nautical zone is 15 minutes wide so "Civil" and "Astro" (the same label row) sit
        // closer together than their words are wide, which is what piled the words up at the left edge.
        private static PlannerState BuildChartState()
        {
            var state = BuildState();
            state.CivilSet = NightStart - TimeSpan.FromMinutes(45);
            state.NauticalSet = NightStart - TimeSpan.FromMinutes(30);
            state.NauticalRise = NightEnd + TimeSpan.FromMinutes(30);
            state.CivilRise = NightEnd + TimeSpan.FromMinutes(45);
            state.MinHeightAboveHorizon = 20;
            state.WeatherForecast = [.. Enumerable.Range(-1, 14).Select(h => new HourlyWeatherForecast(
                NightStart.AddHours(h), CloudCover: 20, Precipitation: 0, Temperature: 12, Humidity: 85, DewPoint: 9,
                WindSpeed: 3, WindGust: 5, WindDirection: 270, Visibility: 20000, WeatherCode: 1,
                WindSpeed250hPa: 20))];
            return state;
        }

        private static (PlannerTab<RgbaImage> Tab, TextCapturingRenderer Renderer) RenderChartTab(
            int width, int height, float dpiScale)
        {
            var renderer = new TextCapturingRenderer((uint)width, (uint)height);
            var tab = new PlannerTab<RgbaImage>(renderer) { DpiScale = dpiScale, FontPath = FontResolver.ResolveSystemFont() };
            tab.Render(BuildChartState(), new RectF32(0, 0, renderer.Width, renderer.Height),
                new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero)));
            return (tab, renderer);
        }

        [Theory]
        [InlineData(780, 1688, 2f)]  // 390x844 design space at 2x
        [InlineData(1170, 2532, 3f)] // and at 3x
        public void Portrait_ChartText_IsNeverUnderTheMinimumPhysicalSize(int width, int height, float dpiScale)
        {
            var (tab, renderer) = RenderChartTab(width, height, dpiScale);
            using var _ = renderer;

            var chart = tab.ChartRect;
            var inChart = renderer.Runs
                .Where(r => r.Rect.UpperLeft.X >= (int)chart.X && r.Rect.LowerRight.X <= (int)(chart.X + chart.Width)
                         && r.Rect.UpperLeft.Y >= (int)chart.Y && r.Rect.LowerRight.Y <= (int)(chart.Y + chart.Height))
                .ToArray();

            // The strip, the title, the legend and the limit label are all in there, or this proves nothing.
            inChart.ShouldContain(r => r.Text == "RH");
            inChart.ShouldContain(r => r.Text.StartsWith("Observation Schedule", StringComparison.Ordinal));
            inChart.ShouldContain(r => r.Text == "20°");

            var floor = AltitudeChartRenderer.MinFontDesign * dpiScale;
            foreach (var run in inChart)
            {
                run.FontSize.ShouldBeGreaterThanOrEqualTo(floor - 0.01f,
                    $"'{run.Text}' is drawn at {run.FontSize} px, under the {floor} px a {dpiScale}x phone needs to read it");
            }
        }

        [Theory]
        [InlineData(390, 844, 1f)]
        [InlineData(780, 1688, 2f)]
        [InlineData(1170, 2532, 3f)]
        public void Portrait_TwilightLabels_DoNotOverlapOneAnother(int width, int height, float dpiScale)
        {
            var (_, renderer) = RenderChartTab(width, height, dpiScale);
            using var _ = renderer;

            var labels = renderer.Runs
                .Where(r => r.Text is "Civil" or "Naut." or "Astro")
                .Select(r =>
                {
                    var ink = renderer.MeasureText(r.Text.AsSpan(), r.Font, r.FontSize).Width;
                    var centre = (r.Rect.UpperLeft.X + r.Rect.LowerRight.X) / 2f;
                    return (r.Text, Y: r.Rect.UpperLeft.Y, Left: centre - ink / 2f, Right: centre + ink / 2f);
                })
                .ToArray();

            labels.Length.ShouldBeGreaterThan(1, "the twilight labels must draw, or this proves nothing");
            for (var i = 0; i < labels.Length; i++)
            {
                for (var j = i + 1; j < labels.Length; j++)
                {
                    if (labels[i].Y != labels[j].Y)
                    {
                        continue; // different rows: the rows are a text height apart
                    }
                    var overlap = labels[i].Left < labels[j].Right && labels[j].Left < labels[i].Right;
                    overlap.ShouldBeFalse($"'{labels[i].Text}' and '{labels[j].Text}' share a row and their words overlap");
                }
            }
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

        /// <summary>
        /// Regression for the sky-map "View in Planner" browser crash: the chart must not throw when
        /// <see cref="PlannerState.PinnedCount"/> transiently exceeds <see cref="PlannerState.HandoffSliders"/>
        /// length. PinnedCount is recomputed on EVERY render (GetFilteredTargets), but HandoffSliders only in
        /// RecomputeHandoffSliders -- so a proposal that becomes resolvable between the two (CommitSuggestion
        /// scoring a previously-unmatched restored pin) grows PinnedCount by one with the sliders still short.
        /// The pinned-window draw used to index HandoffSliders[i-1] unguarded and threw IndexOutOfRangeException.
        /// </summary>
        [Fact]
        public void Chart_PinnedCountExceedsSliders_DoesNotThrow()
        {
            var state = new PlannerState
            {
                AstroDark = NightStart,
                AstroTwilight = NightEnd,
                MinHeightAboveHorizon = 0,
            };

            // Two resolvable pinned proposals -> GetFilteredTargets computes PinnedCount = 2, but sliders are
            // left empty (the desync). A consistent state would have HandoffSliders length 1.
            var scoredBuilder = ImmutableDictionary.CreateBuilder<Target, ScoredTarget>();
            var profilesBuilder = ImmutableDictionary.CreateBuilder<Target, List<(DateTimeOffset Time, double Alt)>>();
            var proposalsBuilder = ImmutableArray.CreateBuilder<ProposedObservation>();
            for (var i = 0; i < 2; i++)
            {
                var target = new Target(i * 2.0, 45, $"Pin{i}", null);
                var peak = NightStart + TimeSpan.FromHours(3 + i * 2);
                scoredBuilder[target] = new ScoredTarget(target, (Half)1.0, (Half)1.0,
                    new Dictionary<RaDecEventTime, RaDecEventInfo>(),
                    OptimalStart: NightStart + TimeSpan.FromHours(1 + i), OptimalDuration: TimeSpan.FromHours(1),
                    OptimalAltitude: 60.0);
                profilesBuilder[target] = [(NightStart, 0), (peak, 80), (NightEnd, 0)];
                proposalsBuilder.Add(new ProposedObservation(target));
            }
            state.ScoredTargets = scoredBuilder.ToImmutable();
            state.AltitudeProfiles = profilesBuilder.ToImmutable();
            state.Proposals = proposalsBuilder.ToImmutable();
            state.HandoffSliders = []; // deliberately short: PinnedCount resolves to 2, sliders stay 0

            using var renderer = new RgbaImageRenderer(800, 600);
            var fontPath = FontResolver.ResolveSystemFont();

            Should.NotThrow(() => AltitudeChartRenderer.Render(renderer, state, fontPath, 0, 0, 800, 600));

            // Prove the desync was actually exercised (PinnedCount grew past the sliders while the pinned-window
            // loop ran), so this isn't a vacuous pass where the missing slider was never reached.
            state.PinnedCount.ShouldBe(2);
            state.HandoffSliders.Length.ShouldBe(0);
        }

        /// <summary>
        /// Regression for "arrow+enter on the search box works, mouse+click doesn't": the autocomplete
        /// dropdown row is clickable but had NO OnClick, so only the keyboard could commit a suggestion.
        /// The row now dispatches to <see cref="DIR.Lib.SearchInteraction.CommitAt"/> on
        /// <see cref="PlannerState.Search"/> (the same commit path the keyboard Enter-on-highlight uses),
        /// so a click commits identically. Asserts a click on the second dropdown row reaches the commit
        /// (box reset + focus released); the index->suggestion binding is unit-pinned in DIR.Lib's
        /// SearchInteractionTests.

        /// <summary>
        /// A handle beats the chart under it, because the handles are registered AFTER it.
        /// </summary>
        /// <remarks>
        /// The ordering is load-bearing and silent when wrong. Registered the other way round, the chart
        /// wins every press, so every handle is unreachable and every grab becomes a click-to-place --
        /// which still moves a divider to roughly where you pressed, so it LOOKS like the feature working.
        /// Stated here against the registered regions rather than against the picture, because the order
        /// is the only thing that distinguishes the two behaviours.
        /// </remarks>
        [Fact]
        public void ADividerHandleIsRegisteredOverTheChartSoAPressGrabsItRatherThanPlacing()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = new PlannerTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
            var state = BuildState();
            state.HandoffSliders = [NightStart + TimeSpan.FromHours(4)];

            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero));
            tab.Render(state, new RectF32(0, 0, 1600, 1000), time);

            var regions = tab.GetRegisteredRegions().ToArray();
            var chartAt = Array.FindIndex(regions,
                r => r.Result is HitResult.ButtonHit { Action: PlannerSliderInteraction.ChartRegion });
            var handleAt = Array.FindIndex(regions,
                r => r.Result is HitResult.ButtonHit { Action: PlannerSliderInteraction.DividerRegion });

            chartAt.ShouldBeGreaterThanOrEqualTo(0, "the chart registers a click-to-place surface");
            handleAt.ShouldBeGreaterThan(chartAt, "and each handle registers after it, so it wins the press");

            // Both carry a press handler, which is what makes either able to arm its own drag.
            regions[chartAt].OnPress.ShouldNotBeNull();
            regions[handleAt].OnPress.ShouldNotBeNull();
        }

        /// </summary>
        [Fact]
        public void SuggestionDropdown_MouseClick_CommitsTheClickedSuggestion()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = new PlannerTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
            var state = BuildState();
            // The dropdown only renders when the search input is active with suggestions present. A null
            // transform makes the commit skip DB resolution and just reset the box (the observable effect).
            new TextInputFocus().Focus(state.SearchInput, "M3");
            var deactivated = 0;
            state.Search = new PlannerSearchInteraction(
                state, db: null!, createTransform: () => null,
                autoComplete: () => ["M31", "M32"],
                ensureVisible: null, deactivate: () => deactivated++, requestRedraw: () => { });
            state.SearchInput.OnTextChanged!("M3"); // populate the dropdown (2 rows) the way a keystroke would
            state.Search.Results.Length.ShouldBe(2);

            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero));
            tab.Render(state, new RectF32(0, 0, 1600, 1000), time);

            // The dropdown is painted last, so its rows are the topmost regions at their pixels -- a routed
            // press on the second row's centre reaches that row (the router walks topmost-first), invoking
            // search.CommitAt(1).
            var region = tab.GetRegisteredRegions()
                .First(r => r.Result is HitResult.ListItemHit { ListId: "Suggestion", Index: 1 });
            var hit = tab.HitTest(region.X + region.Width / 2f, region.Y + region.Height / 2f);
            UiRouting.RoutePress(tab, region.X + region.Width / 2f, region.Y + region.Height / 2f);

            hit.ShouldBeOfType<HitResult.ListItemHit>().Index.ShouldBe(1);
            // CommitAt(1) reached the commit path: the box reset (text + dropdown cleared) and focus released.
            deactivated.ShouldBe(1);
            state.SearchInput.Text.ShouldBe("");
            state.Search.Results.ShouldBeEmpty();
        }

        /// <summary>
        /// The suggestion dropdown's highlight is the layout tree's own <c>.BgFocus</c>, resolved against
        /// the <c>ListCursor</c> the tab opens from the search interaction's <c>SelectedIndex</c> -- so
        /// this asserts on the PICTURE, and on the one thing no hit-tracker assertion can see.
        /// <para>
        /// A <c>.BgFocus</c> whose cursor is never opened paints nothing at all: the rows still register,
        /// still dispatch, still commit, and every other test in this file stays green while the highlight
        /// has silently gone. Asserted as a SWAP between two renders rather than against a colour literal,
        /// so it pins that the fill follows the cursor without re-stating the palette (or the blend) here.
        /// </para>
        /// </summary>
        [Fact]
        public void SuggestionDropdown_HighlightFill_FollowsTheSelectedIndex()
        {
            var (first0, second0) = RenderDropdownRowPixels(selectedIndex: 0);
            var (first1, second1) = RenderDropdownRowPixels(selectedIndex: 1);

            // One row is lit and the other is not, whichever is selected...
            first0.ShouldNotBe(second0);
            first1.ShouldNotBe(second1);

            // ...and moving the cursor exchanges the two fills exactly.
            first1.ShouldBe(second0);
            second1.ShouldBe(first0);
        }

        /// <summary>
        /// Renders the planner with an active search whose dropdown highlight sits on
        /// <paramref name="selectedIndex"/>, and reads back the fill of both suggestion rows from the rects
        /// the rows themselves registered. The sample x is two pixels into the row, inside its leading pad
        /// cell, so the pixel is the row's fill and never a label glyph.
        /// </summary>
        private static (RGBAColor32 First, RGBAColor32 Second) RenderDropdownRowPixels(int selectedIndex)
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = new PlannerTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
            var state = BuildState();
            new TextInputFocus().Focus(state.SearchInput, "M3");
            state.Search = new PlannerSearchInteraction(
                state, db: null!, createTransform: () => null,
                autoComplete: () => ["M31", "M32"],
                ensureVisible: null, deactivate: () => { }, requestRedraw: () => { });
            state.SearchInput.OnTextChanged!("M3");
            state.Search.SelectedIndex = selectedIndex;

            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero));
            tab.Render(state, new RectF32(0, 0, 1600, 1000), time);

            var rows = tab.GetRegisteredRegions()
                .Where(r => r.Result is HitResult.ListItemHit { ListId: "Suggestion" })
                .OrderBy(r => r.Y)
                .ToArray();
            rows.Length.ShouldBe(2);

            return (PixelAt(renderer, rows[0].X + 2f, rows[0].Y + rows[0].Height / 2f),
                    PixelAt(renderer, rows[1].X + 2f, rows[1].Y + rows[1].Height / 2f));
        }

        private static RGBAColor32 PixelAt(RgbaImageRenderer renderer, float x, float y)
        {
            var surface = renderer.Surface;
            var i = ((int)y * (int)surface.Width + (int)x) * 4;
            return new RGBAColor32(surface.Pixels[i], surface.Pixels[i + 1], surface.Pixels[i + 2], surface.Pixels[i + 3]);
        }

        /// <summary>
        /// The details name line for a catalogued target with a VERIFIED article is a Wikipedia link: it
        /// registers a <see cref="HitResult.LinkHit"/> carrying that article's URL
        /// (<see cref="ICelestialObjectDB.TryGetArticle"/>). The host decides what a link does (the
        /// SDL/Vulkan chrome maps LinkHit -> open the OS browser + a pointer cursor on hover; the web
        /// renders a real &lt;a&gt;), so this pins only the tab's contract -- that the region exists with
        /// the right URL, and does not exist for an object the bake verified no article for.
        /// </summary>
        [Fact]
        public async Task DetailsName_ForVerifiedArticle_RegistersWikipediaLinkHit()
        {
            var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
            CatalogUtils.TryGetCleanedUpCatalogName("M31", out var m31).ShouldBeTrue();

            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = new PlannerTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero));
            tab.Render(BuildState(m31, db), new RectF32(0, 0, 1600, 1000), time);

            // The strip's picture credit is a link too (to the Commons file page); this is the NAME's.
            var region = tab.GetRegisteredRegions()
                .First(r => r.Result is HitResult.LinkHit { Url: var url } && url.StartsWith("https://en.wikipedia.org/", StringComparison.Ordinal));
            var hit = tab.HitTest(region.X + region.Width / 2f, region.Y + region.Height / 2f);

            // The verified article, not a page named after the designation (which was M31).
            hit.ShouldBeOfType<HitResult.LinkHit>().Url.ShouldBe("https://en.wikipedia.org/wiki/Andromeda_Galaxy");
        }

        [Fact]
        public async Task DetailsName_ForUnverifiedObject_RegistersNoLink()
        {
            var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

            // IC 1000 used to link https://en.wikipedia.org/wiki/IC_1000 on its designation alone; the bake
            // verified no article for it, so there is nothing to link.
            db.TryGetArticle(CatalogIndex.IC1000, out _).ShouldBeFalse("the test needs an object the table does not cover");

            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = new PlannerTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero));
            tab.Render(BuildState(CatalogIndex.IC1000, db), new RectF32(0, 0, 1600, 1000), time);

            tab.GetRegisteredRegions().ShouldNotContain(r => r.Result is HitResult.LinkHit);
        }

        /// <summary>
        /// <b>The weather band has its hover on every host.</b> Reported 2026-09-28: on the web, the RH and
        /// seeing row had none, because the detail box was drawn by the desktop's planner tab alone. This is
        /// the tab the web runs, the base <see cref="PlannerTab{TSurface}"/>, with a pointer on the band: it
        /// names the hour under the pointer and draws the box under the band, where the frame without the
        /// pointer has none.
        /// </summary>
        [Fact]
        public void APointerOnTheWeatherBandShowsThatHoursDetailOnTheBaseTab()
        {
            var state = BuildState();
            var hours = Enumerable.Range(-1, 14).Select(h => new HourlyWeatherForecast(NightStart.AddHours(h),
                CloudCover: 20, Precipitation: 0, Temperature: 12, Humidity: 85, DewPoint: 9,
                WindSpeed: 3, WindGust: 5, WindDirection: 270, Visibility: 20000, WeatherCode: 1)).ToList();
            state.WeatherForecast = hours;

            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = new PlannerTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero));
            var content = new RectF32(0, 0, renderer.Width, renderer.Height);
            tab.Render(state, content, time);
            var without = renderer.Surface.Pixels.ToArray();

            var chart = tab.ChartRect;
            var (cx, cy, cw, ch) = ((int)chart.X, (int)chart.Y, (int)chart.Width, (int)chart.Height);
            var (bandX, bandY, bandW, bandH) = AltitudeChartRenderer.GetWeatherBandLayout(state, cx, cy, cw, ch).ShouldNotBeNull();
            var (tStart, tEnd, plotX, _, plotW, _) = AltitudeChartRenderer.GetChartPlotLayout(state, cx, cy, cw, ch);
            var midnight = NightStart.AddHours(6);
            var pointer = ((float)(plotX + ((midnight - tStart).TotalHours / (tEnd - tStart).TotalHours * plotW)), bandY + (bandH / 2f));

            // The hour it shows is the one under the pointer.
            using (var probe = new RgbaImageRenderer(1600, 1000))
            {
                AltitudeChartRenderer.DrawWeatherTooltip(probe, state, FontResolver.ResolveSystemFont(), pointer, cx, cy, cw, ch)
                    .ShouldNotBeNull().Time.ShouldBe(midnight);
            }

            tab.Render(state, content, time, pointer);
            var with = renderer.Surface.Pixels;

            // The box: under the band, around the pointer. Count what changed there.
            var changed = 0;
            for (var y = bandY + bandH + 8; y < bandY + bandH + 60; y++)
            {
                for (var x = (int)pointer.Item1 - 40; x < (int)pointer.Item1 + 40; x++)
                {
                    var i = ((y * renderer.Surface.Width) + x) * 4;
                    if (with[i] != without[i] || with[i + 1] != without[i + 1] || with[i + 2] != without[i + 2])
                    {
                        changed++;
                    }
                }
            }
            changed.ShouldBeGreaterThan(80 * 52 / 2, "the detail box covers the chart under the band");
        }

        /// <summary>
        /// <b>The planner's details panel offers Show in atlas.</b> The atlas panel had View in Planner and
        /// the planner had no way back (reported 2026-09-28). Pressed through the region the panel
        /// registered, it posts the selected target, which every host selects in the atlas the same way.
        /// </summary>
        [Fact]
        public void TheDetailsPanelsShowInAtlasPostsTheSelectedTarget()
        {
            var state = BuildState();
            var bus = new SignalBus();
            ViewInSkyMapSignal? posted = null;
            bus.Subscribe<ViewInSkyMapSignal>(sig => posted = sig);

            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = new PlannerTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont(), Bus = bus };
            tab.Render(state, new RectF32(0, 0, renderer.Width, renderer.Height),
                new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero)));

            var button = tab.GetRegisteredRegions()
                .Single(r => r.Result is HitResult.ButtonHit { Action: PlannerTab<RgbaImage>.ShowInAtlasAction });
            button.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
            bus.ProcessPending(new BackgroundTaskTracker());

            var selected = PlannerActions.GetFilteredTargets(state)[state.SelectedTargetIndex].Target;
            var sig = posted.ShouldNotBeNull("the press posts the signal");
            sig.Name.ShouldBe(selected.Name);
            sig.RA.ShouldBe(selected.RA);
            sig.Dec.ShouldBe(selected.Dec);
            sig.Index.ShouldBe(selected.CatalogIndex);
        }

        /// <summary>Records where the planner asks for a picture, and still draws everything else.</summary>
        private sealed class PictureCapturingPlanner(RgbaImageRenderer renderer) : PlannerTab<RgbaImage>(renderer)
        {
            public List<(ObjectArticleImage Image, RectF32 Rect)> Pictures { get; } = [];

            protected override void DrawObjectPicture(in ObjectArticleImage image, RectF32 rect)
                => Pictures.Add((image, rect));
        }

        /// <summary>
        /// <b>A realistic credit stays inside the picture section.</b> The strip's section is narrow (the slot's
        /// aspect ratio at the strip's height), and a credit like an artist's full name and a licence is far wider
        /// than it. The credit's run trims its tail to the rect it is arranged in (<c>TextTrim.End</c>, the
        /// engine's default, which the painter honours), so it ends inside the section with an ellipsis rather
        /// than running under the text column; the whole credit is in the large view.
        /// </summary>
        [Fact]
        public async Task ALongCreditIsTrimmedToThePictureSectionNotPaintedPastIt()
        {
            var image = new ObjectArticleImage("Example.jpg", "2c", "CC BY-SA 4.0",
                "Artist Name Up To Forty-Eight Characters Long", "Observatory", AttributionRequired: true, Width: 4000, Height: 3000);
            ObjectInfoPanel.CreditLine(in image).ShouldContain("CC BY-SA 4.0");
            var db = Substitute.For<ICelestialObjectDB>();
            db.TryGetArticle(Arg.Any<CatalogIndex>(), out Arg.Any<ObjectArticle>())
                .Returns(call =>
                {
                    call[1] = new ObjectArticle(1u, "Example", image);
                    return true;
                });

            using var renderer = new RgbaImageRenderer(1600, 1000);
            var (tab, _, _, _) = await PlannerWithPictureAsync(renderer, db: db);

            var credit = tab.GetRegisteredRegions()
                .Single(r => r.Result is HitResult.LinkHit { Url: var url } && url == image.FilePageUrl);
            var details = tab.DetailsRect;
            credit.X.ShouldBeGreaterThanOrEqualTo(tab.PaintedDetailsTextRect.X + tab.PaintedDetailsTextRect.Width);
            (credit.X + credit.Width).ShouldBeLessThanOrEqualTo(details.X + details.Width + 0.5f);
            credit.Y.ShouldBeGreaterThanOrEqualTo(details.Y);
            (credit.Y + credit.Height).ShouldBeLessThanOrEqualTo(details.Y + details.Height + 0.5f);

            // The full text really does not fit there, so the trim is doing the work.
            renderer.MeasureText(ObjectInfoPanel.CreditLine(in image).AsSpan(), FontResolver.ResolveSystemFont(),
                    GuiTheme.Metrics.BaseFontSize * 0.8f)
                .Width.ShouldBeGreaterThan(credit.Width);
        }

        /// <summary>
        /// With the large view open the strip's lines are plain raster text under the canvas scrim: a host that renders
        /// selectable text as DOM spans (the web) would otherwise leave them at full brightness above it.
        /// </summary>
        [Fact]
        public async Task TheStripsSelectableTextIsHeldBackWhileTheLargeViewIsOpen()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000) { HostRendersSelectableText = true };
            var (tab, state, content, time) = await PlannerWithPictureAsync(renderer);
            var closed = tab.SelectableTextRegions.Length;
            closed.ShouldBeGreaterThan(0, "closed, the detail lines are selectable text");
            var lines = tab.SelectableTextRegions.ToArray().Count(r => r.Y >= tab.DetailsRect.Y);

            state.PictureExpanded = true;
            tab.Render(state, content, time);
            // What the large view itself writes stays selectable above the scrim; the strip's lines are held back.
            lines.ShouldBe(closed, "closed, every selectable line is the strip's");
            tab.SelectableTextRegions.Length.ShouldBeLessThan(closed);
            tab.Pictures.Count.ShouldBe(2);
        }

        private const string PictureOpenAction = "ObjectInfoPictureOpen";
        private const string PictureCloseAction = "ObjectInfoPictureClose";

        private static readonly DateTimeOffset PictureNow = new(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);

        private static async Task<(PictureCapturingPlanner Tab, PlannerState State, RectF32 Content, FakeTimeProviderWrapper Time)>
            PlannerWithPictureAsync(RgbaImageRenderer renderer, CatalogIndex? index = null, float dpiScale = 1f,
                float interfaceScale = 1f, ICelestialObjectDB? db = null)
        {
            db ??= await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
            CatalogUtils.TryGetCleanedUpCatalogName("M31", out var m31).ShouldBeTrue();
            var tab = new PictureCapturingPlanner(renderer) { DpiScale = dpiScale, InterfaceScale = interfaceScale, FontPath = FontResolver.ResolveSystemFont() };
            var state = BuildState(index ?? m31, db);
            var time = new FakeTimeProviderWrapper(PictureNow);
            var content = new RectF32(0, 0, renderer.Width, renderer.Height);
            tab.Render(state, content, time);
            return (tab, state, content, time);
        }

        [Theory]
        [InlineData(1f, 1f, 1600)]
        [InlineData(1.5f, 1f, 1600)]
        [InlineData(1.5f, 1.15f, 1600)] // the GUI's planner always carries the interface scale
        [InlineData(1f, 1f, 640)]       // a narrow strip, where the 40 % cap on the picture section bites
        public async Task TheDetailsStripShowsTheVerifiedPictureRightOfTheTextAtTheStripsHeight(
            float dpiScale, float interfaceScale, int width)
        {
            var scale = dpiScale * interfaceScale;
            // Landscape both ways: portrait (H > W) drops the picture from the strip.
            var height = width >= 1000 ? 1000u : 480u;
            using var renderer = new RgbaImageRenderer((uint)width, height);
            var (tab, _, _, _) = await PlannerWithPictureAsync(renderer, dpiScale: dpiScale, interfaceScale: interfaceScale);

            var thumbnail = tab.Pictures.ShouldHaveSingleItem().Rect;
            var details = tab.DetailsRect;
            var strip = details.Height;

            // The strip did not grow for the picture: a target without one has the same.
            using (var plain = new RgbaImageRenderer((uint)width, height))
            {
                var (noPicture, _, _, _) = await PlannerWithPictureAsync(plain, CatalogIndex.IC1000, dpiScale, interfaceScale);
                noPicture.DetailsRect.Height.ShouldBe(strip, 0.5f);
                noPicture.PaintedDetailsTextRect.Width.ShouldBe(noPicture.DetailsRect.Width, 0.5f);
            }

            // At the strip's right end, inside it, with the text keeping the most of it.
            (thumbnail.X + thumbnail.Width).ShouldBeLessThanOrEqualTo(details.X + details.Width);
            thumbnail.Y.ShouldBeGreaterThanOrEqualTo(details.Y);
            (thumbnail.Y + thumbnail.Height).ShouldBeLessThanOrEqualTo(details.Y + details.Height);
            tab.PaintedDetailsTextRect.Width.ShouldBeGreaterThan(details.Width * (1f - PlannerTab<RgbaImage>.DetailsPictureMaxShare) - 1f);
            thumbnail.X.ShouldBeGreaterThanOrEqualTo(tab.PaintedDetailsTextRect.X + tab.PaintedDetailsTextRect.Width);

            // The thumbnail is the picture's own aspect, and its credit row stays inside the strip, beside the text.
            var image = tab.Pictures.Single().Image;
            if (width >= 1000)
            {
                (thumbnail.Width / thumbnail.Height).ShouldBe((float)image.Width / image.Height, 0.05f);
            }
            var credit = tab.GetRegisteredRegions()
                .Single(r => r.Result is HitResult.LinkHit { Url: var url } && url == image.FilePageUrl);
            credit.X.ShouldBeGreaterThanOrEqualTo(tab.PaintedDetailsTextRect.X + tab.PaintedDetailsTextRect.Width - 0.5f);
            (credit.X + credit.Width).ShouldBeLessThanOrEqualTo(details.X + details.Width + 0.5f);
            credit.Y.ShouldBeGreaterThanOrEqualTo(details.Y);
            (credit.Y + credit.Height).ShouldBeLessThanOrEqualTo(details.Y + details.Height + 0.5f);
            if (width < 1000)
            {
                var sectionW = details.X + details.Width - (tab.PaintedDetailsTextRect.X + tab.PaintedDetailsTextRect.Width);
                sectionW.ShouldBe(details.Width * PlannerTab<RgbaImage>.DetailsPictureMaxShare, 1f, "the cap is what sized the section");
            }

            // Every text line fits the column the picture left, not the strip.
            var fontPath = FontResolver.ResolveSystemFont();
            var fontSize = GuiTheme.Metrics.BaseFontSize * scale;
            foreach (var line in tab.PaintedDetailLines.Skip(1))
            {
                renderer.MeasureText(line.AsSpan(), fontPath, fontSize).Width
                    .ShouldBeLessThanOrEqualTo(tab.PaintedDetailsTextRect.Width - (2f * PlannerTab<RgbaImage>.BasePadding * scale), line);
            }
        }

        [Fact]
        public async Task ATargetWithNoPictureAndThePortraitStripKeepTheirTextOnly()
        {
            var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
            db.TryGetArticle(CatalogIndex.IC1000, out _).ShouldBeFalse("the test needs an object the table does not cover");

            using (var renderer = new RgbaImageRenderer(1600, 1000))
            {
                var (tab, state, _, _) = await PlannerWithPictureAsync(renderer, CatalogIndex.IC1000);
                tab.Pictures.ShouldBeEmpty();
                tab.GetRegisteredRegions()
                    .Any(r => r.Result is HitResult.ButtonHit { Action: PictureOpenAction }).ShouldBeFalse();
                state.PictureExpanded.ShouldBeFalse();
            }

            using (var renderer = new RgbaImageRenderer(390, 844))
            {
                var (tab, _, _, _) = await PlannerWithPictureAsync(renderer);
                tab.Pictures.ShouldBeEmpty("the compact portrait strip keeps text only");
            }
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task APressOnTheThumbnailOpensItLargeAndEscapeOrTheScrimCloseIt(float dpiScale)
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);
            var (tab, state, content, time) = await PlannerWithPictureAsync(renderer, dpiScale: dpiScale);
            var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), () => { })
            {
                Widgets = () => [tab],
                Unhandled = tab.HandleInput,
            };

            var thumbnail = tab.Pictures.Single().Rect;
            router.Handle(new InputEvent.MouseDown(thumbnail.X + (thumbnail.Width / 2f), thumbnail.Y + (thumbnail.Height / 2f)))
                .ShouldBeTrue();
            state.PictureExpanded.ShouldBeTrue();

            // The large view is drawn after the strip, inside the content and centred in it.
            tab.Pictures.Clear();
            tab.Render(state, content, time);
            tab.Pictures.Count.ShouldBe(2);
            var large = tab.Pictures[^1].Rect;
            large.Width.ShouldBeGreaterThan(thumbnail.Width * 2f);
            large.X.ShouldBeGreaterThanOrEqualTo(content.X);
            (large.X + large.Width).ShouldBeLessThanOrEqualTo(content.X + content.Width);
            (large.Y + large.Height).ShouldBeLessThanOrEqualTo(content.Y + content.Height);
            (large.X + (large.Width / 2f)).ShouldBe(content.X + (content.Width / 2f), 1f);

            // A press anywhere outside the picture is the scrim, over the list and the chart alike.
            tab.HitTest(5f, 5f).ShouldBeOfType<HitResult.ButtonHit>().Action.ShouldBe(PictureCloseAction);
            tab.HitTest(content.Width - 5f, 5f).ShouldBeOfType<HitResult.ButtonHit>().Action.ShouldBe(PictureCloseAction);

            router.Handle(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
            state.PictureExpanded.ShouldBeFalse();

            // Open again, and close it by the scrim.
            state.PictureExpanded = true;
            tab.Render(state, content, time);
            tab.GetRegisteredRegions()
                .Last(r => r.Result is HitResult.ButtonHit { Action: PictureCloseAction })
                .OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
            state.PictureExpanded.ShouldBeFalse();
        }

        [Fact]
        public async Task SelectingATargetWithNoPictureRetiresTheLargeView()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);
            var (tab, state, content, time) = await PlannerWithPictureAsync(renderer);
            state.PictureExpanded = true;

            // The other target has no catalogue index, so no picture.
            state.SelectedTargetIndex = tab.FilteredTargets.ToList().FindIndex(t => t.Target.CatalogIndex is null);
            state.SelectedTargetIndex.ShouldBeGreaterThanOrEqualTo(0);
            tab.Pictures.Clear();
            tab.Render(state, content, time);

            state.PictureExpanded.ShouldBeFalse();
            tab.Pictures.ShouldBeEmpty();
        }
    }
}
