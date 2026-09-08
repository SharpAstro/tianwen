using System;
using System.Collections.Generic;
using System.IO;
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
    /// The blink transport as a HELD key, and the file list as the thing that says which frame is on
    /// screen. Both reported 2026-09-07: docs/plans/viewer-prerelease-fixes.md P23.
    /// </summary>
    /// <remarks>
    /// <para>Neither defect was in the blink. One is a fact the host dropped (an OS auto-repeat is not a
    /// new press, and a toggle driven by repeats flips at the repeat rate), and the other is a call the
    /// viewer's file list never made while every other list in the app made it.</para>
    /// <para>Rendered with a null document, because none of this needs pixels: the key path and the
    /// scroll controller both work off <see cref="ViewerState"/> and the arranged band.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerBlinkTransportTests
    {
        private const uint SurfaceW = 900;
        private const uint SurfaceH = 700;

        private sealed class BlinkViewer : ImageRendererBase<RgbaImage>
        {
            public BlinkViewer(RgbaImageRenderer renderer) : base(renderer)
            {
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

            public int FirstVisibleRow => FileListFirstVisibleRow;
        }

        private static (BlinkViewer Viewer, ViewerState State) NewViewer(int fileCount)
        {
            var viewer = new BlinkViewer(new RgbaImageRenderer(SurfaceW, SurfaceH));
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, 400, 300);

            var state = new ViewerState
            {
                ShowFileList = true,
                ShowInfoPanel = false,
                ShowHistogram = false,
                StretchMode = StretchMode.None,
                CurrentFolder = Path.Combine("folder", "of", "lights"),
                ImageFileNames = Enumerable.Range(0, fileCount).Select(i => $"light_{i:D4}.fits").ToList(),
                SelectedFileIndex = 0,
            };

            viewer.Render(null, state);
            return (viewer, state);
        }

        private static void Press(BlinkViewer viewer, InputKey key, bool repeat = false)
            => viewer.HandleInput(new InputEvent.KeyDown(key) { Repeat = repeat });

        private static void Release(BlinkViewer viewer, InputKey key)
            => viewer.HandleInput(new InputEvent.KeyUp(key));

        /// <summary>A held key as the platform actually delivers it: one press, then repeats.</summary>
        private static void Hold(BlinkViewer viewer, InputKey key, int repeats = 3)
        {
            Press(viewer, key);
            for (var i = 0; i < repeats; i++)
            {
                Press(viewer, key, repeat: true);
            }
        }

        /// <summary>
        /// The reported symptom in one assertion: holding Space made the blink "start/stop rapidly". SDL
        /// sends a held key as a stream of KeyDown events, and the Space case toggles, so the blink flipped
        /// once per repeat. The press before and after the repeats is what keeps this honest: the key still
        /// has to work.
        /// </summary>
        [Fact]
        public void AHeldSpaceRunsTheBlinkOnce_BecauseARepeatIsNotANewPress()
        {
            var (viewer, state) = NewViewer(fileCount: 12);

            Press(viewer, InputKey.Space);
            state.IsBlinking.ShouldBeTrue("a fresh press starts the blink");

            for (var i = 0; i < 5; i++)
            {
                Press(viewer, InputKey.Space, repeat: true);
                state.IsBlinking.ShouldBeTrue($"repeat {i + 1} must not toggle the blink");
            }

            Press(viewer, InputKey.Space);
            state.IsBlinking.ShouldBeFalse("the next real press is the pause");
        }

        /// <summary>
        /// The other half of the same rule, and the reason it is not "ignore every repeat": a STEP is
        /// exactly what auto-repeat is for. Holding Down walks the folder, which is the gesture a blink
        /// automates, so it has to keep stepping.
        /// </summary>
        [Fact]
        public void AHeldStepKeyKeepsStepping()
        {
            var (viewer, state) = NewViewer(fileCount: 12);

            Press(viewer, InputKey.Down);
            state.SelectedFileIndex.ShouldBe(1);

            Press(viewer, InputKey.Down, repeat: true);
            state.SelectedFileIndex.ShouldBe(2, "a repeated step repeats the step");
        }

        /// <summary>
        /// A toggle that is not the blink, to say the rule is about the KIND of action rather than about
        /// Space. Shift+H holds or releases the display mapping a blink is measured against, and held down
        /// it used to flip the same way.
        /// </summary>
        [Fact]
        public void AHeldToggleThatIsNotSpace_AlsoActsOnce()
        {
            var (viewer, state) = NewViewer(fileCount: 12);
            var before = state.CarryDisplayAcrossFrames;

            viewer.HandleInput(new InputEvent.KeyDown(InputKey.H, InputModifier.Shift));
            state.CarryDisplayAcrossFrames.ShouldBe(!before);

            viewer.HandleInput(new InputEvent.KeyDown(InputKey.H, InputModifier.Shift) { Repeat = true });
            state.CarryDisplayAcrossFrames.ShouldBe(!before, "a repeat is the same press, still held");
        }

        /// <summary>
        /// A selection that moves without a click has to bring its row into view, or the list goes on
        /// showing rows the viewer is not displaying. The request is a one-shot, so the paint clears it:
        /// applied every frame it would drag the list back to the loaded file and the wheel could never
        /// leave it.
        /// </summary>
        [Fact]
        public void SelectingAFileAsksTheListToShowIt_Once()
        {
            var (viewer, state) = NewViewer(fileCount: 200);

            ViewerActions.SelectFile(state, 150);
            state.PendingFileListEnsureVisible.ShouldBe(150);

            viewer.Render(null, state);
            state.PendingFileListEnsureVisible.ShouldBeNull("the paint consumes the request");
        }

        /// <summary>
        /// End to end: the row a blink walked to is on screen afterwards. Asserted as the FIRST VISIBLE
        /// ROW moving, which is the only observable the scroll controller offers, and bounded above by the
        /// selection itself so a scroll that overshot the row would fail too.
        /// </summary>
        [Fact]
        public void TheListFollowsASelectionThatWalksOffTheVisibleRun()
        {
            var (viewer, state) = NewViewer(fileCount: 200);
            viewer.FirstVisibleRow.ShouldBe(0);

            ViewerActions.SelectFile(state, 150);
            viewer.Render(null, state);

            viewer.FirstVisibleRow.ShouldBeGreaterThan(0, "row 150 cannot be visible from the top of 200");
            viewer.FirstVisibleRow.ShouldBeLessThanOrEqualTo(150, "and the row itself must be at or below the top");
        }

        /// <summary>
        /// The counterpart, and the reason EnsureVisible is the right call rather than a re-centring one:
        /// stepping within what is already on screen must not move the list at all, or every blink tick
        /// scrolls under the eye that is trying to compare two frames.
        /// </summary>
        [Fact]
        public void AStepInsideTheVisibleRunDoesNotScroll()
        {
            var (viewer, state) = NewViewer(fileCount: 200);
            var before = viewer.FirstVisibleRow;

            ViewerActions.SelectFile(state, 1);
            viewer.Render(null, state);

            viewer.FirstVisibleRow.ShouldBe(before);
        }

        /// <summary>
        /// The other half of the reported note, and the half that needed a new event: "holding down space
        /// when we are blinking should pause it". Held, the blink stops for as long as the key is down and
        /// comes back on release.
        /// </summary>
        [Fact]
        public void AHeldSpaceSuspendsTheBlinkAndReleasingResumesIt()
        {
            var (viewer, state) = NewViewer(fileCount: 12);

            Press(viewer, InputKey.Space);
            state.IsBlinking.ShouldBeTrue("the first press starts it");

            Hold(viewer, InputKey.Space);
            state.IsBlinking.ShouldBeFalse("held, the blink is suspended rather than flipping per repeat");
            state.BlinkResumeOnRelease.ShouldBeTrue();

            Release(viewer, InputKey.Space);
            state.IsBlinking.ShouldBeTrue("releasing resumes what the hold suspended");
            state.BlinkResumeOnRelease.ShouldBeFalse();
        }

        /// <summary>
        /// A TAP still stops it, which is the gesture that existed before and the reason the hold is
        /// detected from the platform's repeat rather than from how long the key was down.
        /// </summary>
        /// <remarks>
        /// Without this the feature would be a regression dressed as a fix: making Space momentary
        /// outright would leave no way to stop a blink at all.
        /// </remarks>
        [Fact]
        public void ATappedSpaceStillStopsTheBlink()
        {
            var (viewer, state) = NewViewer(fileCount: 12);

            Press(viewer, InputKey.Space);
            state.IsBlinking.ShouldBeTrue();

            // No repeat between them: that is the whole difference from a hold.
            Press(viewer, InputKey.Space);
            Release(viewer, InputKey.Space);

            state.IsBlinking.ShouldBeFalse("a tap is the toggle it always was");
            state.BlinkResumeOnRelease.ShouldBeFalse();
        }

        /// <summary>
        /// Holding the key that STARTED a blink is not a hold: there is nothing suspended, so the release
        /// must not turn anything on or off.
        /// </summary>
        [Fact]
        public void HoldingSpaceThatStartedABlinkLeavesItRunning()
        {
            var (viewer, state) = NewViewer(fileCount: 12);

            Hold(viewer, InputKey.Space);
            state.IsBlinking.ShouldBeTrue();
            state.BlinkResumeOnRelease.ShouldBeFalse("this press started the blink, it suspended nothing");

            Release(viewer, InputKey.Space);
            state.IsBlinking.ShouldBeTrue();
        }

        /// <summary>
        /// A release lost to a focus change leaves the blink STOPPED, and the next press starts clean.
        /// </summary>
        /// <remarks>
        /// SDL sends no key-up when the window loses focus mid-hold, so this is a real sequence and not a
        /// contrived one. Stopped is the safe end of it: visible, and one press from running again. The
        /// assertion that matters is the second one, since a flag left set would make some later,
        /// unrelated release resume a blink nobody asked for.
        /// </remarks>
        [Fact]
        public void AReleaseLostToAFocusChangeLeavesTheBlinkStopped()
        {
            var (viewer, state) = NewViewer(fileCount: 12);

            Press(viewer, InputKey.Space);
            Hold(viewer, InputKey.Space);
            state.IsBlinking.ShouldBeFalse();
            state.BlinkResumeOnRelease.ShouldBeTrue();

            // The window loses focus, so no KeyUp ever arrives. The user presses Space again later.
            Press(viewer, InputKey.Space);

            state.IsBlinking.ShouldBeTrue("the fresh press starts it");
            state.BlinkResumeOnRelease.ShouldBeFalse("the stale hold cannot outlive the press that set it");

            Release(viewer, InputKey.Space);
            state.IsBlinking.ShouldBeTrue("and that release resumes nothing, having suspended nothing");
        }

        /// <summary>A release of any other key is not claimed, so a host can keep routing it.</summary>
        [Fact]
        public void ReleasingAnyOtherKeyIsNotClaimed()
        {
            var (viewer, state) = NewViewer(fileCount: 12);

            Press(viewer, InputKey.Space);
            var blinking = state.IsBlinking;

            Release(viewer, InputKey.Up);

            state.IsBlinking.ShouldBe(blinking);
        }
    }
}
