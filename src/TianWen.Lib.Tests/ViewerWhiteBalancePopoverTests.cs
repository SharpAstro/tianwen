using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
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

            /// <summary>The R channel's dial, so a region can be recognised by the state it points at.</summary>
            public SliderState RedSlider => WhiteBalanceSliderState(0);
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
            UiRouting.Route(viewer,
                new InputEvent.MouseDown(x, y),
                new InputEvent.MouseUp(x, y));
        }

        /// <summary>
        /// Everything registered below the toolbar after a render: each button by name at its OWN
        /// painted rect, whether any white-balance slider band exists, and the right-hand end of the R
        /// track. Read straight back from <see cref="PixelWidgetBase{T}.GetRegisteredRegions"/> -- the
        /// arranged regions the render itself produced -- rather than found by sweeping pixels and
        /// calling <see cref="PixelWidgetBase{T}.HitTest"/> at each one.
        /// </summary>
        /// <remarks>
        /// This used to scan every 8th pixel across the WHOLE window below the button, because the
        /// popover's width is not a constant: it sizes itself to its MEASURED button row (425 px here at
        /// dpi 1, and whatever the host's own face measures elsewhere), and a box that would overhang the
        /// window is pulled LEFT by its placement clamp, the wider it is the further -- so a guessed
        /// column used to land past the first button on a host whose system face is wider than this
        /// one's, and "Auto" was reported missing from a panel that had drawn it perfectly well. Reading
        /// the registered regions directly needs no column guess at all: whatever the render placed, and
        /// wherever, is exactly what this reports. The 800-wide case below still exercises the same
        /// clamp, now by asserting on the regions it produces rather than by out-scanning it.
        /// </remarks>
        private static (Dictionary<string, (float X, float Y)> Buttons, bool Sliders, RectF32? RedTrack)
            HitsBelowTheBar(PopoverViewer viewer)
        {
            var button = Button(viewer);
            var buttons = new Dictionary<string, (float X, float Y)>();
            var sliders = false;
            RectF32? redTrack = null;
            foreach (var region in viewer.GetRegisteredRegions())
            {
                // Below the toolbar bar only, exactly as the old sweep's y range started just past it --
                // the toolbar's own buttons share the bar's Y and must not be picked up as popover ones.
                if (region.Y < button.Bottom)
                {
                    continue;
                }

                switch (region.Result)
                {
                    case HitResult.ButtonHit hit:
                        // Centre of the button's own rect: guaranteed inside it, unlike a sweep pixel
                        // that happened to land there at 8px granularity.
                        buttons.TryAdd(hit.Action, (region.X + region.Width / 2f, region.Y + region.Height / 2f));
                        break;
                    // A declared slider's hit carries the caller-owned state the leaf points AT, so
                    // "which channel" is identity rather than an index the panel and this test both
                    // have to spell the same way.
                    case HitResult.SliderStateHit slider:
                        sliders = true;
                        if (ReferenceEquals(slider.State, viewer.RedSlider))
                        {
                            // The whole rect, because a drag now goes through the real press path and
                            // needs a y as well as an x. The tests aim at the far END of the track:
                            // the middle of a log-mapped track is exactly 1.00, which would prove
                            // nothing about a drag having landed.
                            redTrack = new RectF32(region.X, region.Y, region.Width, region.Height);
                        }
                        break;
                }
            }
            return (buttons, sliders, redTrack);
        }

        [Fact]
        public async Task TheStripHasNoWhiteBalanceSectionAndTheBarHasTheButton()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, _, _) = await NewViewerAsync(renderer, ct);

            Button(viewer).Width.ShouldBeGreaterThan(0f);
            state.WhiteBalancePopover.IsOpen.ShouldBeFalse("closed until pressed");

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
            state.WhiteBalancePopover.IsOpen.ShouldBeTrue("the button opens it");
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

            // The rightmost edge of each region, read straight back from what the render registered
            // rather than found by sweeping pixels and asking HitTest at each one.
            float? panelRight = null;
            float? calibrateRight = null;
            foreach (var region in viewer.GetRegisteredRegions())
            {
                if (region.Result is not HitResult.ButtonHit hit)
                {
                    continue;
                }

                var right = region.X + region.Width;
                if (hit.Action == "WhiteBalancePanelBackground") { panelRight = right; }
                else if (hit.Action == "ToggleColorCalibration") { calibrateRight = right; }
            }

            panelRight.ShouldNotBeNull("the panel is open and registered");
            calibrateRight.ShouldNotBeNull("the calibration button is in the row");
            calibrateRight!.Value.ShouldBeLessThanOrEqualTo(panelRight!.Value,
                $"at dpi {dpiScale} the calibration button reaches {calibrateRight} and the panel ends at {panelRight}");
        }

        /// <summary>
        /// <b>Nothing in the popover is cut short.</b> The G of the channel column was drawn as a lone
        /// ellipsis, the column being sized to R, and the calibration line was trimmed to the box, which
        /// lost its white reference (2026-09-18). Every text leaf the popover paints must be at least as
        /// wide as its own text measures.
        /// </summary>
        /// <remarks>
        /// Measured in the face the viewer ships, DejaVu Sans. The harness default is the platform's
        /// MONOSPACE face, where G and R are one width, and this test passed against the R-wide column
        /// there until it was moved onto the proportional face.
        /// </remarks>
        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task NoTextInThePopoverIsCutShort(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer((uint)(WindowW * dpiScale), (uint)(WindowH * dpiScale));
            var (viewer, state, document, _) = await NewViewerAsync(renderer, ct);
            document.InheritColorCalibration((0.671f, 1f, 1.605f),
                new ColorCalibrationSummary("SPCC", 0.671f, 1f, 1.605f, 22, "Average spiral galaxy (SWIRE Sb)"));
            state.ColorCalibrationEnabled = true;
            var face = System.IO.Path.Combine(AppContext.BaseDirectory, "TestFonts", "DejaVuSans.ttf");
            System.IO.File.Exists(face).ShouldBeTrue("the viewer's own face is copied to the test output");
            viewer.FontPath = face;
            viewer.DpiScale = dpiScale;
            viewer.Render(document, state);

            var button = Button(viewer);
            Press(viewer, button.X + (button.Width / 2f), button.Y + (button.Height / 2f));
            viewer.Render(document, state);

            var nodes = new List<Layout.ArrangedNode<float>>();
            viewer.CollectPaintedNodes(nodes);
            var texts = new List<(string Value, float Measured, float Available)>();
            foreach (var arranged in nodes)
            {
                if (arranged.Node is not Layout.Node.Leaf { Content: Layout.Content.Text text } leaf
                    || arranged.Bounds.Y < button.Bottom || text.Value.Length == 0)
                {
                    continue;
                }

                var measured = renderer.MeasureText(text.Value, viewer.FontPath, text.FontSize * dpiScale).Width;
                texts.Add((text.Value, measured, arranged.Bounds.Width - (2f * leaf.Padding * dpiScale)));
            }

            texts.ShouldContain(t => t.Value == "G", "the channel column is painted");
            texts.ShouldContain(t => t.Value == "White: Average spiral galaxy (SWIRE Sb)", "the provenance is painted whole");
            foreach (var (value, measured, available) in texts)
            {
                measured.ShouldBeLessThanOrEqualTo(available + 0.5f,
                    $"'{value}' measures {measured:F1} at dpi {dpiScale} and was given {available:F1}");
            }
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

            state.WhiteBalancePopover.IsOpen.ShouldBeTrue("a press on the dim button does not fall through and close the panel");
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

            state.WhiteBalancePopover.IsOpen.ShouldBeTrue("the press does not fall through to the backdrop");
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
            var (_, _, redTrack) = HitsBelowTheBar(viewer);
            redTrack.ShouldNotBeNull("the R track is registered while the popover is open");
            // Through the real press path: a declared slider arms its own drag, so there is no
            // BeginWhiteBalanceDragAt to call and the press itself is what moves the value.
            var dragX = redTrack.Value.Right - 1f;
            var dragY = redTrack.Value.Y + (redTrack.Value.Height / 2f);
            UiRouting.Route(viewer,
                new InputEvent.MouseDown(dragX, dragY),
                new InputEvent.MouseUp(dragX, dragY));
            state.ManualWhiteBalance.R.ShouldNotBe(1f, "the track took the drag");
            viewer.IsToolbarButtonActiveForTest(ToolbarAction.WhiteBalance, state)
                .ShouldBeTrue("a white balance in force lights the button");

            viewer.Render(document, state);
            var (buttons, _, _) = HitsBelowTheBar(viewer);
            var reset = buttons["ResetWhiteBalance"];
            UiRouting.RoutePress(viewer, reset.X, reset.Y);
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
            var (_, openSliders, redTrack) = HitsBelowTheBar(viewer);
            openSliders.ShouldBeTrue("the track is there to begin with");
            redTrack.ShouldNotBeNull();
            var dragX = redTrack.Value.Right - 1f;
            var dragY = redTrack.Value.Y + (redTrack.Value.Height / 2f);

            // Routed, because that is how every host sends a key -- see UiRouting.
            UiRouting.RouteKey(viewer, InputKey.Escape);

            state.WhiteBalancePopover.IsOpen.ShouldBeFalse("Escape closes it");
            exits().ShouldBe(0, "and nothing asked to exit");

            // Closed, it registers nothing and a drag where the R track was does nothing.
            viewer.Render(document, state);
            var (_, sliders, _) = HitsBelowTheBar(viewer);
            sliders.ShouldBeFalse();
            var before = state.ManualWhiteBalance;
            UiRouting.Route(viewer,
                new InputEvent.MouseDown(dragX, dragY),
                new InputEvent.MouseUp(dragX, dragY));
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
            state.WhiteBalancePopover.IsOpen.ShouldBeTrue();

            var area = viewer.ImageArea;
            Press(viewer, area.X + (area.Width * 0.8f), area.Y + (area.Height * 0.8f));
            state.WhiteBalancePopover.IsOpen.ShouldBeFalse("a press on the picture closes it");

            // A frame between presses, as there always is: the regions a press lands on are the
            // ones the last paint registered, and the closed popover has to paint as closed before
            // the button underneath its backdrop is reachable again.
            viewer.Render(document, state);
            Press(viewer, onButton.X, onButton.Y);
            viewer.Render(document, state);
            state.WhiteBalancePopover.IsOpen.ShouldBeTrue();

            Press(viewer, onButton.X, onButton.Y);
            state.WhiteBalancePopover.IsOpen.ShouldBeFalse("a second press on the button closes what the first opened");
        }
    }
}
