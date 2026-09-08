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
    /// Escape dismisses what is open before it quits the viewer.
    /// </summary>
    /// <remarks>
    /// This is the one key where not consuming an event is destructive rather than merely wrong.
    /// Escape is bound to Quit, so the "?" panel failing to absorb it meant that pressing Escape to
    /// dismiss the panel closed the whole viewer, losing the loaded folder and every display setting
    /// with it. Reported 2026-09-08 after doing exactly that.
    /// </remarks>
    [Collection("UI")]
    public class ViewerEscapeTests
    {
        private sealed class EscapeViewer : ImageRendererBase<RgbaImage>
        {
            public EscapeViewer(RgbaImageRenderer renderer, SignalBus bus) : base(renderer)
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

        private static (EscapeViewer Viewer, ViewerState State, SignalBus Bus, Func<int> Exits) NewViewer()
        {
            var bus = new SignalBus();
            var exits = 0;
            bus.Subscribe<RequestExitSignal>(_ => exits++);

            var viewer = new EscapeViewer(new RgbaImageRenderer(600, 400), bus);
            var state = new ViewerState();
            return (viewer, state, bus, () => exits);
        }

        private static void Escape(EscapeViewer viewer, SignalBus bus)
        {
            viewer.HandleInput(new InputEvent.KeyDown(InputKey.Escape));
            bus.ProcessPending();
        }

        /// <summary>An open panel absorbs the key: it closes, and nothing asks to exit.</summary>
        [Fact]
        public void EscapeClosesAnOpenPanelRatherThanQuitting()
        {
            var (viewer, state, bus, exits) = NewViewer();
            viewer.Render(null, state);

            state.ToolbarDropdown.IsOpen = true;
            Escape(viewer, bus);

            state.ToolbarDropdown.IsOpen.ShouldBeFalse();
            exits().ShouldBe(0);
        }

        /// <summary>
        /// And with nothing open it still quits, which is the half that must not be lost: the fix is
        /// "dismiss first", not "stop quitting".
        /// </summary>
        [Fact]
        public void EscapeWithNothingOpenStillAsksToExit()
        {
            var (viewer, state, bus, exits) = NewViewer();
            viewer.Render(null, state);

            state.ToolbarDropdown.IsOpen.ShouldBeFalse();
            Escape(viewer, bus);

            exits().ShouldBe(1);
        }

        /// <summary>
        /// Two presses: the first dismisses, the second exits. This is what the user actually does, and
        /// it is the sequence a fix that swallowed Escape unconditionally would break.
        /// </summary>
        [Fact]
        public void TheSecondEscapeExits()
        {
            var (viewer, state, bus, exits) = NewViewer();
            viewer.Render(null, state);

            state.ToolbarDropdown.IsOpen = true;
            Escape(viewer, bus);
            exits().ShouldBe(0);

            Escape(viewer, bus);
            exits().ShouldBe(1);
        }
    }
}
