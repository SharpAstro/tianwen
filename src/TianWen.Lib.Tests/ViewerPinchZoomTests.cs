using System;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// A two-finger pinch on a touchscreen must zoom the image.
    /// </summary>
    /// <remarks>
    /// <para>Reported as "pinch zoom doesn't work". Nothing was broken in the gesture recognition -- the
    /// renderer had been raising the events all along. The standalone viewer's host wired no
    /// <c>OnPinch</c> at all, and the shared viewer's input switch had no case for one, so both halves
    /// dropped it silently. That is why it looked like the touchscreen was not being read: a dropped
    /// event and an unread device are indistinguishable from the glass.</para>
    /// <para>The anchoring test re-derives the screen-to-image transform rather than reading it back
    /// through <c>UpdateCursorFromScreenPosition</c> (which needs a real document). It is asserting the
    /// invariant BETWEEN the two transforms -- the point under the fingers does not move -- which holds
    /// whatever the transform itself is, so the duplication cannot make it pass wrongly.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerPinchZoomTests
    {
        // Square and chrome-free, so the image pane is large and the image at the test zoom covers it on
        // both axes: the viewport confinement clamp then never moves the pan, and a pan that DID move is
        // therefore the gesture's doing and not the clamp's.
        private const uint WindowW = 600;
        private const uint WindowH = 600;
        private const int ImageW = 400;
        private const int ImageH = 300;

        private sealed class PinchViewer : ImageRendererBase<RgbaImage>
        {
            public PinchViewer(RgbaImageRenderer renderer)
                : base(renderer)
            {
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

        private static ViewerState NewState() => new ViewerState
        {
            ShowFileList = false,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
            // An explicit magnification, not Fit: the image then covers the pane on both axes (see the
            // window constants above) and Zoom is a number the assertions can multiply.
            ZoomToFit = false,
            Zoom = 4f,
        };

        private static PinchViewer NewViewer(RgbaImageRenderer renderer)
        {
            var viewer = new PinchViewer(renderer);
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);
            return viewer;
        }

        /// <summary>
        /// The viewer's screen-to-image mapping, as <c>ImageRendererBase.Layout</c> and
        /// <c>ViewerActions.UpdateCursorFromScreenPosition</c> both spell it: centred, then panned, then
        /// scaled. Fractional on purpose -- a sub-pixel drift is the failure this has to be able to see.
        /// </summary>
        private static (float X, float Y) ScreenToImage(RectF32 area, ViewerState state, float px, float py)
        {
            var offsetX = area.X + (area.Width - ImageW * state.Zoom) / 2f + state.PanOffset.X;
            var offsetY = area.Y + (area.Height - ImageH * state.Zoom) / 2f + state.PanOffset.Y;
            return ((px - offsetX) / state.Zoom, (py - offsetY) / state.Zoom);
        }

        private static InputEvent.Pinch Pinch(float scale, float x, float y,
            PinchSource source = PinchSource.Touchscreen)
            => new InputEvent.Pinch(scale, x, y) { Source = source };

        /// <summary>The gesture reaches the zoom at all -- the bug, in one line.</summary>
        [Theory]
        [InlineData(1.5f)]   // spread
        [InlineData(0.75f)]  // squeeze
        public void APinchZoomsTheImage(float scale)
        {
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Render(null, state);

            var area = viewer.ImageArea;
            var before = state.Zoom;

            viewer.HandleInput(Pinch(scale, area.X + area.Width * 0.5f, area.Y + area.Height * 0.5f))
                .ShouldBeTrue();

            state.Zoom.ShouldBe(before * scale, tolerance: 1e-4);
        }

        /// <summary>
        /// The scale is per EVENT, not cumulative from the start of the gesture: the renderer re-bases
        /// its reference distance after every dispatch. Two 1.5x events are therefore 2.25x, and reading
        /// them as absolute would leave the zoom stuck at 1.5x for the whole pinch.
        /// </summary>
        [Fact]
        public void SuccessivePinchEventsCompound()
        {
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Render(null, state);

            var area = viewer.ImageArea;
            var cx = area.X + area.Width * 0.5f;
            var cy = area.Y + area.Height * 0.5f;
            var before = state.Zoom;

            viewer.HandleInput(Pinch(1.5f, cx, cy));
            viewer.HandleInput(Pinch(1.5f, cx, cy));

            state.Zoom.ShouldBe(before * 1.5f * 1.5f, tolerance: 1e-3);
        }

        /// <summary>
        /// The image point between the fingers stays between the fingers. An off-centre anchor is used
        /// deliberately: a centre-anchored zoom would satisfy a centre-anchored assertion by accident.
        /// </summary>
        [Fact]
        public void APinchKeepsThePointUnderTheFingersFixed()
        {
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Render(null, state);

            var area = viewer.ImageArea;
            var anchorX = area.X + area.Width * 0.25f;
            var anchorY = area.Y + area.Height * 0.7f;

            var (imgXBefore, imgYBefore) = ScreenToImage(area, state, anchorX, anchorY);

            viewer.HandleInput(Pinch(1.4f, anchorX, anchorY)).ShouldBeTrue();
            viewer.Render(null, state); // let the layout apply its confinement clamp, if any

            var (imgXAfter, imgYAfter) = ScreenToImage(viewer.ImageArea, state, anchorX, anchorY);

            imgXAfter.ShouldBe(imgXBefore, tolerance: 0.5);
            imgYAfter.ShouldBe(imgYBefore, tolerance: 0.5);

            // And it really was anchored rather than centred: an off-centre anchor has to move the pan.
            state.PanOffset.ShouldNotBe((0f, 0f));
        }

        /// <summary>
        /// A TOUCHPAD pinch is left alone. Windows fires both the finger events and a mouse wheel for one
        /// touchpad pinch, and the wheel is the path this viewer has always zoomed on -- so acting on the
        /// finger events too would zoom twice per gesture.
        /// </summary>
        [Fact]
        public void ATouchpadPinchIsLeftToTheWheel()
        {
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Render(null, state);

            var area = viewer.ImageArea;
            var before = state.Zoom;

            viewer.HandleInput(Pinch(1.5f, area.X + area.Width * 0.5f, area.Y + area.Height * 0.5f,
                PinchSource.Touchpad)).ShouldBeFalse();

            state.Zoom.ShouldBe(before);
        }

        /// <summary>
        /// A midpoint outside the image pane is not a gesture on the image, exactly as for the wheel.
        /// </summary>
        [Fact]
        public void APinchOutsideTheImagePaneDoesNotZoom()
        {
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Render(null, state);

            var area = viewer.ImageArea;
            var before = state.Zoom;

            // Above the pane: the toolbar band, which the pane's top edge is below.
            viewer.HandleInput(Pinch(1.5f, area.X + area.Width * 0.5f, area.Y - 1f)).ShouldBeFalse();

            state.Zoom.ShouldBe(before);
        }

        /// <summary>
        /// The gesture must not also PAN. A touchscreen synthesizes mouse events from the first finger,
        /// so the press that opened the pinch reads as a press-and-drag: without the suppression the
        /// image is dragged by one finger while the same gesture zooms it, which is a runaway rather than
        /// a zoom.
        /// </summary>
        [Fact]
        public void ThePinchDoesNotAlsoPanWithTheFirstFinger()
        {
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Render(null, state);

            var area = viewer.ImageArea;
            var cx = area.X + area.Width * 0.5f;
            var cy = area.Y + area.Height * 0.5f;

            // Finger one lands (synthesized press), then finger two joins and both move.
            viewer.HandleInput(new InputEvent.MouseDown(cx, cy, MouseButton.Left));
            viewer.HandleInput(Pinch(1.2f, cx, cy));

            var panAfterZoom = state.PanOffset;

            // The synthesized motion of finger one, arriving mid-pinch.
            viewer.HandleInput(new InputEvent.MouseMove(cx + 80f, cy + 60f));

            state.PanOffset.ShouldBe(panAfterZoom);
        }

        /// <summary>
        /// A press arriving mid-pinch must not arm a pan either, and the finger left on the glass when
        /// its partner lifts must not resume one from an anchor the zoom has since moved under. Panning
        /// resumes on the next fresh press.
        /// </summary>
        [Fact]
        public void PanningResumesOnlyOnAPressAfterThePinch()
        {
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Render(null, state);

            var area = viewer.ImageArea;
            var cx = area.X + area.Width * 0.5f;
            var cy = area.Y + area.Height * 0.5f;

            viewer.HandleInput(Pinch(1.2f, cx, cy));
            viewer.HandleInput(new InputEvent.MouseDown(cx, cy, MouseButton.Left)); // mid-pinch press
            viewer.HandleInput(new InputEvent.PinchEnd());

            var panAtPinchEnd = state.PanOffset;
            viewer.HandleInput(new InputEvent.MouseMove(cx + 40f, cy));
            state.PanOffset.ShouldBe(panAtPinchEnd, "the finger still down must not resume a pan");

            // A fresh press does arm one.
            viewer.HandleInput(new InputEvent.MouseDown(cx, cy, MouseButton.Left));
            viewer.HandleInput(new InputEvent.MouseMove(cx + 40f, cy));
            state.PanOffset.X.ShouldBeGreaterThan(panAtPinchEnd.X);
        }
    }
}
