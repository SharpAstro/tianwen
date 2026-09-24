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
    /// The file keys every desktop app has -- <c>Ctrl+O</c>, <c>Ctrl+S</c>, <c>Ctrl+Shift+S</c> -- and the
    /// display hold's default, which is what decides what a click in the file list shows.
    /// </summary>
    /// <remarks>
    /// The keys POST, as P and E do, because a file dialog needs the controller; so the assertion is on
    /// the bus, not on state. Rendered with a null document: none of this needs pixels.
    /// </remarks>
    [Collection("UI")]
    public class ViewerFileKeysTests
    {
        private sealed class KeysViewer : ImageRendererBase<RgbaImage>
        {
            public KeysViewer(RgbaImageRenderer renderer, SignalBus bus) : base(renderer)
            {
                Bus = bus;
                Width = renderer.Width;
                Height = renderer.Height;
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
        }

        private static (KeysViewer Viewer, ViewerState State, SignalBus Bus) NewViewer()
        {
            var bus = new SignalBus();
            var viewer = new KeysViewer(new RgbaImageRenderer(600, 400), bus);
            var state = new ViewerState();
            viewer.Render(null, state);
            return (viewer, state, bus);
        }

        /// <summary>A press and the bus pump behind it: a post is queued, not delivered, until the host drains it.</summary>
        private static void Press(KeysViewer viewer, SignalBus bus, InputKey key, InputModifier mods)
        {
            viewer.HandleInput(new InputEvent.KeyDown(key, mods));
            bus.ProcessPending();
        }

        /// <summary>
        /// <b>A click in the file list is "show me this frame", and gets its own auto-stretch.</b> The hold
        /// shipped on, and on a night that left a light-pollution dome the first sub's curve made every
        /// later, darker-sky sub render its galaxy DIMMER -- the same numbers over a lower sky. A blink
        /// holds on its own; this flag is the explicit hold for stepping by hand, and it starts off.
        /// </summary>
        [Fact]
        public void TheDisplayIsNotHeldAcrossFramesByDefault()
        {
            new ViewerState().CarryDisplayAcrossFrames.ShouldBeFalse();
        }

        [Fact]
        public void CtrlO_AsksForTheFileDialog()
        {
            var (viewer, state, bus) = NewViewer();
            var opens = 0;
            bus.Subscribe<OpenFileSignal>(_ => opens++);

            Press(viewer, bus, InputKey.O, InputModifier.Ctrl);

            opens.ShouldBe(1);
        }

        /// <summary>Plain O is the annotation toggle and must stay that: the file dialog is Ctrl's alone.</summary>
        [Fact]
        public void PlainO_DoesNotOpenAFile()
        {
            var (viewer, state, bus) = NewViewer();
            var opens = 0;
            bus.Subscribe<OpenFileSignal>(_ => opens++);

            Press(viewer, bus, InputKey.O, InputModifier.None);

            opens.ShouldBe(0);
        }

        /// <summary>
        /// Ctrl+S is the one-press save: the clean 16-bit raster, exactly what the Save button's
        /// right-click writes. The choice (overlays, depth) is a menu, and a menu is Ctrl+Shift+S.
        /// </summary>
        [Fact]
        public void CtrlS_SavesTheCleanSixteenBitRaster()
        {
            var (viewer, state, bus) = NewViewer();
            SaveImageSignal? posted = null;
            bus.Subscribe<SaveImageSignal>(sig => posted = sig);

            Press(viewer, bus, InputKey.S, InputModifier.Ctrl);

            posted.ShouldNotBeNull();
            posted.Value.WithOverlays.ShouldBeFalse();
            posted.Value.PngDepth.ShouldBe(PngDepth.SixteenBit);
        }

        /// <summary>Plain S toggles the star overlay, as it always has; it must not save.</summary>
        [Fact]
        public void PlainS_StillTogglesTheStarOverlay()
        {
            var (viewer, state, bus) = NewViewer();
            var saves = 0;
            bus.Subscribe<SaveImageSignal>(_ => saves++);
            var before = state.ShowStarOverlay;

            Press(viewer, bus, InputKey.S, InputModifier.None);

            saves.ShouldBe(0);
            state.ShowStarOverlay.ShouldBe(!before);
        }

        /// <summary>Ctrl+Shift+S is the menu, not a save: nothing is written until a row is chosen.</summary>
        [Fact]
        public void CtrlShiftS_DoesNotSaveOnItsOwn()
        {
            var (viewer, state, bus) = NewViewer();
            var saves = 0;
            bus.Subscribe<SaveImageSignal>(_ => saves++);

            Press(viewer, bus, InputKey.S, InputModifier.Ctrl | InputModifier.Shift);

            saves.ShouldBe(0);
        }
        /// <summary>
        /// H is the histogram and Shift+H its log scale. H used to cycle the highlight soft clip, which
        /// is the Tone popover's and its button wheel's now, and V, the histogram's old key, is free.
        /// Neither half may touch the soft clip or the display hold (Ctrl+H).
        /// </summary>
        [Fact]
        public void H_TogglesTheHistogramAndShiftH_ItsLogScale()
        {
            var (viewer, state, bus) = NewViewer();
            var (shown, log, hdr) = (state.ShowHistogram, state.HistogramLogScale, state.HdrPresetIndex);

            Press(viewer, bus, InputKey.H, InputModifier.None);
            state.ShowHistogram.ShouldBe(!shown);
            state.HistogramLogScale.ShouldBe(log);

            Press(viewer, bus, InputKey.H, InputModifier.Shift);
            state.HistogramLogScale.ShouldBe(!log);
            state.ShowHistogram.ShouldBe(!shown, "Shift+H is the scale, not the overlay");

            state.HdrPresetIndex.ShouldBe(hdr, "H no longer steps the soft clip");
            state.CarryDisplayAcrossFrames.ShouldBeFalse("neither is the hold, which is Ctrl+H");

            Press(viewer, bus, InputKey.V, InputModifier.None);
            state.ShowHistogram.ShouldBe(!shown, "V is free: it no longer toggles the histogram");
        }

        [Fact]
        public void CtrlH_TogglesTheHold()
        {
            var (viewer, state, bus) = NewViewer();

            Press(viewer, bus, InputKey.H, InputModifier.Ctrl);
            state.CarryDisplayAcrossFrames.ShouldBeTrue();

            Press(viewer, bus, InputKey.H, InputModifier.Ctrl);
            state.CarryDisplayAcrossFrames.ShouldBeFalse();
        }
    }
}
