using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Immutable;
using System.IO;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Offline render tests for <see cref="GuiderTab{TSurface}"/> over the CPU
    /// <see cref="RgbaImageRenderer"/> -- no GPU/device needed. These pin the L1 layout-driven
    /// conversion (docs/plans/layout-driven-ui.md): the whole frame is ONE Layout tree, so the
    /// arranged raster-pane rects (graph band, target scatter, star-profile plot) must reproduce
    /// the shipped hand-computed geometry, at 1x and at high DPI, and every region must paint
    /// (the sentinel sweep catches a zero-width pane that silently drops out of the tree).
    /// </summary>
    [Collection("UI")]
    public class GuiderTabLayoutTests
    {
        private static readonly DateTimeOffset SampleStart = new(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);

        /// <summary>A live state mid-guiding: samples, stats, and a star profile, so the graph,
        /// target view, stats rows, and profile plot all have real content to paint.</summary>
        private static LiveSessionState BuildGuidingState()
        {
            var samples = ImmutableArray.CreateBuilder<GuideErrorSample>(60);
            for (var i = 0; i < 60; i++)
            {
                samples.Add(new GuideErrorSample(
                    SampleStart + TimeSpan.FromSeconds(i * 2),
                    RaError: Math.Sin(i * 0.3) * 0.8,
                    DecError: Math.Cos(i * 0.2) * 0.5,
                    RaCorrectionMs: i % 5 == 0 ? 120 : 0,
                    DecCorrectionMs: i % 7 == 0 ? -90 : 0,
                    IsDither: i == 30,
                    IsSettling: i is > 30 and < 35));
            }

            // Gaussian-ish cross-sections so the profile pane plots + fits real curves.
            var h = new float[21];
            var v = new float[21];
            for (var i = 0; i < h.Length; i++)
            {
                var d = i - 10;
                h[i] = 1000f * MathF.Exp(-d * d / 8f);
                v[i] = 900f * MathF.Exp(-d * d / 10f);
            }

            return new LiveSessionState
            {
                IsRunning = true,
                Phase = SessionPhase.Observing,
                GuiderState = "Guiding",
                GuideSamples = samples.MoveToImmutable(),
                LastGuideStats = new GuideStats
                {
                    TotalRMS = 0.82,
                    RaRMS = 0.55,
                    DecRMS = 0.61,
                    PeakRa = 1.6,
                    PeakDec = 1.3,
                    LastRaErr = 0.31,
                    LastDecErr = -0.22,
                },
                GuideExposure = TimeSpan.FromSeconds(2),
                GuideStarProfile = (h, v),
            };
        }

        private static GuiderTab<RgbaImage> RenderTab(RgbaImageRenderer renderer, LiveSessionState live, float dpiScale = 1f)
        {
            // DPI + font are widget-owned properties now (host-set), not Render arguments.
            // A real font: the header/stats/axis labels rasterize glyphs on the CPU renderer.
            var tab = new GuiderTab<RgbaImage>(renderer) { DpiScale = dpiScale, FontPath = FontResolver.ResolveSystemFont() };
            var time = new FakeTimeProviderWrapper(SampleStart);
            tab.Render(live, new RectF32(0, 0, renderer.Width, renderer.Height), time);
            return tab;
        }

        [Fact]
        public void Guiding_ArrangesTheShippedFrame()
        {
            using var renderer = new RgbaImageRenderer(1280, 800);
            var tab = RenderTab(renderer, BuildGuidingState());

            // Graph band pinned to the bottom, full width, max(20% of the body, 80) tall.
            var graphH = MathF.Max((800f - 32f) * 0.2f, 80f);
            tab.GraphRect.X.ShouldBe(0f, 0.5f);
            tab.GraphRect.Width.ShouldBe(1280f, 0.5f);
            tab.GraphRect.Height.ShouldBe(graphH, 0.5f);
            (tab.GraphRect.Y + tab.GraphRect.Height).ShouldBe(800f, 0.5f);

            // Right column = profile (120) + stats (220); target view is its bottom half.
            var mainH = 800f - 32f - graphH;
            tab.TargetViewRect.X.ShouldBe(1280f - 340f, 0.5f);
            tab.TargetViewRect.Width.ShouldBe(340f, 0.5f);
            tab.TargetViewRect.Y.ShouldBe(32f + mainH / 2f, 0.5f);
            tab.TargetViewRect.Height.ShouldBe(mainH / 2f, 0.5f);

            // Star-profile PLOT rect: the pane minus the panel padding + title row, which are layout. The title is its
            // text's line box (DIR.Lib 11.20), at the 0.85 em it is drawn in.
            var pad = GuiTheme.Metrics.Padding;
            var titleH = TextBaseline.LineHeight(GuiTheme.Metrics.BaseFontSize * 0.85f);
            tab.ProfilePlotRect.X.ShouldBe(1280f - 340f + pad, 0.5f);
            tab.ProfilePlotRect.Width.ShouldBe(120f - pad * 2f, 0.5f);
            tab.ProfilePlotRect.Y.ShouldBe(32f + pad + titleH, 0.5f);
            (tab.ProfilePlotRect.Y + tab.ProfilePlotRect.Height).ShouldBe(32f + mainH / 2f - pad, 0.5f);

            // No viewer wired in this test, so the camera pane is its empty-state text leaf.
            tab.CameraRect.Width.ShouldBe(0f);
        }

        [Fact]
        public void Guiding_HighDpi_ScalesTheSameDesignLayout()
        {
            // Same 1280x800 design space at dpiScale 2 (a 2560x1600 physical surface): every rect is
            // exactly the 1x layout scaled -- design units are the single source of truth.
            using var renderer = new RgbaImageRenderer(2560, 1600);
            var tab = RenderTab(renderer, BuildGuidingState(), dpiScale: 2f);

            var graphH = MathF.Max((800f - 32f) * 0.2f, 80f) * 2f;
            tab.GraphRect.Height.ShouldBe(graphH, 1f);
            tab.TargetViewRect.X.ShouldBe(2560f - 680f, 1f);
            tab.TargetViewRect.Width.ShouldBe(680f, 1f);
            tab.GraphRect.Y.ShouldBe(1600f - graphH, 1f);
        }

        [Fact]
        public void Placeholder_HasNoRasterPanes()
        {
            // A default live state (no session) renders the placeholder tree: header + centred
            // reason, no camera/profile/target/graph fills.
            using var renderer = new RgbaImageRenderer(1280, 800);
            var tab = RenderTab(renderer, new LiveSessionState());

            tab.State.PlaceholderReason.ShouldBe(GuiderPlaceholder.NoSession);
            tab.GraphRect.ShouldBe(default(RectF32));
            tab.TargetViewRect.ShouldBe(default(RectF32));
            tab.ProfilePlotRect.ShouldBe(default(RectF32));
            tab.CameraRect.ShouldBe(default(RectF32));
        }

        /// <summary>
        /// With no session and a loop wired (#1915) the NoSession placeholder gives way to the loop's controls: a Loop button
        /// that asks for the guide camera at the stepped exposure and gain, then Stop once the loop runs, and the steppers.
        /// </summary>
        [Fact]
        public void NoSession_WithAGuideLoop_OffersLoopExposureAndGain()
        {
            using var renderer = new RgbaImageRenderer(1280, 800);
            var bus = new SignalBus();
            var loop = new GuideLoopController(new SystemTimeProvider(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveViewController>.Instance);
            var tab = new GuiderTab<RgbaImage>(renderer)
            {
                DpiScale = 1f,
                FontPath = FontResolver.ResolveSystemFont(),
                Bus = bus,
                GuideLoop = loop,
            };
            var starts = new List<StartGuideLoopSignal>();
            bus.Subscribe<StartGuideLoopSignal>(signal => starts.Add(signal));
            var time = new FakeTimeProviderWrapper(SampleStart);

            void Draw() => tab.Render(new LiveSessionState(), new RectF32(0, 0, 1280, 800), time);
            bool Has(string action) => tab.GetRegisteredRegions().Any(r => r.Result is HitResult.ButtonHit hit && hit.Action == action);
            void Click(string action)
            {
                var region = tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit hit && hit.Action == action);
                UiRouting.RoutePress(tab, region.X + (region.Width / 2f), region.Y + (region.Height / 2f));
                bus.ProcessPending();
            }

            Draw();
            Has("GuideLoopToggle").ShouldBeTrue();
            Has("GuideLoopExpDec").ShouldBeTrue();
            Has("GuideLoopExpInc").ShouldBeTrue();
            Has("GuideLoopGainDec").ShouldBeTrue();
            Has("GuideLoopGainInc").ShouldBeTrue();

            // The steppers move what the loop starts at, and the Loop button asks for exactly that.
            Click("GuideLoopExpInc");
            Click("GuideLoopGainInc");
            loop.ExposureSeconds.ShouldBeGreaterThan(1.0);
            loop.Gain.ShouldBe(GuideLoopController.GainStep);
            Draw();
            Click("GuideLoopToggle");
            starts.ShouldBe([new StartGuideLoopSignal(loop.ExposureSeconds, GuideLoopController.GainStep)]);

            // Once it runs, the same button is Stop.
            loop.View.Began(0, null);
            var stops = 0;
            bus.Subscribe<StopGuideLoopSignal>(_ => stops++);
            Draw();
            Click("GuideLoopToggle");
            stops.ShouldBe(1);
            starts.Count.ShouldBe(1);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public void GuideDarks_AreOfferedWithNoLoopAndAskTheNodeForExactlyTheLoopsExposure(float dpi)
        {
            using var renderer = new RgbaImageRenderer(1600, 800);
            var bus = new SignalBus();
            var loop = new GuideLoopController(new SystemTimeProvider(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveViewController>.Instance);
            var tab = LoopTab(renderer, bus, dpi);
            tab.GuideLoop = loop;
            var starts = new List<StartGuideDarksSignal>();
            bus.Subscribe<StartGuideDarksSignal>(signal => starts.Add(signal));
            var time = new FakeTimeProviderWrapper(SampleStart);

            void Draw() => tab.Render(new LiveSessionState(), new RectF32(0, 0, 1600, 800), time);
            ClickableRegion[] Regions(string action) => [.. tab.GetRegisteredRegions().Where(r => r.Result is HitResult.ButtonHit hit && hit.Action == action)];

            Draw();
            var darks = Regions("GuideDarks").ShouldHaveSingleItem();
            darks.Width.ShouldBeGreaterThan(40f * dpi, "the label fits its painted box");
            var inc = Regions("GuideDarksCountInc").ShouldHaveSingleItem();

            UiRouting.RoutePress(tab, inc.X + (inc.Width / 2f), inc.Y + (inc.Height / 2f));
            bus.ProcessPending();
            loop.Darks.Count.ShouldBe(20);

            Draw();
            darks = Regions("GuideDarks").ShouldHaveSingleItem();
            UiRouting.RoutePress(tab, darks.X + (darks.Width / 2f), darks.Y + (darks.Height / 2f));
            bus.ProcessPending();
            starts.ShouldBe([new StartGuideDarksSignal(loop.ExposureSeconds, null)]);

            // With the loop running there is nothing to press: the node refuses darks while the camera loops.
            loop.View.Began(0, null);
            Draw();
            Regions("GuideDarks").ShouldBeEmpty();
        }

        private static GuiderTab<RgbaImage> LoopTab(RgbaImageRenderer renderer, SignalBus bus, float dpiScale = 1f)
            => new(renderer)
            {
                DpiScale = dpiScale,
                FontPath = FontResolver.ResolveSystemFont(),
                Bus = bus,
                GuideLoop = new GuideLoopController(new SystemTimeProvider(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveViewController>.Instance),
            };

        /// <summary>A looped guide frame as the loop's source holds it: unit-referred, one Gaussian star over a quiet sky.</summary>
        private static LiveFramePreviewSource SourceWithAStar(double starX, double starY, float peak)
        {
            var plane = new float[240, 320];
            var rng = new Random(3);
            for (var y = 0; y < 240; y++)
            {
                for (var x = 0; x < 320; x++)
                {
                    var dx = x - starX;
                    var dy = y - starY;
                    plane[y, x] = 0.01f + (0.0005f * (float)rng.NextDouble()) + (peak * MathF.Exp((float)(-(dx * dx + dy * dy) / 8.0)));
                    plane[y, x] = MathF.Min(plane[y, x], 1f);
                }
            }
            var meta = new ImageMeta("synth", SampleStart, TimeSpan.FromSeconds(1),
                FrameType.Light, "", 3.76f, 3.76f, 500, FocuserPositions.None, Filter.Luminance, 1, 1,
                float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            var source = new LiveFramePreviewSource();
            source.AcceptFrame(new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta), freezeStats: false)
                .ShouldBeTrue();
            return source;
        }

        /// <summary>
        /// While looping, a picked star is read off the frame: Auto finds it, the box follows it frame to frame, and the panes
        /// show the readout (HFD, SNR, peak) with the profile plotted and the saturation said in words (#1915).
        /// </summary>
        [Fact]
        public void Loop_AutoPicksTheStarAndTheReadoutAndProfileShow()
        {
            using var renderer = new RgbaImageRenderer(1280, 800);
            var bus = new SignalBus();
            var tab = LoopTab(renderer, bus);
            var time = new FakeTimeProviderWrapper(SampleStart);
            var source = SourceWithAStar(150.4, 100.6, 0.5f);

            void Draw() => tab.Render(new LiveSessionState(), new RectF32(0, 0, 1280, 800), time);
            Draw();
            tab.State.Readout.ShouldBeNull();
            tab.GetRegisteredRegions().Any(r => r.Result is HitResult.ButtonHit { Action: "GuideStarAuto" }).ShouldBeTrue();

            // The button asks; the next frame answers.
            var auto = tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit { Action: "GuideStarAuto" });
            UiRouting.RoutePress(tab, auto.X + (auto.Width / 2f), auto.Y + (auto.Height / 2f));
            tab.State.UpdateReadout(source, isNewFrame: true);

            var star = tab.State.Readout.ShouldNotBeNull();
            star.X.ShouldBe(150.4, 0.3);
            star.Y.ShouldBe(100.6, 0.3);
            star.Saturated.ShouldBeFalse();
            tab.State.Selection.ShouldNotBeNull().X.ShouldBe(star.X, 1e-9);
            GuiderActions.SaturationWords(star).ShouldBe("Not saturated");

            // The next frame moves the star two pixels: the selection follows it.
            tab.State.UpdateReadout(SourceWithAStar(152.4, 100.6, 0.5f), isNewFrame: true);
            tab.State.Selection.ShouldNotBeNull().X.ShouldBe(152.4, 0.3);

            // No viewer in this test, so the profile pane is what shows the star: its plot rect is laid out.
            Draw();
            tab.ProfilePlotRect.Width.ShouldBeGreaterThan(0f);
        }

        /// <summary>The node's state of a guiding run (#1915), with the guider's own slice when it goes on: what the tab is handed.</summary>
        private static GuidingStateDto RunState(bool running, GuidingPhase phase, bool withSteps = false, bool withCalibration = false,
            string? failure = null)
        {
            var steps = ImmutableArray.CreateBuilder<GuideStepDto>();
            for (var i = 0; withSteps && i < 40; i++)
            {
                steps.Add(new GuideStepDto
                {
                    Timestamp = SampleStart + TimeSpan.FromSeconds(i),
                    RaError = Math.Sin(i * 0.3) * 0.8,
                    DecError = Math.Cos(i * 0.2) * 0.5,
                    RaCorrectionMs = 40,
                    DecCorrectionMs = -30,
                    IsDither = false,
                    IsSettling = false,
                });
            }

            return new GuidingStateDto
            {
                Running = running,
                Mode = GuideRunMode.Guide,
                Phase = phase,
                Camera = "Guide cam",
                FailureReason = failure,
                Calibration = withCalibration
                    ? new GuiderCalibrationDto
                    {
                        RaAngleDeg = 93.4,
                        DecAngleDeg = 3.1,
                        RaRateArcsecPerSec = 12.8,
                        DecRateArcsecPerSec = 14.2,
                        OrthogonalityErrorDeg = 0.9,
                        DurationSeconds = 38,
                        Overlay = new CalibrationOverlayDto
                        {
                            RaOrigin = new CalibrationPointDto { X = 160, Y = 120 },
                            DecOrigin = new CalibrationPointDto { X = 160, Y = 120 },
                            RaSteps = [new CalibrationPointDto { X = 180, Y = 121 }],
                            DecSteps = [new CalibrationPointDto { X = 161, Y = 140 }],
                            PixelScaleArcsec = 3.8,
                            CameraAngleRad = 0.05,
                        },
                    }
                    : null,
                Guider = running
                    ? new GuiderStateDto
                    {
                        State = phase.ToString(),
                        TotalRMS = 0.82,
                        RaRMS = 0.55,
                        DecRMS = 0.61,
                        PeakRa = 1.6,
                        PeakDec = 1.3,
                        GuideExposureSeconds = 1,
                        RecentSteps = steps.ToImmutable(),
                        GuideFrameNumber = 7,
                    }
                    : null,
            };
        }

        /// <summary>
        /// Calibrate and Guide are offered only while looping with no run going on (the node's run replaces the loop): dimmed
        /// before the loop and during a run, where the first button ends the RUN, and each sends the star chosen on the loop, or
        /// none, at the loop's exposure (#1915).
        /// </summary>
        [Fact]
        public void Loop_CalibrateAndGuideAreOfferedOnlyWhileLoopingAndSendTheChosenStar()
        {
            using var renderer = new RgbaImageRenderer(1280, 800);
            var bus = new SignalBus();
            var tab = LoopTab(renderer, bus);
            var loop = tab.GuideLoop.ShouldNotBeNull();
            var time = new FakeTimeProviderWrapper(SampleStart);
            var starts = new List<StartGuidingSignal>();
            var loopStops = 0;
            var runStops = 0;
            bus.Subscribe<StartGuidingSignal>(signal => starts.Add(signal));
            bus.Subscribe<StopGuideLoopSignal>(_ => loopStops++);
            bus.Subscribe<StopGuidingSignal>(_ => runStops++);

            void Draw() => tab.Render(new LiveSessionState(), new RectF32(0, 0, 1280, 800), time);
            bool Has(string action) => tab.GetRegisteredRegions().Any(r => r.Result is HitResult.ButtonHit hit && hit.Action == action);
            void Click(string action)
            {
                var region = tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit hit && hit.Action == action);
                UiRouting.RoutePress(tab, region.X + (region.Width / 2f), region.Y + (region.Height / 2f));
                bus.ProcessPending();
            }

            // Not looping: nothing to calibrate on.
            Draw();
            Has("GuideCalibrate").ShouldBeFalse();
            Has("GuideStart").ShouldBeFalse();

            // Looping, with no star chosen: both are offered and leave the star to the guider.
            loop.View.Began(0, null);
            Draw();
            Has("GuideCalibrate").ShouldBeTrue();
            Has("GuideStart").ShouldBeTrue();
            Click("GuideCalibrate");
            starts.ShouldBe([new StartGuidingSignal(GuideRunMode.Calibrate, loop.ExposureSeconds)]);

            // With a star chosen, Guide sends where it is.
            tab.State.SelectStar(150, 100);
            tab.State.UpdateReadout(SourceWithAStar(150.4, 100.6, 0.5f), isNewFrame: false);
            var chosen = tab.State.Selection.ShouldNotBeNull();
            Draw();
            Click("GuideStart");
            starts.Count.ShouldBe(2);
            starts[1].ShouldBe(new StartGuidingSignal(GuideRunMode.Guide, loop.ExposureSeconds, null, chosen.X, chosen.Y));

            // A run going on: both are dimmed, and the first button, Stop, ends the run and not the loop.
            loop.Run.Publish(RunState(running: true, GuidingPhase.Calibrating));
            Draw();
            Has("GuideCalibrate").ShouldBeFalse();
            Has("GuideStart").ShouldBeFalse();
            Click("GuideLoopToggle");
            (runStops, loopStops).ShouldBe((1, 0));

            // The run ended and the loop is back: offered again, and the button is the loop's Stop once more.
            loop.Run.Publish(RunState(running: false, GuidingPhase.Idle));
            Draw();
            Has("GuideCalibrate").ShouldBeTrue();
            Click("GuideLoopToggle");
            (runStops, loopStops).ShouldBe((1, 1));
        }

        /// <summary>
        /// While the run guides, its steps and RMS are drawn as a session's are (the graph pane, the stats panel in the readout's
        /// place), and the calibration is said in words beside the L it drew on the frame; once it ends the graph goes and the
        /// calibration stays, and a failure is said beside the loop (#1915).
        /// </summary>
        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public void Loop_ShowsTheCalibrationInWordsAndTheGuideGraphWhileTheRunGuides(float dpiScale)
        {
            // The same 1280x800 design space at the scale: the graph's height is the engine's split of it, so it stays a fifth
            // of what the header leaves, and its bottom edge is the content's.
            var width = (uint)(1280 * dpiScale);
            var height = (uint)(800 * dpiScale);
            using var renderer = new RgbaImageRenderer(width, height);
            var tab = LoopTab(renderer, new SignalBus(), dpiScale);
            var loop = tab.GuideLoop.ShouldNotBeNull();
            var time = new FakeTimeProviderWrapper(SampleStart);
            void Draw() => tab.Render(new LiveSessionState(), new RectF32(0, 0, width, height), time);

            // Looping, never calibrated: the panel says how to get one, and there is no graph.
            loop.View.Began(0, null);
            Draw();
            tab.State.Run.ShouldBeNull();
            tab.GraphRect.ShouldBe(default(RectF32));

            loop.Run.Publish(RunState(running: true, GuidingPhase.Guiding, withSteps: true, withCalibration: true));
            Draw();
            tab.State.IsGuidingRun.ShouldBeTrue();
            tab.State.GuideSamples.Length.ShouldBe(40);
            tab.State.LastGuideStats.ShouldNotBeNull().TotalRMS.ShouldBe(0.82, 1e-9);
            tab.State.CalibrationOverlay.ShouldNotBeNull().RaSteps.Length.ShouldBe(1);
            tab.GraphRect.Width.ShouldBe(width, 0.5f);
            tab.GraphRect.Height.ShouldBe(MathF.Max((800f - 32f) * 0.2f, 80f) * dpiScale, 1f);
            (tab.GraphRect.Y + tab.GraphRect.Height).ShouldBe(height, 1f);
            GuiderActions.GuidingStatus(loop.Run.View).ShouldBe("Guiding  0.82\" RMS");

            GuiderActions.CalibrationLines(tab.State.Run.ShouldNotBeNull().Calibration.ShouldNotBeNull()).ShouldBe(
            [
                "RA: 93° at 12.8\"/s",
                "Dec: 3° at 14.2\"/s",
                "Axes 0.9° from square",
                "Took 38 s",
            ]);

            // Ended: the graph is gone, the calibration is kept, and a failure is said beside the loop.
            loop.Run.Publish(RunState(running: false, GuidingPhase.Idle, withCalibration: true, failure: "The guider stopped"));
            Draw();
            tab.State.IsGuidingRun.ShouldBeFalse();
            tab.GraphRect.ShouldBe(default(RectF32));
            tab.State.Run.ShouldNotBeNull().Calibration.ShouldNotBeNull();
            GuiderActions.GuidingStatus(loop.Run.View).ShouldBe("The guider stopped");
        }

        [Fact]
        public void CalibrationLines_SayWhyAnAxisPairThatIsOffSquareIs()
        {
            var lines = GuiderActions.CalibrationLines(new GuiderCalibrationDto { OrthogonalityErrorDeg = 12.3, RaAngleDeg = 10, RaRateArcsecPerSec = 7 });

            lines.ShouldContain("Axes 12.3° from square: cone error or camera tilt");
            lines[0].ShouldBe("RA: 10° at 7.0\"/s");
            lines[1].ShouldBe("Dec: ? at ?", "an unknown figure is said so, never shown as zero");
        }

        [Fact]
        public void Loop_AClippedStarIsSaidToBeSaturated()
        {
            using var renderer = new RgbaImageRenderer(1280, 800);
            var tab = LoopTab(renderer, new SignalBus());

            tab.State.SelectAutoStar();
            tab.State.UpdateReadout(SourceWithAStar(150, 100, 1.5f), isNewFrame: true);

            var star = tab.State.Readout.ShouldNotBeNull();
            star.Saturated.ShouldBeTrue();
            GuiderActions.SaturationWords(star).ShouldContain("Saturated");
        }

        [Fact]
        public void Loop_AClickNearAStarPicksIt_AndClearDropsTheSelection()
        {
            var state = new GuiderTabState();
            var source = SourceWithAStar(200, 120, 0.5f);

            state.SelectStar(204, 116);
            state.UpdateReadout(source, isNewFrame: false);
            state.Selection.ShouldNotBeNull().X.ShouldBe(200, 0.3);

            state.ClearSelection();
            state.Selection.ShouldBeNull();
            state.Readout.ShouldBeNull();
        }

        /// <summary>
        /// The guide focuser's controls are the Preview's own, shown only while the profile has one assigned and connected,
        /// and each press is the Preview's signal aimed at the guide focuser.
        /// </summary>
        [Fact]
        public void Loop_GuideFocuserControlsShowOnlyWithAConnectedGuideFocuser()
        {
            using var renderer = new RgbaImageRenderer(1280, 800);
            var bus = new SignalBus();
            var tab = LoopTab(renderer, bus);
            var time = new FakeTimeProviderWrapper(SampleStart);
            var jogs = new List<JogFocuserSignal>();
            var gotos = new List<GotoFocuserSignal>();
            bus.Subscribe<JogFocuserSignal>(signal => jogs.Add(signal));
            bus.Subscribe<GotoFocuserSignal>(signal => gotos.Add(signal));
            bool Has(string action) => tab.GetRegisteredRegions().Any(r => r.Result is HitResult.ButtonHit hit && hit.Action == action);
            void Click(string action)
            {
                var region = tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit hit && hit.Action == action);
                UiRouting.RoutePress(tab, region.X + (region.Width / 2f), region.Y + (region.Height / 2f));
                bus.ProcessPending();
            }

            // Assigned but not connected (the node reads none), or not assigned: nothing to show.
            var live = new LiveSessionState();
            tab.Render(live, new RectF32(0, 0, 1280, 800), time);
            Has("FocFineInGuide").ShouldBeFalse();
            Has("FocGotoGuide").ShouldBeFalse();

            live.GuideFocuser = new FocuserReading(5000, 12.5, false);
            tab.Render(live, new RectF32(0, 0, 1280, 800), time);
            Has("FocFineInGuide").ShouldBeTrue();
            Has("FocCoarseOutGuide").ShouldBeTrue();
            Has("FocGotoGuide").ShouldBeTrue();

            Click("FocFineInGuide");
            Click("FocCoarseOutGuide");
            jogs.ShouldBe([new JogFocuserSignal(FocuserTargets.Guider, -10), new JogFocuserSignal(FocuserTargets.Guider, 100)]);

            // The goto field was pre-filled with the position the focuser gave.
            live.GuideFocuserGotoInput.Text.ShouldBe("5000");
            Click("FocGotoGuide");
            gotos.ShouldBe([new GotoFocuserSignal(FocuserTargets.Guider, 5000)]);

            // Disconnected again: the controls go.
            live.GuideFocuser = null;
            tab.Render(live, new RectF32(0, 0, 1280, 800), time);
            Has("FocFineInGuide").ShouldBeFalse();
        }

        // Sentinel the guider tab never paints: the root tree carries the content background and every
        // pane paints an opaque panel background, so after a render almost no sentinel survives. A
        // zero/negative-width pane (the classic conversion failure mode) leaves its whole rect
        // sentinel-coloured, which this catches.
        private static bool IsSentinel(byte r, byte g, byte b) => r == 0xff && g == 0x00 && b == 0xff;

        [Theory]
        [InlineData(1280, 800, true, "guiding-desktop")]
        [InlineData(500, 360, true, "guiding-narrow")]
        [InlineData(1280, 800, false, "placeholder")]
        public void Guider_PaintsTheFullContent(int width, int height, bool guiding, string label)
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var pixels = renderer.Surface.Pixels;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                pixels[i] = 0xff; pixels[i + 1] = 0x00; pixels[i + 2] = 0xff; pixels[i + 3] = 0xff;
            }

            RenderTab(renderer, guiding ? BuildGuidingState() : new LiveSessionState());

            long sentinel = 0;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                if (IsSentinel(pixels[i], pixels[i + 1], pixels[i + 2]))
                {
                    sentinel++;
                }
            }

            // Emit a PNG beside the test binary so the render can be eyeballed.
            var pngPath = Path.Combine(AppContext.BaseDirectory, $"guidertab-{label}.png");
            File.WriteAllBytes(pngPath, PngWriter.Encode(pixels, renderer.Surface.Width, renderer.Surface.Height));

            var sentinelFraction = (double)sentinel / ((long)width * height);
            sentinelFraction.ShouldBeLessThan(0.02,
                $"{label} ({width}x{height}) left unpainted regions; PNG at {pngPath}");
        }
    }
}
