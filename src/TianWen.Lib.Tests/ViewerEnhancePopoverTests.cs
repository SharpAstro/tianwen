using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The sparkles button opens one Enhance popover (#1345, P36): the deep-sky half holds the AI program (the backends that
    /// serve, run / cancel with its progress, and off), the planetary half the planet and telescope and the wavelet sharpening.
    /// </summary>
    /// <remarks>
    /// Observed through the hit tracker, as <see cref="ViewerWhiteBalancePopoverTests"/> is: a popover that is open has
    /// registered its buttons, one that is closed has registered nothing. Run, Cancel and Off are asserted by what they
    /// PRESS (the Enhance button with no mouse button, which is what <c>E</c> does), because the toggle itself is the
    /// controller's and is pinned by <c>ViewerControllerTests</c>. Every test runs at DPI 1 and 1.5, the window growing with it.
    /// </remarks>
    [Collection("UI")]
    public class ViewerEnhancePopoverTests
    {
        private const uint WindowW = 900;
        private const uint WindowH = 700;

        /// <summary>The planetary half stacks the telescope and the dials, which want more than the deep-sky half's window.</summary>
        private const uint TallH = 1400;
        private const int ImageW = 8;
        private const int ImageH = 6;

        private sealed class PopoverViewer : ImageRendererBase<RgbaImage>
        {
            public PopoverViewer(RgbaImageRenderer renderer, SignalBus bus, float dpiScale) : base(renderer)
            {
                Bus = bus;
                Width = renderer.Width;
                Height = renderer.Height;
                DpiScale = dpiScale;
                FontPath = FontResolver.ResolveSystemFont();
            }

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels) { }

            protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
                ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

            protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
                float rotationRad, RGBAColor32 color, float thickness) { }

            protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

            protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
                RGBAColor32 color, float thickness) { }

            protected override void OnResize(uint width, uint height) { }

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int width, int height) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;

            public RectF32 ImageArea => ImageAreaRect;
        }

        private static readonly ImmutableArray<EnhanceChoice> TwoChoices =
        [
            new(EnhanceBackend.Auto, "Auto", ["gradient-correction", "denoise-starless"]),
            new(EnhanceBackend.TianWen, "TianWen", ["gradient-correction", "denoise-frame"]),
        ];

        private static RgbaImageRenderer NewRenderer(float dpiScale, uint designHeight = WindowH)
            => new((uint)(WindowW * dpiScale), (uint)(designHeight * dpiScale));

        private static async Task<(PopoverViewer Viewer, ViewerState State, AstroImageDocument Document)>
            NewViewerAsync(RgbaImageRenderer renderer, float dpiScale, CancellationToken ct, AstroImageDocument? shown = null)
        {
            var document = shown ?? await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
            var viewer = new PopoverViewer(renderer, new SignalBus(), dpiScale)
            {
                // The bar a host with an AI pipeline offers; the default offer has no Enhance button.
                Offer = ToolbarOffer.FileViewer.With(ToolbarAction.Enhance),
            };
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);
            var state = new ViewerState
            {
                ShowFileList = false,
                ShowHistogram = false,
                ShowInfoPanel = true,
                StretchMode = StretchMode.None,
                ZoomToFit = false,
                Zoom = 1f,
            };
            viewer.Render(document, state);
            return (viewer, state, document);
        }

        private static RectF32 Button(PopoverViewer viewer)
        {
            viewer.TryGetPaintedToolbarRect(ToolbarAction.Enhance, out var rect)
                .ShouldBeTrue("the Enhance button is on the bar of a host with an AI pipeline");
            return rect;
        }

        private static void Press(PopoverViewer viewer, float x, float y)
            => UiRouting.Route(viewer, new InputEvent.MouseDown(x, y), new InputEvent.MouseUp(x, y));

        private static void PressButton(PopoverViewer viewer)
        {
            var button = Button(viewer);
            Press(viewer, button.X + (button.Width / 2f), button.Y + (button.Height / 2f));
        }

        /// <summary>Every button registered below the bar whose name passes <paramref name="named"/>, at the centre of its own rect.</summary>
        private static Dictionary<string, (float X, float Y)> HitsBelowTheBar(PopoverViewer viewer, Func<string, bool> named)
        {
            var bottom = Button(viewer).Bottom;
            var buttons = new Dictionary<string, (float X, float Y)>();
            foreach (var region in viewer.GetRegisteredRegions())
            {
                if (region.Y >= bottom && region.Result is HitResult.ButtonHit hit && named(hit.Action))
                {
                    buttons.TryAdd(hit.Action, (region.X + region.Width / 2f, region.Y + region.Height / 2f));
                }
            }
            return buttons;
        }

        /// <summary>The deep-sky half's own rows. The statistics toggle sits below the bar whatever the popover does, so it is not counted.</summary>
        private static Dictionary<string, (float X, float Y)> HitsBelowTheBar(PopoverViewer viewer)
            => HitsBelowTheBar(viewer, action => action.StartsWith("Enhance", StringComparison.Ordinal));

        /// <summary>The planetary half's controls, by the names the strip used to register them under.</summary>
        private static Dictionary<string, (float X, float Y)> PlanetaryHits(PopoverViewer viewer)
            => HitsBelowTheBar(viewer, action => action.StartsWith("Planet", StringComparison.Ordinal)
                || action.StartsWith("Design", StringComparison.Ordinal) || action.StartsWith("Aperture", StringComparison.Ordinal)
                || action.StartsWith("Strength", StringComparison.Ordinal) || action.StartsWith("BalanceSaturation", StringComparison.Ordinal) || action.StartsWith("Wavelet", StringComparison.Ordinal));

        private static async Task<(PopoverViewer Viewer, ViewerState State, List<(ToolbarAction Action, MouseButton Button)> Presses)>
            OpenAsync(RgbaImageRenderer renderer, float dpiScale, CancellationToken ct, Action<ViewerState>? arrange = null, AstroImageDocument? shown = null)
        {
            var (viewer, state, document) = await NewViewerAsync(renderer, dpiScale, ct, shown);
            var presses = new List<(ToolbarAction, MouseButton)>();
            // The host's policy stands in for the controller: what the popover presses is what reaches it.
            viewer.ToolbarPressPolicy = (viewerState, action, button) =>
            {
                if (button == MouseButton.Left && viewer.OpenToolbarDropdown(viewerState, action))
                {
                    return true;
                }
                presses.Add((action, button));
                return true;
            };
            state.EnhanceChoices = TwoChoices;
            arrange?.Invoke(state);
            PressButton(viewer);
            viewer.Render(document, state);
            return (viewer, state, presses);
        }

        /// <summary>A capture's stacked view on show: the telescope and the dials both apply.</summary>
        private static void OnAStackedCapture(ViewerState state)
        {
            state.SequencePath = "capture.ser";
            state.ShowStacked = true;
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task AClosedPopoverHasRegisteredNothing(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, state, _) = await NewViewerAsync(renderer, dpiScale, ct);
            state.EnhanceChoices = TwoChoices;

            state.EnhancePopover.IsOpen.ShouldBeFalse("closed until pressed");
            HitsBelowTheBar(viewer).Keys.ShouldBeEmpty("a closed popover registers no region, and the strip no control");
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task PressingTheButtonOpensThePopoverAndAPressOutsideOrOnTheButtonCloses(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, state, presses) = await OpenAsync(renderer, dpiScale, ct);

            state.EnhancePopover.IsOpen.ShouldBeTrue("the button opens it");
            presses.ShouldBeEmpty("a left press on the button is the menu, not the toggle");
            viewer.Ui.PointerClaim.ShouldNotBeNull("an open popover claims the pointer as it paints");
            HitsBelowTheBar(viewer).Keys.ShouldContain("EnhanceRun");

            var area = viewer.ImageArea;
            Press(viewer, area.X + (area.Width * 0.8f), area.Y + (area.Height * 0.8f));
            state.EnhancePopover.IsOpen.ShouldBeFalse("a press on the picture closes it");

            var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
            viewer.Render(document, state);
            HitsBelowTheBar(viewer).Keys.ShouldBeEmpty("closed again, registered nothing");

            PressButton(viewer);
            viewer.Render(document, state);
            state.EnhancePopover.IsOpen.ShouldBeTrue();
            PressButton(viewer);
            state.EnhancePopover.IsOpen.ShouldBeFalse("a second press on the button closes what the first opened");
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task EscapeClosesIt(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, state, _) = await OpenAsync(renderer, dpiScale, ct);

            UiRouting.RouteKey(viewer, InputKey.Escape);

            state.EnhancePopover.IsOpen.ShouldBeFalse();
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task TheServingBackendsAreAButtonGroupAndChoosingOneSetsThePreference(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, state, _) = await OpenAsync(renderer, dpiScale, ct);

            var hits = HitsBelowTheBar(viewer);
            hits.Keys.ShouldContain("EnhanceBackendAuto");
            hits.Keys.ShouldContain("EnhanceBackendTianWen");
            hits.Keys.ShouldNotContain("EnhanceBackendForceRcAstro", "a backend nothing serves is not offered");

            var tianWen = hits["EnhanceBackendTianWen"];
            Press(viewer, tianWen.X, tianWen.Y);

            state.PreferredEnhanceBackend.ShouldBe(EnhanceBackend.TianWen);
            state.EnhancePopover.IsOpen.ShouldBeTrue("choosing a backend leaves the popover open");
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task RunPressesTheSameToggleEDoes(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, _, presses) = await OpenAsync(renderer, dpiScale, ct);

            var run = HitsBelowTheBar(viewer)["EnhanceRun"];
            Press(viewer, run.X, run.Y);

            presses.ShouldBe([(ToolbarAction.Enhance, MouseButton.None)]);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task WhileRunningTheRowIsCancelWithTheProgressAndRunIsGone(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, _, presses) = await OpenAsync(renderer, dpiScale, ct, s =>
            {
                s.IsEnhancing = true;
                s.EnhanceProgressPct = 42f;
            });

            var hits = HitsBelowTheBar(viewer);
            hits.Keys.ShouldContain("EnhanceCancel");
            hits.Keys.ShouldNotContain("EnhanceRun");
            Press(viewer, hits["EnhanceCancel"].X, hits["EnhanceCancel"].Y);
            presses.ShouldBe([(ToolbarAction.Enhance, MouseButton.None)]);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task WhileEnhancedOffRevertsAndRunDoesNothing(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, _, presses) = await OpenAsync(renderer, dpiScale, ct, s => s.IsEnhanced = true);

            var hits = HitsBelowTheBar(viewer);
            // Registered even when it cannot act, so a press lands on it and does not fall through to the backdrop.
            Press(viewer, hits["EnhanceRun"].X, hits["EnhanceRun"].Y);
            presses.ShouldBeEmpty("a second run would stack a pass on the first one's output");

            Press(viewer, hits["EnhanceOff"].X, hits["EnhanceOff"].Y);
            presses.ShouldBe([(ToolbarAction.Enhance, MouseButton.None)]);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task EKeepsItsToggleAndNeverOpensThePopover(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, state, document) = await NewViewerAsync(renderer, dpiScale, ct);
            var presses = new List<(ToolbarAction, MouseButton)>();
            viewer.ToolbarPressPolicy = (viewerState, action, button) =>
            {
                if (button == MouseButton.Left && viewer.OpenToolbarDropdown(viewerState, action))
                {
                    return true;
                }
                presses.Add((action, button));
                return true;
            };

            UiRouting.RouteKey(viewer, InputKey.E);
            viewer.Render(document, state);

            presses.ShouldBe([(ToolbarAction.Enhance, MouseButton.None)]);
            state.EnhancePopover.IsOpen.ShouldBeFalse();
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task AFrameOnAPlanetsWayShowsThePlanetaryHalfAndNoAiControls(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale, TallH);
            var (viewer, state, _) = await OpenAsync(renderer, dpiScale, ct, OnAStackedCapture);

            state.EnhancesAsPlanet.ShouldBeTrue();
            // Neither the AI backends nor Run, Cancel and Off are on a planet's way: only the popover's own backdrop is.
            HitsBelowTheBar(viewer).Keys.ShouldBe(["EnhancePanelBackground"]);

            var hits = PlanetaryHits(viewer).Keys;
            hits.ShouldContain("PlanetJupiter");
            hits.ShouldContain("PlanetSaturn");
            hits.ShouldContain("ApertureDown");
            hits.ShouldContain("ApertureUp");
            hits.ShouldContain("DesignSct");
            hits.ShouldContain("StrengthTruth");
            hits.ShouldContain("WaveletToggle");
            hits.ShouldContain("WaveletReset");
            hits.ShouldContain("WaveletDerive");
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task TheRawFramesOfASerHaveTheTelescopeAndNoDials(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale, TallH);
            var (viewer, _, _) = await OpenAsync(renderer, dpiScale, ct, s => s.SequencePath = "capture.ser");

            var hits = PlanetaryHits(viewer).Keys;
            hits.ShouldContain("PlanetJupiter", "the telescope applies to the capture");
            hits.ShouldNotContain("WaveletDerive", "the dials re-sharpen a master, not a raw frame");
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task BalanceSaturationStopsShowOnAColourSourceAndChoosingEyeIsKept(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale, TallH);
            var (viewer, state, _) = await OpenAsync(renderer, dpiScale, ct, OnAStackedCapture);

            state.PlanetaryColourSaturation.ShouldBe(PlanetaryColourBalance.DefaultSaturation, "the measured one until the panel says otherwise");
            var hits = PlanetaryHits(viewer);
            hits.Keys.ShouldContain("BalanceSaturationTruth");
            hits.Keys.ShouldContain("BalanceSaturationEye");

            Press(viewer, hits["BalanceSaturationEye"].X, hits["BalanceSaturationEye"].Y);
            state.PlanetaryColourSaturation.ShouldBe(PlanetaryColourBalance.EyeSaturation);
            state.EnhancePopover.IsOpen.ShouldBeTrue("a choice leaves the popover open");

            var truth = PlanetaryHits(viewer)["BalanceSaturationTruth"];
            Press(viewer, truth.X, truth.Y);
            state.PlanetaryColourSaturation.ShouldBe(PlanetaryColourBalance.DefaultSaturation);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task BalanceSaturationRowIsDimWithItsReasonOnAMonoSource(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            var plane = new float[ImageH, ImageW];
            var image = new Image([plane], BitDepth.Int16, 65535f, 0f, 0f,
                new ImageMeta("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                    0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                    RowOrder.TopDown, float.NaN, float.NaN));
            var mono = await AstroImageDocument.AdoptImageAsync(image, DebayerAlgorithm.None, null, filePath: "mono.fits", cancellationToken: ct);
            using var renderer = NewRenderer(dpiScale, TallH);
            var (viewer, _, _) = await OpenAsync(renderer, dpiScale, ct, OnAStackedCapture, mono);

            var hits = PlanetaryHits(viewer).Keys;
            hits.ShouldContain("PlanetJupiter", "the rest of the section is there");
            hits.ShouldNotContain("BalanceSaturationTruth");
            hits.ShouldNotContain("BalanceSaturationEye");
            ImageRendererBase<RgbaImage>.BalanceSaturationMonoReason.ShouldContain("needs a colour source");
        }

        [Fact]
        public void BalanceSaturationChosenReachesTheAutoStackOptions()
        {
            var identity = new PlanetaryIdentity(null, "", null, null, "", null, OpticalDesign.Unknown, "", null, default);
            ViewerController.AutoOptions(identity, 1, PlanetaryColourBalance.EyeSaturation).ColourSaturation.ShouldBe(PlanetaryColourBalance.EyeSaturation);
            ViewerController.AutoOptions(identity, 1, PlanetaryColourBalance.DefaultSaturation).ColourSaturation.ShouldBe(PlanetaryColourBalance.DefaultSaturation);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task ChoosingThePlanetTheDesignAndTheStrengthInThePopoverSetsThem(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale, TallH);
            var (viewer, state, _) = await OpenAsync(renderer, dpiScale, ct, OnAStackedCapture);

            var saturn = PlanetaryHits(viewer)["PlanetSaturn"];
            Press(viewer, saturn.X, saturn.Y);
            state.PlanetaryBody.ShouldBe(CatalogIndex.Saturn);
            state.EnhancePopover.IsOpen.ShouldBeTrue("a choice leaves the popover open");

            var sct = PlanetaryHits(viewer)["DesignSct"];
            Press(viewer, sct.X, sct.Y);
            state.PlanetaryDesign.ShouldBe(OpticalDesign.SCT);
            state.PlanetaryTelescopeSetHere.ShouldBeTrue();

            var strength = PlanetaryHits(viewer)["Strength2"];
            Press(viewer, strength.X, strength.Y);
            state.PlanetaryStrength.ShouldBe(2);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task DeriveAsksForADerivationAndResetPutsThePresetBack(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale, TallH);
            var (viewer, state, _) = await OpenAsync(renderer, dpiScale, ct, OnAStackedCapture);

            var derive = PlanetaryHits(viewer)["WaveletDerive"];
            Press(viewer, derive.X, derive.Y);
            state.WaveletDeriveRequested.ShouldBeTrue("Derive asks the host's derivation for the gains");

            state.WaveletDerived = true;
            state.WaveletDeriveNote = "Gains derived for Jupiter";
            var reset = PlanetaryHits(viewer)["WaveletReset"];
            Press(viewer, reset.X, reset.Y);
            state.WaveletDerived.ShouldBeFalse();
            state.WaveletDeriveNote.ShouldBeNull();
            state.WaveletGains.ShouldBe(WaveletSharpenOptions.PlanetaryDefault.Gains);
        }

        /// <summary>The longest sentence a derivation writes: colour balance, a camera's matrix and a telescope read from the folder name (#1878).</summary>
        private const string LongestDeriveNote = "Gains derived for Saturn through a 406 mm pupil at 610 nm toward the aperture (telescope read from the "
            + "folder name 2026-10-07_Saturn_C14); balanced to Saturn's colour: gains R 1.005, B 0.887 over green through the Uranus-C's matrix, saturation 1.1";

        /// <summary>The painted rect of the popover's body, read from the region it registers rather than measured again.</summary>
        private static RectF32 PanelRect(PopoverViewer viewer)
        {
            foreach (var region in viewer.GetRegisteredRegions())
            {
                if (region.Result is HitResult.ButtonHit { Action: "EnhancePanelBackground" })
                {
                    return new RectF32(region.X, region.Y, region.Width, region.Height);
                }
            }
            throw new InvalidOperationException("the Enhance popover registered no body");
        }

        [Theory]
        [InlineData(1f, 900u)]
        [InlineData(1f, 2400u)]
        [InlineData(1.5f, 900u)]
        [InlineData(1.5f, 2400u)]
        public async Task TheDerivationNoteWrapsInsideThePopoverInsteadOfWideningIt(float dpiScale, uint designWidth)
        {
            var ct = TestContext.Current.CancellationToken;

            async Task<(RectF32 Panel, float ContentBottom)> PanelWith(string? note)
            {
                using var renderer = new RgbaImageRenderer((uint)(designWidth * dpiScale), (uint)(TallH * dpiScale));
                var (viewer, state, _) = await OpenAsync(renderer, dpiScale, ct, s =>
                {
                    OnAStackedCapture(s);
                    s.WaveletDerived = true;
                    s.WaveletDeriveNote = note;
                });
                var panel = PanelRect(viewer);
                var bottom = 0f;
                foreach (var region in viewer.GetRegisteredRegions())
                {
                    if (region.Result is not HitResult.ButtonHit { Action: "EnhancePanelBackground" }
                        && region.X >= panel.X && region.X < panel.Right && region.Y >= panel.Y)
                    {
                        bottom = MathF.Max(bottom, region.Y + region.Height);
                    }
                }
                return (panel, bottom);
            }

            var bare = await PanelWith(null);
            var oneLine = await PanelWith("Gains derived.");
            var noted = await PanelWith(LongestDeriveNote);

            noted.Panel.Width.ShouldBe(bare.Panel.Width, 1f, "a sentence wraps inside the popover's width; it does not set it");
            noted.Panel.Height.ShouldBeGreaterThan(oneLine.Panel.Height + (2f * 10f * dpiScale), "the note spans more than one line");
            noted.ContentBottom.ShouldBeLessThanOrEqualTo(noted.Panel.Bottom + 0.5f, "the body is as tall as the wrapped note and the last dial need");
            noted.Panel.Bottom.ShouldBeLessThanOrEqualTo(TallH * dpiScale, "the popover stays on the window");
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task TheDeepSkyHalfKeepsItsOwnWidthUnderTheCap(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, _, _) = await OpenAsync(renderer, dpiScale, ct);
            PanelRect(viewer).Width.ShouldBeLessThanOrEqualTo((ImageRendererBase<RgbaImage>.EnhancePopoverMaxWidth * dpiScale) + 1f, "the deep-sky rows are narrower than the cap, so the cap changes nothing there");
        }

        [Fact]
        public void OneRuleSaysWhichHalfShows()
        {
            new ViewerState().EnhancesAsPlanet.ShouldBeFalse("a deep-sky frame");
            new ViewerState { ShowStacked = true }.EnhancesAsPlanet.ShouldBeTrue();
            new ViewerState { ShowAuto = true }.EnhancesAsPlanet.ShouldBeTrue();
            new ViewerState { SequencePath = "capture.ser" }.EnhancesAsPlanet.ShouldBeTrue();
            new ViewerState { MasterPlanet = CatalogIndex.Jupiter }.EnhancesAsPlanet.ShouldBeTrue();
        }

        /// <summary>The deep-sky half is lit while an enhance runs or is on show; the planetary half while its sharpening layer is on show.</summary>
        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task TheButtonIsLitWhileEitherHalfsEnhancementIsInForce(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, _, _) = await NewViewerAsync(renderer, dpiScale, ct);
            bool Lit(ViewerState s) => viewer.IsToolbarButtonActiveForTest(ToolbarAction.Enhance, s);

            // Deep sky: the AI enhance.
            Lit(new ViewerState()).ShouldBeFalse("nothing in force");
            Lit(new ViewerState { IsEnhancing = true }).ShouldBeTrue("an enhance running");
            Lit(new ViewerState { IsEnhanced = true }).ShouldBeTrue("an enhance on show");

            // Planet: the sharpening layer on show, never the AI enhance.
            Lit(new ViewerState { SequencePath = "capture.ser" }).ShouldBeFalse("the raw frames carry no sharpening");
            Lit(new ViewerState { ShowStacked = true }).ShouldBeTrue("the live stack is sharpened");
            Lit(new ViewerState { ShowAuto = true }).ShouldBeTrue("the Auto stack is sharpened");
            Lit(new ViewerState { MasterPlanet = CatalogIndex.Jupiter }).ShouldBeFalse("a master opens as the file holds it");
            Lit(new ViewerState { MasterPlanet = CatalogIndex.Jupiter, WaveletSharpenEnabled = true }).ShouldBeTrue("a master re-sharpened by its dials");
            Lit(new ViewerState { SequencePath = "capture.ser", IsEnhanced = true }).ShouldBeFalse("the AI enhance is not the planetary half's");
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task TheTooltipSaysWhichHalf(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = NewRenderer(dpiScale);
            var (viewer, _, _) = await NewViewerAsync(renderer, dpiScale, ct);
            string? Tip(ViewerState s) => viewer.ToolbarTooltipForTest(ToolbarAction.Enhance, s);

            Tip(new ViewerState()).ShouldNotBeNull().ShouldContain("program and its backend");
            Tip(new ViewerState { ShowStacked = true }).ShouldNotBeNull().ShouldContain("planetary sharpening is on");
            var off = Tip(new ViewerState { SequencePath = "capture.ser" }).ShouldNotBeNull();
            off.ShouldContain("planetary sharpening");
            off.ShouldNotContain("is on");
        }

        /// <summary>The backends offered come from what SERVES the frame, not from what is registered.</summary>
        [Fact]
        public async Task TheChoicesAreAskedOfWhatServesAndAutoIsAlwaysFirst()
        {
            var ct = TestContext.Current.CancellationToken;
            var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
            var pipeline = new SharpenPipeline(gradientCorrector: new PassThrough());

            var choices = EnhanceChoices.For(pipeline, document.UnstretchedImage);

            choices.Select(c => c.Backend).ShouldBe([EnhanceBackend.Auto], "no role serves under another backend either");
            choices[0].Steps.ShouldBe(["gradient-correction"], "the program is what CanonicalProgram builds from what serves");
        }

        private sealed class PassThrough : IGradientCorrector
        {
            public string Name => "Test/PassThrough";
            public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default) => Task.FromResult(input);
        }
    }
}
