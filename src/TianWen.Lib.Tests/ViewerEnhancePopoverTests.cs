using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The sparkles button opens one Enhance popover (#1345, P36), and the deep-sky half holds the AI program: the
    /// backends that serve, run / cancel with its progress, and off.
    /// </summary>
    /// <remarks>
    /// Observed through the hit tracker, as <see cref="ViewerWhiteBalancePopoverTests"/> is: a popover that is open has
    /// registered its buttons, one that is closed has registered nothing. Run, Cancel and Off are asserted by what they
    /// PRESS (the Enhance button with no mouse button, which is what <c>E</c> does), because the toggle itself is the
    /// controller's and is pinned by <c>ViewerControllerTests</c>.
    /// </remarks>
    [Collection("UI")]
    public class ViewerEnhancePopoverTests
    {
        private const uint WindowW = 900;
        private const uint WindowH = 700;
        private const int ImageW = 8;
        private const int ImageH = 6;

        private sealed class PopoverViewer : ImageRendererBase<RgbaImage>
        {
            public PopoverViewer(RgbaImageRenderer renderer, SignalBus bus) : base(renderer)
            {
                Bus = bus;
                Width = renderer.Width;
                Height = renderer.Height;
                DpiScale = 1f;
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

        private static async Task<(PopoverViewer Viewer, ViewerState State, AstroImageDocument Document)>
            NewViewerAsync(RgbaImageRenderer renderer, CancellationToken ct)
        {
            var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
            var viewer = new PopoverViewer(renderer, new SignalBus())
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

        /// <summary>Every button registered below the bar, by name, at the centre of its own rect.</summary>
        private static Dictionary<string, (float X, float Y)> HitsBelowTheBar(PopoverViewer viewer)
        {
            var bottom = Button(viewer).Bottom;
            var buttons = new Dictionary<string, (float X, float Y)>();
            foreach (var region in viewer.GetRegisteredRegions())
            {
                // The statistics toggle sits below the bar whatever the popover does; only the popover's own rows count.
                if (region.Y >= bottom && region.Result is HitResult.ButtonHit hit && hit.Action.StartsWith("Enhance", StringComparison.Ordinal))
                {
                    buttons.TryAdd(hit.Action, (region.X + region.Width / 2f, region.Y + region.Height / 2f));
                }
            }
            return buttons;
        }

        private static async Task<(PopoverViewer Viewer, ViewerState State, List<(ToolbarAction Action, MouseButton Button)> Presses)>
            OpenAsync(RgbaImageRenderer renderer, CancellationToken ct, Action<ViewerState>? arrange = null)
        {
            var (viewer, state, document) = await NewViewerAsync(renderer, ct);
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

        [Fact]
        public async Task AClosedPopoverHasRegisteredNothing()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, _) = await NewViewerAsync(renderer, ct);
            state.EnhanceChoices = TwoChoices;

            state.EnhancePopover.IsOpen.ShouldBeFalse("closed until pressed");
            HitsBelowTheBar(viewer).Keys.ShouldBeEmpty("a closed popover registers no region, and the strip no control");
        }

        [Fact]
        public async Task PressingTheButtonOpensThePopoverAndAPressOutsideOrOnTheButtonCloses()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, presses) = await OpenAsync(renderer, ct);

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

        [Fact]
        public async Task EscapeClosesIt()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, _) = await OpenAsync(renderer, ct);

            UiRouting.RouteKey(viewer, InputKey.Escape);

            state.EnhancePopover.IsOpen.ShouldBeFalse();
        }

        [Fact]
        public async Task TheServingBackendsAreAButtonGroupAndChoosingOneSetsThePreference()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, _) = await OpenAsync(renderer, ct);

            var hits = HitsBelowTheBar(viewer);
            hits.Keys.ShouldContain("EnhanceBackendAuto");
            hits.Keys.ShouldContain("EnhanceBackendTianWen");
            hits.Keys.ShouldNotContain("EnhanceBackendForceRcAstro", "a backend nothing serves is not offered");

            var tianWen = hits["EnhanceBackendTianWen"];
            Press(viewer, tianWen.X, tianWen.Y);

            state.PreferredEnhanceBackend.ShouldBe(EnhanceBackend.TianWen);
            state.EnhancePopover.IsOpen.ShouldBeTrue("choosing a backend leaves the popover open");
        }

        [Fact]
        public async Task RunPressesTheSameToggleEDoes()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, _, presses) = await OpenAsync(renderer, ct);

            var run = HitsBelowTheBar(viewer)["EnhanceRun"];
            Press(viewer, run.X, run.Y);

            presses.ShouldBe([(ToolbarAction.Enhance, MouseButton.None)]);
        }

        [Fact]
        public async Task WhileRunningTheRowIsCancelWithTheProgressAndRunIsGone()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, _, presses) = await OpenAsync(renderer, ct, s =>
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

        [Fact]
        public async Task WhileEnhancedOffRevertsAndRunDoesNothing()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, _, presses) = await OpenAsync(renderer, ct, s => s.IsEnhanced = true);

            var hits = HitsBelowTheBar(viewer);
            // Registered even when it cannot act, so a press lands on it and does not fall through to the backdrop.
            Press(viewer, hits["EnhanceRun"].X, hits["EnhanceRun"].Y);
            presses.ShouldBeEmpty("a second run would stack a pass on the first one's output");

            Press(viewer, hits["EnhanceOff"].X, hits["EnhanceOff"].Y);
            presses.ShouldBe([(ToolbarAction.Enhance, MouseButton.None)]);
        }

        [Fact]
        public async Task EKeepsItsToggleAndNeverOpensThePopover()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document) = await NewViewerAsync(renderer, ct);
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

        [Fact]
        public async Task ARightPressOnTheButtonNoLongerCyclesTheBackend()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, _) = await NewViewerAsync(renderer, ct);

            var button = Button(viewer);
            UiRouting.Route(viewer,
                new InputEvent.MouseDown(button.X + 2f, button.Y + 2f, MouseButton.Right),
                new InputEvent.MouseUp(button.X + 2f, button.Y + 2f, MouseButton.Right));

            state.PreferredEnhanceBackend.ShouldBe(EnhanceBackend.Auto);
        }

        [Fact]
        public async Task AFrameOnAPlanetsWayShowsTheOtherHalfAndNoAiControls()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, _) = await OpenAsync(renderer, ct, s => s.SequencePath = "capture.ser");

            state.EnhancesAsPlanet.ShouldBeTrue();
            var hits = HitsBelowTheBar(viewer);
            hits.Keys.ShouldNotContain("EnhanceRun");
            hits.Keys.ShouldNotContain("EnhanceBackendAuto");
            hits.Keys.ShouldContain("EnhancePanelBackground", "the popover is open, with the planetary half's placeholder");
        }

        [Fact]
        public void OneRuleSaysWhichHalfShows()
        {
            new ViewerState().EnhancesAsPlanet.ShouldBeFalse("a deep-sky frame");
            new ViewerState { ShowStacked = true }.EnhancesAsPlanet.ShouldBeTrue();
            new ViewerState { ShowAuto = true }.EnhancesAsPlanet.ShouldBeTrue();
            new ViewerState { SequencePath = "capture.ser" }.EnhancesAsPlanet.ShouldBeTrue();
            new ViewerState { MasterPlanet = TianWen.Lib.Astrometry.Catalogs.CatalogIndex.Jupiter }.EnhancesAsPlanet.ShouldBeTrue();
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
