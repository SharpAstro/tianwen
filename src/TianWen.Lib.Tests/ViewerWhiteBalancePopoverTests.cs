using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The white-balance sliders live in a popover under a toolbar button, not in the info strip.
    /// </summary>
    /// <remarks>
    /// <para>The user's design: "a new button with three colour circles ... that opens a menu with
    /// those three sliders (and a reset)", lit "if wb is not 1/1/1". The strip's section is gone; the
    /// button is a mark-only entry in the toolbar's colour group; the popover behaves as a menu --
    /// Escape closes it, a press anywhere else closes it, and while it is open it owns the pointer.</para>
    /// <para><b>Observed through the hit tracker.</b> A popover that is open has registered its slider
    /// bands and its buttons; one that is closed has registered nothing, and a drag where a track was
    /// does nothing -- the rule that a hidden widget consumes no input.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerWhiteBalancePopoverTests
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

            /// <summary>
            /// The window the hit scan covers. Exposed because <c>Width</c>/<c>Height</c> are protected
            /// on the base, and because a test that re-states the window size it asked for is one more
            /// place for an assumption to hide -- which is exactly what went wrong with the panel width.
            /// </summary>
            public (float W, float H) WindowSize => (Width, Height);
        }

        private static async Task<(PopoverViewer Viewer, ViewerState State, AstroImageDocument Document, Func<int> Exits)>
            NewViewerAsync(RgbaImageRenderer renderer, CancellationToken ct)
        {
            var bus = new SignalBus();
            var exits = 0;
            bus.Subscribe<RequestExitSignal>(_ => exits++);

            var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
            var viewer = new PopoverViewer(renderer, bus);
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
            return (viewer, state, document, () => { bus.ProcessPending(); return exits; });
        }

        /// <summary>The white-balance button's painted rect, which the popover hangs from.</summary>
        private static RectF32 Button(PopoverViewer viewer)
        {
            viewer.TryGetPaintedToolbarRect(ToolbarAction.WhiteBalance, out var rect)
                .ShouldBeTrue("the button is on the bar for a colour source");
            return rect;
        }

        private static void Press(PopoverViewer viewer, float x, float y)
        {
            viewer.HandleInput(new InputEvent.MouseDown(x, y));
            viewer.HandleInput(new InputEvent.MouseUp(x, y));
        }

        /// <summary>
        /// Everything registered below the toolbar after a render: each button by name at the point it
        /// was found, whether any white-balance slider band exists, and the right-hand end of the R
        /// track. <see cref="PixelWidgetBase{T}.HitTest"/> looks without dispatching, so the scan
        /// changes nothing.
        /// </summary>
        /// <remarks>
        /// <para><b>Scanned across the WHOLE window, because the popover's width is not a constant.</b>
        /// This used to scan 300 design pixels rightwards from a left edge it worked out by passing
        /// 300f to the same clamp the panel uses -- an answer that agrees with the panel only while the
        /// panel really is 300 wide. It is not, and since the panel began sizing itself to its MEASURED
        /// button row it is not even a constant: 425 px here at dpi 1, and whatever the host's own face
        /// measures elsewhere.</para>
        /// <para>The clamp is what turns that into a failure rather than a near miss. A box that would
        /// overhang the window is pulled LEFT, and the wider it is the further -- so on a host whose
        /// system face is wider than this one's, the panel sat left of where the guess said, the scan
        /// started past the first button, and "Auto" was reported missing from a panel that had drawn
        /// it perfectly well. It reproduces here by narrowing the window until the same clamp bites,
        /// which is what the 800-wide case below is for.</para>
        /// </remarks>
        private static (Dictionary<string, (float X, float Y)> Buttons, bool Sliders, float? RedTrackRight)
            HitsBelowTheBar(PopoverViewer viewer)
        {
            var button = Button(viewer);
            var (windowW, windowH) = viewer.WindowSize;
            var buttons = new Dictionary<string, (float X, float Y)>();
            var sliders = false;
            float? redTrackRight = null;
            // Every eighth pixel across the window, so a button is found wherever the face happens to
            // have put it, and each is remembered at the point it was found -- the point a press is
            // then sent to. Guessing a column landed a press in the gap between two buttons.
            for (var y = button.Bottom + 1f; y < windowH; y += 1f)
            {
                for (var x = 0f; x < windowW; x += 8f)
                {
                    switch (viewer.HitTest(x, y))
                    {
                        case HitResult.ButtonHit hit:
                            buttons.TryAdd(hit.Action, (x, y));
                            break;
                        case WhiteBalanceSliderHit { Channel: 0 }:
                            sliders = true;
                            // The far end of the track, not its middle: the drag tests need a point
                            // whose multiplier is unmistakably not neutral, and the middle of a
                            // log-mapped track is exactly 1.00.
                            if (x > (redTrackRight ?? float.MinValue)) { redTrackRight = x; }
                            break;
                        case WhiteBalanceSliderHit:
                            sliders = true;
                            break;
                    }
                }
            }
            return (buttons, sliders, redTrackRight);
        }

        [Fact]
        public async Task TheStripHasNoWhiteBalanceSectionAndTheBarHasTheButton()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, _, _) = await NewViewerAsync(renderer, ct);

            Button(viewer).Width.ShouldBeGreaterThan(0f);
            state.WhiteBalancePanelOpen.ShouldBeFalse("closed until pressed");

            var (buttons, sliders, _) = HitsBelowTheBar(viewer);
            buttons.ShouldNotContainKey("AutoWhiteBalance", "nothing of the white balance is registered while the popover is closed");
            buttons.ShouldNotContainKey("ToggleWhiteBalance", "and the strip no longer has a section for it");
            sliders.ShouldBeFalse();
        }

        /// <summary>
        /// <b>Every one of the popover's controls is reachable, at a window width that forces the
        /// placement clamp.</b>
        /// </summary>
        /// <remarks>
        /// The narrow case is the one that bit: the panel sizes itself to its measured button row, and
        /// a box that would overhang the window is pulled left, so on a narrow window -- or on any host
        /// whose face measures wider than this one's -- the panel is not under its button at all. At
        /// 800 the panel here is 425 px wide and clamped to x = 374, 213 px left of the button it hangs
        /// from, which is what a CI host with a wider system face was doing at 900 and what the old
        /// scan could not see past.
        /// </remarks>
        [Theory]
        [InlineData(900u)]
        [InlineData(800u)]
        public async Task PressingTheButtonOpensThePopoverWithItsSlidersAndButtons(uint windowW)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(windowW, WindowH);
            var (viewer, state, document, _) = await NewViewerAsync(renderer, ct);

            var button = Button(viewer);
            Press(viewer, button.X + (button.Width / 2f), button.Y + (button.Height / 2f));
            state.WhiteBalancePanelOpen.ShouldBeTrue("the button opens it");
            state.OverlayOwnsPointer.ShouldBeTrue("an open popover owns the pointer, like a dropdown");

            viewer.Render(document, state);
            var (buttons, sliders, _) = HitsBelowTheBar(viewer);
            buttons.ShouldContainKey("AutoWhiteBalance");
            buttons.ShouldContainKey("ResetWhiteBalance");
            sliders.ShouldBeTrue("the three tracks are registered");
        }

        /// <summary>
        /// <b>Every button stays inside the box.</b> The panel took its width from
        /// <c>BaseInfoPanelWidth</c> while the button row is MEASURED TEXT, so once a calibration
        /// landed and Reset became "Reset to calibrated" the row outgrew the panel and the calibration
        /// button hung out over the image -- reported as "spcc button is larger than the dropdown box".
        /// </summary>
        /// <remarks>
        /// Asserted at a DPI above 1 because that is where it showed: the row scales with the font
        /// while the old width was a constant times the same scale, so the two crossed over. No
        /// calibration is needed to provoke it any more, which is itself the fix -- both variable
        /// buttons now reserve their WIDEST label, so the row occupies the same width in every state
        /// and cannot shuffle its neighbours when toggled.
        /// </remarks>
        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        [InlineData(2f)]
        public async Task TheButtonRowFitsInsideThePanel(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            // The window scales with the DPI, or the toolbar runs out of room and stops painting the
            // white-balance button at all -- which is a different (and correct) behaviour that would
            // otherwise fail this test before it reached the popover.
            var windowW = (uint)(WindowW * dpiScale);
            var windowH = (uint)(WindowH * dpiScale);
            using var renderer = new RgbaImageRenderer(windowW, windowH);
            var (viewer, state, document, _) = await NewViewerAsync(renderer, ct);

            // The calibration button only REGISTERS when the frame has stars to fit against, and the
            // synthetic document has none. Only the count is consulted, so placeholders are enough --
            // this test is about where the button lands, not what a calibration would produce.
            var stars = new System.Collections.Concurrent.ConcurrentBag<ImagedStar>();
            for (var i = 0; i < 5; i++) { stars.Add(default); }
            document.Stars = new StarList(stars);

            viewer.DpiScale = dpiScale;
            viewer.Render(document, state);

            var button = Button(viewer);
            Press(viewer, button.X + (button.Width / 2f), button.Y + (button.Height / 2f));
            viewer.Render(document, state);

            // The rightmost pixel each region answers to, found by looking rather than by asking for a
            // rect: HitTest dispatches nothing, so the scan changes no state.
            var panelRight = float.MinValue;
            var calibrateRight = float.MinValue;
            for (var y = button.Bottom + 1f; y < windowH; y += 1f)
            {
                for (var x = 0f; x < windowW; x += 1f)
                {
                    if (viewer.HitTest(x, y) is HitResult.ButtonHit hit)
                    {
                        if (hit.Action == "WhiteBalancePanelBackground" && x > panelRight) { panelRight = x; }
                        else if (hit.Action == "ToggleColorCalibration" && x > calibrateRight) { calibrateRight = x; }
                    }
                }
            }

            panelRight.ShouldBeGreaterThan(0f, "the panel is open and registered");
            calibrateRight.ShouldBeGreaterThan(0f, "the calibration button is in the row");
            calibrateRight.ShouldBeLessThanOrEqualTo(panelRight,
                $"at dpi {dpiScale} the calibration button reaches {calibrateRight} and the panel ends at {panelRight}");
        }

        /// <summary>
        /// <b>Reset does nothing when there is nothing to reset to, and that is not merely cosmetic.</b>
        /// It also clears the triple parked when the calibration was switched on -- so pressing a
        /// button that looked inert used to quietly change what switching SPCC back off would restore.
        /// </summary>
        /// <remarks>
        /// The state is the one a user is in straight after calibrating: sliders reading the effective
        /// 1.44/1.00/1.23 while the MANUAL layer this button clears is already identity, because
        /// <c>SetColorCalibrationEnabled</c> sets it so when it turns the calibration on. The button is
        /// dim there now, and inert.
        /// </remarks>
        [Fact]
        public async Task ResetIsInertWhileTheManualLayerIsAlreadyIdentity()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerAsync(renderer, ct);

            // A manual adjustment the user made BEFORE calibrating, which switching the calibration on
            // parks and replaces with identity.
            state.ManualWhiteBalance = (1.2f, 1f, 1f);
            ViewerActions.SetColorCalibrationEnabled(state, true);
            state.ManualWhiteBalance.ShouldBe((1f, 1f, 1f), "switching it on sets the manual layer to identity");
            state.ManualWhiteBalanceBeforeCalibration.ShouldBe((1.2f, 1f, 1f), "and parks what was there");

            var button = Button(viewer);
            Press(viewer, button.X + (button.Width / 2f), button.Y + (button.Height / 2f));
            viewer.Render(document, state);

            var (buttons, _, _) = HitsBelowTheBar(viewer);
            buttons.ShouldContainKey("ResetWhiteBalance", "still registered when dim, so it swallows the press");
            var (rx, ry) = buttons["ResetWhiteBalance"];
            Press(viewer, rx, ry);

            state.WhiteBalancePanelOpen.ShouldBeTrue("a press on the dim button does not fall through and close the panel");
            state.ManualWhiteBalanceBeforeCalibration.ShouldBe((1.2f, 1f, 1f),
                "the parked triple survives, so switching the calibration off still restores it");

            ViewerActions.SetColorCalibrationEnabled(state, false);
            state.ManualWhiteBalance.ShouldBe((1.2f, 1f, 1f), "which is what switching it off gives back");
        }

        /// <summary>
        /// While a fit is running the calibration button refuses a press, and refusing is not the same
        /// as being absent: it keeps its region, so the press stops on the button instead of reaching
        /// the backdrop and closing the whole popover.
        /// </summary>
        [Fact]
        public async Task TheCalibrationButtonRefusesAPressWhileAFitIsRunning()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerAsync(renderer, ct);

            var stars = new System.Collections.Concurrent.ConcurrentBag<ImagedStar>();
            for (var i = 0; i < 5; i++) { stars.Add(default); }
            document.Stars = new StarList(stars);

            var button = Button(viewer);
            Press(viewer, button.X + (button.Width / 2f), button.Y + (button.Height / 2f));

            // Claim the in-flight slot the way the compute task does, without running one.
            document.TryBeginColorCalibration().ShouldBeTrue("nothing else holds it");
            viewer.Render(document, state);

            var (buttons, _, _) = HitsBelowTheBar(viewer);
            buttons.ShouldContainKey("ToggleColorCalibration", "still registered while busy, so it swallows the press");

            var wasEnabled = state.ColorCalibrationEnabled;
            var (cx, cy) = buttons["ToggleColorCalibration"];
            Press(viewer, cx, cy);

            state.WhiteBalancePanelOpen.ShouldBeTrue("the press does not fall through to the backdrop");
            state.ColorCalibrationEnabled.ShouldBe(wasEnabled, "and starts no second fit, nor toggles mid-fit");
        }

        /// <summary>
        /// A drag on the R track moves the manual factor, and the button lights: it is lit whenever
        /// the effective white balance is not neutral, and not otherwise.
        /// </summary>
        [Fact]
        public async Task ADragLightsTheButtonAndResetPutsItOut()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerAsync(renderer, ct);

            viewer.IsToolbarButtonActiveForTest(ToolbarAction.WhiteBalance, state)
                .ShouldBeFalse("neutral is unlit");

            var button = Button(viewer);
            Press(viewer, button.X + (button.Width / 2f), button.Y + (button.Height / 2f));
            viewer.Render(document, state);

            // Out at the right end of the R track, found by looking rather than by offsetting from an
            // assumed panel edge -- the assumption this suite used to make and that a wider face broke.
            var (_, _, redTrackRight) = HitsBelowTheBar(viewer);
            redTrackRight.ShouldNotBeNull("the R track is registered while the popover is open");
            var dragX = redTrackRight.Value;
            viewer.BeginWhiteBalanceDragAt(0, dragX);
            viewer.HandleInput(new InputEvent.MouseUp(dragX, button.Bottom + 40f));
            state.ManualWhiteBalance.R.ShouldNotBe(1f, "the track took the drag");
            viewer.IsToolbarButtonActiveForTest(ToolbarAction.WhiteBalance, state)
                .ShouldBeTrue("a white balance in force lights the button");

            viewer.Render(document, state);
            var (buttons, _, _) = HitsBelowTheBar(viewer);
            var reset = buttons["ResetWhiteBalance"];
            viewer.HitTestAndDispatch(reset.X, reset.Y);
            state.ManualWhiteBalance.ShouldBe((1f, 1f, 1f));
            viewer.IsToolbarButtonActiveForTest(ToolbarAction.WhiteBalance, state)
                .ShouldBeFalse("reset puts it out");
        }

        /// <summary>Escape closes the popover and does NOT quit: the claimant takes the key first.</summary>
        [Fact]
        public async Task EscapeClosesThePopoverRatherThanQuitting()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, exits) = await NewViewerAsync(renderer, ct);

            var button = Button(viewer);
            Press(viewer, button.X + (button.Width / 2f), button.Y + (button.Height / 2f));
            viewer.Render(document, state);

            // Where the R track is WHILE IT IS OPEN, so the drag below is aimed at the exact point
            // that worked a moment ago rather than at a guess -- which is what makes the closed case
            // evidence of anything.
            var (_, openSliders, redTrackRight) = HitsBelowTheBar(viewer);
            openSliders.ShouldBeTrue("the track is there to begin with");
            redTrackRight.ShouldNotBeNull();
            var dragX = redTrackRight.Value;

            viewer.HandleInput(new InputEvent.KeyDown(InputKey.Escape));

            state.WhiteBalancePanelOpen.ShouldBeFalse("Escape closes it");
            exits().ShouldBe(0, "and nothing asked to exit");

            // Closed, it registers nothing and a drag where the R track was does nothing.
            viewer.Render(document, state);
            var (_, sliders, _) = HitsBelowTheBar(viewer);
            sliders.ShouldBeFalse();
            var before = state.ManualWhiteBalance;
            viewer.BeginWhiteBalanceDragAt(0, dragX);
            state.ManualWhiteBalance.ShouldBe(before, "a closed popover has no track to drag");
        }

        /// <summary>A press anywhere else closes it -- on the picture, and on the button that opened it.</summary>
        [Fact]
        public async Task APressAnywhereElseClosesIt()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerAsync(renderer, ct);

            var button = Button(viewer);
            var onButton = (X: button.X + (button.Width / 2f), Y: button.Y + (button.Height / 2f));

            Press(viewer, onButton.X, onButton.Y);
            viewer.Render(document, state);
            state.WhiteBalancePanelOpen.ShouldBeTrue();

            var area = viewer.ImageArea;
            Press(viewer, area.X + (area.Width * 0.8f), area.Y + (area.Height * 0.8f));
            state.WhiteBalancePanelOpen.ShouldBeFalse("a press on the picture closes it");

            // A frame between presses, as there always is: the regions a press lands on are the
            // ones the last paint registered, and the closed popover has to paint as closed before
            // the button underneath its backdrop is reachable again.
            viewer.Render(document, state);
            Press(viewer, onButton.X, onButton.Y);
            viewer.Render(document, state);
            state.WhiteBalancePanelOpen.ShouldBeTrue();

            Press(viewer, onButton.X, onButton.Y);
            state.WhiteBalancePanelOpen.ShouldBeFalse("a second press on the button closes what the first opened");
        }
    }
}
