using System;
using System.Collections.Immutable;
using System.Linq;
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
    /// <para>This is the one key where not consuming an event is destructive rather than merely wrong:
    /// Escape is bound to Quit, so an overlay that fails to absorb it discards the loaded folder and
    /// every display setting instead of closing itself.</para>
    /// <para><b>The dropdown claims the keyboard AS IT PAINTS</b>, which is why every case here opens it
    /// and then renders. An earlier version of this file set <c>IsOpen</c> and sent the key without a
    /// render, which skips the claim entirely and so tested a state the viewer is never in: it passed
    /// against a hand-written special case in the Escape branch and would have passed with the real
    /// mechanism deleted. The claim is the thing worth pinning, so these drive it.</para>
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

            /// <summary>Who owns the keyboard, per the shared per-window settings.</summary>
            public IKeyboardClaimant? ClaimantForTest => Ui.KeyboardClaimant;
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

        /// <summary>
        /// Opens a panel the way the toolbar does, and paints it, which is what makes it the keyboard
        /// claimant. Opening without painting leaves the claim unset.
        /// </summary>
        private static void OpenAPanel(EscapeViewer viewer, ViewerState state)
        {
            var items = new[] { "one", "two", "three" }.Select(DropdownItem.Text).ToImmutableArray();
            state.ToolbarDropdown.Open(20f, 40f, 200f, items);
            viewer.Render(null, state);
        }

        /// <summary>An open panel absorbs the key: it closes, and nothing asks to exit.</summary>
        [Fact]
        public void EscapeClosesAnOpenPanelRatherThanQuitting()
        {
            var (viewer, state, bus, exits) = NewViewer();
            viewer.Render(null, state);

            OpenAPanel(viewer, state);
            viewer.ClaimantForTest.ShouldNotBeNull("painting the menu is what claims the keyboard");

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

            OpenAPanel(viewer, state);
            Escape(viewer, bus);
            exits().ShouldBe(0);

            // The closed dropdown now DECLINES the key rather than being cleared, which is what makes a
            // stale claim harmless, so the second press falls through to the exit.
            Escape(viewer, bus);
            exits().ShouldBe(1);
        }

        // --- a SELECTION is one of the things Escape dismisses ---

        private static ViewerObjectSelection Selected()
            => new ViewerObjectSelection("Lagoon Nebula", "NGC 6523", 18.06, -24.38, ["Lagoon Nebula"]);

        /// <summary>
        /// A selection is dismissed rather than the viewer quitting -- the same contract the dropdown
        /// has, and the one that makes Escape safe to press at all.
        /// </summary>
        /// <remarks>
        /// Unlike a dropdown, a selection is NOT a keyboard claimant: it paints no panel and owns no
        /// keys, so nothing else can absorb the press on its behalf and the Escape branch has to know
        /// about it. That is the difference this case exists to pin.
        /// </remarks>
        [Fact]
        public void EscapeClearsASelectionRatherThanQuitting()
        {
            var (viewer, state, bus, exits) = NewViewer();
            viewer.Render(null, state);
            state.SelectedObject = Selected();

            Escape(viewer, bus);

            state.SelectedObject.ShouldBeNull();
            exits().ShouldBe(0);

            // And the second press exits, exactly as it does after a dropdown.
            Escape(viewer, bus);
            exits().ShouldBe(1);
        }

        /// <summary>
        /// With BOTH open, the panel goes first: it claimed the keyboard by painting, so the press
        /// never reaches the selection.
        /// </summary>
        /// <remarks>
        /// The ordering matters because the panel is the thing in front of the reader. Getting it the
        /// other way round would clear a selection the user cannot even see past the open menu, and
        /// leave the menu open.
        /// </remarks>
        [Fact]
        public void AnOpenPanelIsDismissedBeforeTheSelection()
        {
            var (viewer, state, bus, exits) = NewViewer();
            viewer.Render(null, state);
            state.SelectedObject = Selected();

            OpenAPanel(viewer, state);
            Escape(viewer, bus);

            state.ToolbarDropdown.IsOpen.ShouldBeFalse();
            state.SelectedObject.ShouldNotBeNull("the panel absorbed the key, so the selection stands");
            exits().ShouldBe(0);
        }
    }
}
