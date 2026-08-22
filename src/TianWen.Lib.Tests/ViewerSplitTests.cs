using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Offline tests for the viewer's before/after split (docs/plans/before-after-slider.md) over the CPU
    /// <see cref="RgbaImageRenderer"/> -- no GPU needed, because the split is a SURFACE-AGNOSTIC
    /// mechanism: two draws of the same quad, cut down by DIR.Lib's clip stack. That is the whole reason
    /// it can be pinned here rather than only by a GPU readback.
    /// </summary>
    [Collection("UI")]
    public class ViewerSplitTests
    {
        private const uint SurfaceW = 1000;
        private const uint SurfaceH = 600;

        /// <summary>One recorded <c>RenderImageQuad</c> call.</summary>
        private readonly record struct DrawRecord(
            RenditionSlot Slot, bool SampleBefore, float Left, float Right, float CurvesBoost, RectInt? Clip);

        // Records the clip rects the widget applies, so a test can assert on the region a draw was
        // confined to. ApplyClip is the one thing a clipping backend implements, and the base has already
        // intersected the rect with its parents by the time it arrives.
        private sealed class ClipRecordingRenderer(uint width, uint height) : RgbaImageRenderer(width, height)
        {
            public RectInt? CurrentClip { get; private set; }

            protected override void ApplyClip(in RectInt rect)
            {
                CurrentClip = rect;
                base.ApplyClip(rect);
            }

            protected override void ClearClip()
            {
                CurrentClip = null;
                base.ClearClip();
            }
        }

        // A viewer whose GPU seam only RECORDS. Everything above the seam -- layout, placement, the split
        // decision, clipping, which rendition each half gets -- is the real shipped code.
        private sealed class RecordingViewer : ImageRendererBase<RgbaImage>
        {
            private readonly ClipRecordingRenderer _renderer;

            public RecordingViewer(ClipRecordingRenderer renderer) : base(renderer)
            {
                _renderer = renderer;
                Width = renderer.Width;
                Height = renderer.Height;
            }

            public List<DrawRecord> Draws { get; } = [];
            public int ReleaseBeforeCalls { get; private set; }
            private bool _beforeRetained;

            public override bool HasBeforeImageTextures => _beforeRetained;

            public override bool TryRetainImageTexturesAsBefore()
            {
                _beforeRetained = true;
                return true;
            }

            public override void ReleaseBeforeImageTextures()
            {
                _beforeRetained = false;
                ReleaseBeforeCalls++;
            }

            /// <summary>Forces the memory policy. The shipped one reads machine-global load, which moved
            /// between two tests 100 ms apart and made this suite flaky before it was made injectable.</summary>
            public bool PretendMemoryIsShort { get; set; }

            public long LastCacheSizeAsked { get; private set; }

            protected override bool ShouldSkipBeforePixelCache(long bytes)
            {
                LastCacheSizeAsked = bytes;
                return PretendMemoryIsShort;
            }

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels)
                => Draws.Add(new DrawRecord(slot, sampleBeforeChannels, left, right,
                    rendition.CurvesBoost, _renderer.CurrentClip));

            protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
                ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

            protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
                float angleRad, RGBAColor32 color, float thickness) { }

            protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

            protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
                RGBAColor32 color, float thickness) { }

            protected override void OnResize(uint width, uint height) { }

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int imageWidth, int imageHeight) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;

            /// <summary>The arranged image-area rect, so a test can press where the divider was painted.</summary>
            public RectF32 ImageArea => ImageAreaRect;
        }

        // Chromeless + no side panels, so the image area IS the content region and the arithmetic in the
        // assertions below is about the split, not about toolbar heights.
        private static ViewerState NewState() => new ViewerState
        {
            HideChrome = true,
            ShowFileList = false,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
        };

        private static RecordingViewer NewViewer(ClipRecordingRenderer renderer)
        {
            var viewer = new RecordingViewer(renderer);
            // Gives the widget an image to place WITHOUT a source: UploadChannelTexture stamps
            // ImageWidth/ImageHeight, which is what gates RenderImage.
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, 400, 300);
            return viewer;
        }

        private static SplitCompareController PinnedAt(DisplayControls controls)
        {
            var split = new SplitCompareController { Mode = SplitCompare.PinnedSettings };
            split.RequestPin();
            split.ConsumePinRequest(default, controls);
            return split;
        }

        private static DisplayControls Boosted(float boost)
            => DisplayControls.Defaults with { CurvesBoost = boost };

        [Theory]
        [InlineData(SplitCompare.BeforePixels, "Before", "After")]
        [InlineData(SplitCompare.PinnedSettings, "Pinned", "Live (same)")]
        public void EachHalfIsNamedForWhatItActuallyShows(SplitCompare mode, string left, string right)
        {
            // The complaint this answers: after pressing a few buttons it is not clear WHAT the two
            // halves are, and the answer differs by mode -- pre-enhance PIXELS on one, the same pixels
            // under frozen display SETTINGS on the other. Neither is derivable from the picture.
            var split = new SplitCompareController { Mode = mode };

            split.HalfLabels(DisplayControls.Defaults).ShouldBe((left, right));
        }

        [Fact]
        public void TheValueStaysOnTheSideThatActuallyHoldsIt()
        {
            // The bug this is here for. Pin with a boost, then switch the boost off: the LEFT half is
            // the boosted one and the right is plain. A label naming only the control that differs
            // ("Live: Boost") sits over the half with no boost in it and reads as the opposite of the
            // truth -- correct about the control, backwards about the side.
            var split = PinnedAt(Boosted(0.25f));

            var (left, right) = split.HalfLabels(DisplayControls.Defaults);

            left.ShouldBe("Pinned: Boost 25%");
            // "No Boost", not "-Boost 25%": a minus in front of a percentage reads as a quantity
            // (reduced BY 25%, or a negative boost) rather than as absence, and the pinned half
            // states the lost value one word across the divider anyway.
            right.ShouldBe("Live: No Boost");
        }

        [Fact]
        public void SomethingSwitchedOnSinceThePinReadsAsAnAddition()
        {
            var split = PinnedAt(DisplayControls.Defaults);

            var live = DisplayControls.Defaults with { ColorCalibrationEnabled = true };

            split.HalfLabels(live).ShouldBe(("Pinned", "Live: +Calibrate"));
        }

        [Fact]
        public void AChangedValueIsNamedByItsNewValueWithNoSign()
        {
            // Neither added nor removed: both halves have a boost, they disagree about how much. The
            // number carries the whole statement, and the pinned half beside it holds the old one.
            var split = PinnedAt(Boosted(0.25f));

            split.HalfLabels(Boosted(1.5f)).ShouldBe(("Pinned: Boost 25%", "Live: Boost 150%"));
        }

        [Fact]
        public void TheContestedControlIsNamedBeforeTheSharedOnes()
        {
            // Measured against a real session: the halves differed by the colour calibration, and in
            // plain declaration order two SHARED controls filled the two-name quota and collapsed the
            // one that actually differed into "+1" -- hiding the only thing the split was open to
            // show. A control both halves share is context; one they disagree about is the point.
            var pinned = DisplayControls.Defaults with
            {
                StretchMode = StretchMode.Linked,
                StretchParameters = new StretchParameters(0.15, -5.0),
                ColorCalibrationEnabled = true,
            };
            var live = pinned with { ColorCalibrationEnabled = false };

            var (left, right) = PinnedAt(pinned).HalfLabels(live);

            left.ShouldStartWith("Pinned: Calibrate");
            left.ShouldEndWith("+1");
            right.ShouldBe("Live: No Calibrate");
        }

        [Fact]
        public void ChangingSomethingBackMakesTheHalvesAgreeAgain()
        {
            var split = PinnedAt(DisplayControls.Defaults);

            split.HalfLabels(Boosted(0.25f)).Right.ShouldBe("Live: +Boost 25%");

            // Back to the pinned value: the halves genuinely agree, and saying so is the point --
            // two identical halves with a line between them is the one state that reads as a bug.
            split.HalfLabels(DisplayControls.Defaults).Right.ShouldBe("Live (same)");
        }

        [Fact]
        public void ALongListCollapsesIntoACount()
        {
            var split = PinnedAt(DisplayControls.Defaults);

            var live = DisplayControls.Defaults with
            {
                StretchMode = StretchMode.Linked,
                CurvesBoost = 0.5f,
                HdrAmount = 1.5f,
                ColorCalibrationEnabled = true,
            };

            // A label that grew without bound would run off its own half of the pane.
            split.HalfLabels(live).Right.ShouldBe("Live: +Linked, +Boost 50% +2");
        }

        [Fact]
        public void WhiteBalanceAloneDoesNotAlsoReportTheStretch()
        {
            // The reason this reads CONTROLS and not the rendition. ComputeStretchUniforms scales the
            // per-channel stats by white balance before deriving shadows/midtones/rescale, so a
            // rendition diff would report the stretch moving too -- naming a control the user never
            // pressed.
            var split = PinnedAt(DisplayControls.Defaults);

            var live = DisplayControls.Defaults with { ManualWhiteBalance = (1.3f, 1f, 0.8f) };

            split.HalfLabels(live).Right.ShouldBe("Live: +WB 1.30/1.00/0.80");
        }

        [Fact]
        public void TheCurveModeIsSilentWhileTheBoostIsOff()
        {
            // The curve mode only reaches the pixels through the boost, so at zero boost the two
            // halves render identically and naming a difference would send the reader hunting for
            // something the picture cannot show.
            var split = PinnedAt(DisplayControls.Defaults);

            var live = DisplayControls.Defaults with { CurvesMode = 1 };

            split.HalfLabels(live).Right.ShouldBe("Live");
        }

        [Fact]
        public void AModeFallingBackToItsDefaultIsNamedByThatDefault()
        {
            // Stretch mode has no "off" state -- every mode IS a mode -- so going back to the default
            // is a CHANGE to Unlinked, not the absence of Linked. "No Linked" would be nonsense.
            var split = PinnedAt(DisplayControls.Defaults with { StretchMode = StretchMode.Linked });

            split.HalfLabels(DisplayControls.Defaults).ShouldBe(("Pinned: Linked", "Live: Unlinked"));
        }

        [Fact]
        public void APixelComparisonNamesNoSettings()
        {
            // Both halves share the live rendition in pixel mode, so the settings are identical by
            // construction and naming any would be a lie.
            var split = new SplitCompareController { Mode = SplitCompare.BeforePixels };

            split.HalfLabels(Boosted(0.25f)).ShouldBe(("Before", "After"));
        }

        [Fact]
        public void TheTwoLabelsNeverReadAsOneBeingBetter()
        {
            // Both sides share one grey on purpose, so the labels stay descriptive. The pair below must
            // also stay distinguishable: telling the two MODES apart is the whole point.
            var d = DisplayControls.Defaults;
            var pixels = new SplitCompareController { Mode = SplitCompare.BeforePixels }.HalfLabels(d);
            var pinned = new SplitCompareController { Mode = SplitCompare.PinnedSettings }.HalfLabels(d);

            pixels.Left.ShouldNotBe(pixels.Right);
            pinned.Left.ShouldNotBe(pinned.Right);
            pixels.ShouldNotBe(pinned);
        }

        // ---- Frame preparation ------------------------------------------------------------------
        // PrepareFrame exists so a host can decide the layout, placement and uniforms BEFORE the main
        // render pass opens, which is the only point at which a cached image layer can be rendered.
        // These live here because the recording harness above is what can see the draws.

        /// <summary>
        /// Preparing first must produce the same frame as not preparing at all. This is the property
        /// that makes the pre-pass optional rather than load-bearing: a host that never calls it gets
        /// the old behaviour, so forgetting to wire it can only cost caching, never correctness.
        /// </summary>
        [Fact]
        public void PreparingBeforeRenderingChangesNothingAboutTheFrame()
        {
            using var rendererA = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewerA = NewViewer(rendererA);
            viewerA.Render(null, NewState());

            using var rendererB = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewerB = NewViewer(rendererB);
            var stateB = NewState();
            viewerB.PrepareFrame(null, stateB);
            viewerB.Render(null, stateB);

            viewerB.Draws.Count.ShouldBe(viewerA.Draws.Count);
            viewerB.Draws[0].ShouldBe(viewerA.Draws[0]);
            viewerB.ImageArea.ShouldBe(viewerA.ImageArea);
        }

        /// <summary>
        /// Preparing twice in one frame must do the work once. A host pre-pass plus Render's own call is
        /// the NORMAL case, so a repeated pass would run every frame rather than being a rare edge.
        /// </summary>
        /// <remarks>
        /// Asserted on the COUNTER, not on the resulting layout, because the layout cannot see this:
        /// measuring, arranging and clamping are all idempotent, so preparing twice produces an identical
        /// frame and a layout assertion would pass with the guard removed. The counter is the only
        /// observable difference, which is exactly why it exists.
        /// </remarks>
        [Fact]
        public void PreparingTwiceInAFrameOnlyDoesTheWorkOnce()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            viewer.PrepareFrame(null, state);
            viewer.PrepareFrame(null, state);
            viewer.FramePreparations.ShouldBe(1);

            // Render finds it already prepared and must not redo it either.
            viewer.Render(null, state);
            viewer.FramePreparations.ShouldBe(1);
            viewer.Draws.Count.ShouldBe(1);
        }

        /// <summary>
        /// The preparation is per FRAME, not once per viewer: the next frame must prepare again, or every
        /// frame after the first would draw the first frame's layout and the viewer would stop responding
        /// to zoom, pan and panel toggles entirely.
        /// </summary>
        [Fact]
        public void EachFramePreparesAgain()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            viewer.Render(null, state);
            var before = viewer.ImageArea;

            // A layout-changing toggle between frames: it can only be picked up if the second frame
            // prepares from scratch.
            state.ShowInfoPanel = true;
            viewer.Render(null, state);

            viewer.ImageArea.Width.ShouldBeLessThan(before.Width,
                "the info panel must narrow the image pane on the very next frame");
            viewer.FramePreparations.ShouldBe(2, "one preparation per frame");
        }

        [Fact]
        public void WithTheSplitOff_ItDrawsOnceFromTheLiveSlot()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            viewer.Render(null, state);

            viewer.Draws.Count.ShouldBe(1);
            viewer.Draws[0].Slot.ShouldBe(RenditionSlot.Live);
            viewer.Draws[0].SampleBefore.ShouldBeFalse();

            // Clipped to the image PANE, which under HideChrome is the whole surface -- so this
            // asserts the pane, not merely "some clip". It used to assert no clip at all: the split
            // path clipped each half while this one clipped nothing, making the ordinary single-image
            // view the worse-bounded of the two, and every fragment behind opaque chrome paid a full
            // demosaic + stretch before being painted over. The chrome-on case is the one that shows
            // the difference, and it is the test below.
            var clip = viewer.Draws[0].Clip.ShouldNotBeNull();
            clip.UpperLeft.X.ShouldBe(0);
            clip.UpperLeft.Y.ShouldBe(0);
            clip.Width.ShouldBe((int)SurfaceW);
            clip.Height.ShouldBe((int)SurfaceH);
        }

        /// <summary>
        /// With the chrome drawn, the image draw must be bounded to the pane the chrome leaves behind.
        /// </summary>
        /// <remarks>
        /// The companion above cannot see this: its state sets <c>HideChrome</c>, so the pane IS the
        /// surface and a missing clip is indistinguishable from a correct one. That is exactly how the
        /// unclipped path survived having a test.
        /// </remarks>
        [Fact]
        public void WithChromeShown_TheImageDrawIsBoundedToThePane()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            state.HideChrome = false;

            viewer.Render(null, state);

            viewer.Draws.Count.ShouldBe(1);
            var clip = viewer.Draws[0].Clip.ShouldNotBeNull();
            clip.Height.ShouldBeLessThan((int)SurfaceH,
                "the toolbar and status bar rows must be outside the clip, or their fragments are shaded and then painted over");
            clip.Height.ShouldBeGreaterThan(0);
            clip.Width.ShouldBeGreaterThan(0);
        }

        [Fact]
        public void TheSplitDrawsBothHalvesFromTheSameQuadIntoComplementaryClips()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Split.Toggle(hasBeforePixels: false);

            viewer.Render(null, state);

            viewer.Draws.Count.ShouldBe(2);
            viewer.Draws[0].Slot.ShouldBe(RenditionSlot.Comparison);
            viewer.Draws[1].Slot.ShouldBe(RenditionSlot.Live);

            // The SAME quad both times. If the halves were drawn as narrower quads instead of being
            // clipped, the two renditions would sit in different projection spaces and the image would
            // visibly jump across the divider.
            viewer.Draws[0].Left.ShouldBe(viewer.Draws[1].Left);
            viewer.Draws[0].Right.ShouldBe(viewer.Draws[1].Right);

            var leftClip = viewer.Draws[0].Clip.ShouldNotBeNull();
            var rightClip = viewer.Draws[1].Clip.ShouldNotBeNull();
            leftClip.Width.ShouldBeGreaterThan(0);
            rightClip.Width.ShouldBeGreaterThan(0);
            // Complementary and non-overlapping: the left ends exactly where the right begins.
            (leftClip.UpperLeft.X + leftClip.Width).ShouldBe(rightClip.UpperLeft.X);
        }

        [Fact]
        public void TheComparisonHalfKeepsThePinnedDialsWhileTheLiveHalfMoves()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            state.CurvesBoost = 0.25f;

            // Pin, then move the dial -- the "pin, then fiddle" gesture.
            viewer.Split.Toggle(hasBeforePixels: false);
            viewer.Render(null, state);
            state.CurvesBoost = 1.5f;
            viewer.Draws.Clear();
            viewer.Render(null, state);

            viewer.Draws.Count.ShouldBe(2);
            // The regression this guards: reading the dials off ViewerState at draw time instead of off
            // the rendition makes BOTH halves show 1.5 and the comparison silently does nothing.
            viewer.Draws[0].CurvesBoost.ShouldBe(0.25f);
            viewer.Draws[1].CurvesBoost.ShouldBe(1.5f);
        }

        [Fact]
        public void ItLeavesTheRendererUnclipped()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Split.Toggle(hasBeforePixels: false);

            viewer.Render(null, state);

            // Push and pop in pairs, or every later frame draws inside a stale region.
            renderer.ClipDepth.ShouldBe(0);
            renderer.CurrentClip.ShouldBeNull();
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(1f)]
        public void NeitherHalfCanCollapseToNothing(float fraction)
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Split.Toggle(hasBeforePixels: false);
            viewer.Split.Fraction = fraction;

            viewer.Render(null, state);

            viewer.Draws.Count.ShouldBe(2);
            // A zero-width half reads as the feature being broken rather than as the divider being at
            // the end of its travel, so the divider keeps a margin at both extremes.
            viewer.Draws[0].Clip.ShouldNotBeNull().Width.ShouldBeGreaterThan(0);
            viewer.Draws[1].Clip.ShouldNotBeNull().Width.ShouldBeGreaterThan(0);
        }

        [Fact]
        public void PinnedModeWithNothingPinnedDrawsNoDivider()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            // Set the fraction WITHOUT going through ToggleSplit, which is what pins.
            viewer.Split.Fraction = 0.5f;
            viewer.Split.Mode = SplitCompare.PinnedSettings;

            viewer.Render(null, state);

            // Two identical halves with a line between them is indistinguishable from a bug, so a mode
            // whose precondition is unmet must not draw at all.
            viewer.Draws.Count.ShouldBe(1);
            viewer.Draws[0].Slot.ShouldBe(RenditionSlot.Live);
        }

        [Fact]
        public void BeforePixelsModeWithNoRetainedPixelsDrawsNoDivider()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Split.Fraction = 0.5f;
            viewer.Split.Mode = SplitCompare.BeforePixels;

            viewer.Render(null, state);

            viewer.Draws.Count.ShouldBe(1);
            viewer.Draws[0].Slot.ShouldBe(RenditionSlot.Live);
        }

        [Fact]
        public void RetainedPixelsFeedTheComparisonHalf()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            // What an enhance apply does: replace the source, ask for the outgoing pixels to be kept.
            state.NotifySourceReplaced();
            state.RetainBeforePixelsRequested = true;
            viewer.UploadDocumentTextures(new StubSource(), state);
            viewer.HasBeforeImageTextures.ShouldBeTrue();
            viewer.Split.PixelsGeneration.ShouldBe(state.SourceGeneration);

            viewer.Split.Toggle(viewer.HasBeforeImageTextures);
            viewer.Split.Mode.ShouldBe(SplitCompare.BeforePixels);
            viewer.Draws.Clear();
            viewer.Render(null, state);

            viewer.Draws.Count.ShouldBe(2);
            viewer.Draws[0].SampleBefore.ShouldBeTrue();
            viewer.Draws[1].SampleBefore.ShouldBeFalse();
        }

        [Fact]
        public void OpeningAnotherDocumentInvalidatesTheBeforeAndTakesTheSplitDown()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            state.NotifySourceReplaced();
            state.RetainBeforePixelsRequested = true;
            viewer.UploadDocumentTextures(new StubSource(), state);
            viewer.Split.Toggle(viewer.HasBeforeImageTextures);
            viewer.Render(null, state);
            viewer.Draws.Count.ShouldBe(2);

            // Another document is adopted. This is INVALIDATION, not eviction: the retained pixels are
            // the before of a source that is gone, so there is nothing to reload and the comparison must
            // stop -- otherwise two unrelated images are drawn either side of one line.
            state.NotifySourceReplaced();
            viewer.Draws.Clear();
            viewer.Render(null, state);

            viewer.ReleaseBeforeCalls.ShouldBe(1);
            viewer.HasBeforeImageTextures.ShouldBeFalse();
            viewer.Split.PixelsGeneration.ShouldBeNull();
            viewer.Split.Fraction.ShouldBeNull();
            viewer.Draws.Count.ShouldBe(1);
        }

        [Fact]
        public void WhenMemoryIsShortTheCacheIsSkippedAndTheComparisonStaysUnavailable()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            viewer.PretendMemoryIsShort = true;
            var state = NewState();

            state.NotifySourceReplaced();
            state.RetainBeforePixelsRequested = true;
            viewer.UploadDocumentTextures(new StubSource(), state);

            // Declining to cache must leave a coherent viewer, not a half-armed one: nothing retained,
            // nothing stamped, and the pixel comparison simply not on offer.
            viewer.HasBeforeImageTextures.ShouldBeFalse();
            viewer.Split.PixelsGeneration.ShouldBeNull();

            // Asked about the SIZE of this retention, not merely whether memory is tight -- 400x300 mono
            // floats. Otherwise a 100 MB cache is refused on a box with gigabytes to spare.
            viewer.LastCacheSizeAsked.ShouldBe(400L * 300 * 1 * sizeof(float));

            viewer.Split.Fraction = 0.5f;
            viewer.Split.Mode = SplitCompare.BeforePixels;
            viewer.Render(null, state);
            viewer.Draws.Count.ShouldBe(1);
        }

        [Fact]
        public void TogglingOffClearsTheDrag()
        {
            var split = new SplitCompareController();
            split.Toggle(hasBeforePixels: false);
            split.BeginDrag();

            split.Toggle(hasBeforePixels: false);

            split.Fraction.ShouldBeNull();
            split.IsDragging.ShouldBeFalse();
        }

        [Fact]
        public void TheDividerArmsItsOwnDragSoNoHostNeedsAPressBranch()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            viewer.Split.Toggle(hasBeforePixels: false);
            viewer.Render(null, state);

            // Find the divider the way a press does, and dispatch to it the way a host does. Nothing
            // anywhere branches on what a "Split" hit MEANS -- the region carries its own onClick, so
            // arming the drag is a property of having painted the divider.
            var regions = viewer.GetRegisteredRegions();
            var bandIndex = System.Array.FindIndex(regions, r => r.Result is ResizeHandleHit { Id: "Split" });
            bandIndex.ShouldBeGreaterThanOrEqualTo(0);
            var band = regions[bandIndex];
            viewer.HitTestAndDispatch(band.X + band.Width / 2f, band.Y + band.Height / 2f);
            viewer.Split.IsDragging.ShouldBeTrue();

            // Motion and release go through HandleInput, which BOTH hosts already forward. This is the
            // regression that shipped: the press branch existed in the shared dispatcher only, so the
            // divider drew, stated a resize cursor, and could not be dragged in tianwen-fits at all.
            var area = viewer.ImageArea;
            var target = area.X + area.Width * 0.75f;
            viewer.HandleInput(new InputEvent.MouseMove(target, area.Y + 10f)).ShouldBeTrue();
            viewer.Split.Fraction.ShouldNotBeNull().ShouldBe(0.75f, 0.01);

            viewer.HandleInput(new InputEvent.MouseUp(target, area.Y + 10f, MouseButton.Left)).ShouldBeTrue();
            viewer.Split.IsDragging.ShouldBeFalse();
        }

        // Minimal source: UploadDocumentTextures only needs geometry + channel count to pick its path.
        private sealed class StubSource : IPreviewSource
        {
            public int Width => 400;
            public int Height => 300;
            public int ChannelCount => 1;
            public SensorType SensorType => SensorType.Monochrome;
            public int BayerOffsetX => 0;
            public int BayerOffsetY => 0;
            public int FrameCount => 1;
            public int FrameIndex => 0;
            public float[] PerChannelBackground => [0f];
            public float LumaBackground => 0f;
            public ImageHistogram[] ChannelStatistics => [];
            public ReadOnlySpan<float> GetChannelData(int channel) => default;
            public bool SelectFrame(int index) => false;
            public bool HasTimestamps => false;
            public DateTimeOffset TimestampOf(int index) => DateTimeOffset.MinValue;

            public StretchUniforms ComputeStretchUniforms(
                StretchMode mode, StretchParameters parameters,
                LumaWeighting weighting = LumaWeighting.Rec709, float lumaBlend = 1f, bool normalize = false,
                int curvesMode = 0, ReadOnlySpan<float> curveLut = default, float curvesBoost = 0f,
                float curvesMidpoint = 0.25f, float hdrAmount = 0f, float hdrKnee = 0.8f,
                float bgNeutralizationStrength = 1f, (float R, float G, float B)? manualWhiteBalance = null)
                => new StretchUniforms(mode, 1f, default, default, default, default, default);
        }

        // ---- The sequence matrix -------------------------------------------------------------------
        //
        // The split has two things it can compare and the user reaches them by SEQUENCE, not by a
        // setting, so the bug class here is "the halves show one thing and the labels say another".
        // Each case below drives the presses in order and asserts BOTH: which pixels each half sampled,
        // and what the labels claim. Asserting only one of them is what let the mismatch ship.
        //
        //   #  sequence                                   left pixels   labels
        //   1  A/B, nothing enhanced                      live          Pinned / Live
        //   2  enhance, A/B                               before        Before / After
        //   3  A/B, THEN enhance                          before        Before / After   <- was wrong
        //   4  enhance, A/B, revert                        --            split down
        //   5  enhance, A/B, move a slider                before        Before / After
        //   6  A/B, enhance, A/B off, A/B on              before        Before / After

        /// <summary>Simulates an enhance landing: a new source, and the outgoing pixels kept.</summary>
        private static void ApplyEnhance(RecordingViewer viewer, ViewerState state)
        {
            state.NotifySourceReplaced();
            state.RetainBeforePixelsRequested = true;
            viewer.UploadDocumentTextures(new StubSource(), state);
        }

        [Fact]
        public void Case1_SplitWithNothingEnhanced_ComparesSettingsOnLivePixels()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            viewer.Split.Toggle(viewer.HasBeforeImageTextures);
            viewer.Render(null, state);

            viewer.Split.Mode.ShouldBe(SplitCompare.PinnedSettings);
            viewer.Draws.Count.ShouldBe(2);
            viewer.Draws[0].SampleBefore.ShouldBeFalse("nothing was retained, so both halves are live pixels");
            viewer.Split.HalfLabels(DisplayControls.FromState(state)).Left.ShouldStartWith("Pinned");
        }

        [Fact]
        public void Case2_EnhanceThenSplit_ComparesPixels()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            ApplyEnhance(viewer, state);
            viewer.Split.Toggle(viewer.HasBeforeImageTextures);
            viewer.Draws.Clear();
            viewer.Render(null, state);

            viewer.Split.Mode.ShouldBe(SplitCompare.BeforePixels);
            viewer.Draws[0].SampleBefore.ShouldBeTrue();
            viewer.Split.HalfLabels(DisplayControls.FromState(state)).ShouldBe(("Before", "After"));
        }

        [Fact]
        public void Case3_SplitThenEnhance_AlsoComparesPixels()
        {
            // The reported bug. Toggle's rule -- "when pre-enhance pixels are retained those win,
            // because a user who just enhanced wants to compare PIXELS" -- was only applied at the
            // moment of pressing A/B, so enhancing while the split was ALREADY up left it comparing
            // settings. Both halves then draw enhanced pixels and nothing on screen says an enhance
            // happened, which is exactly what was reported.
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            viewer.Split.Toggle(viewer.HasBeforeImageTextures);
            viewer.Render(null, state);
            viewer.Split.Mode.ShouldBe(SplitCompare.PinnedSettings);

            ApplyEnhance(viewer, state);
            viewer.Draws.Clear();
            viewer.Render(null, state);

            viewer.Split.Mode.ShouldBe(SplitCompare.BeforePixels,
                "the mode is a consequence of what exists, not of when the button was pressed");
            viewer.Draws.Count.ShouldBe(2);
            viewer.Draws[0].SampleBefore.ShouldBeTrue("the left half is the pre-enhance frame");
            viewer.Draws[1].SampleBefore.ShouldBeFalse();
            viewer.Split.HalfLabels(DisplayControls.FromState(state)).ShouldBe(("Before", "After"));
        }

        [Fact]
        public void Case4_RevertingTheEnhanceTakesThePixelSplitDown()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            ApplyEnhance(viewer, state);
            viewer.Split.Toggle(viewer.HasBeforeImageTextures);
            viewer.Render(null, state);

            // Toggling the enhance off restores the original source: same invalidation path as opening
            // another document, because the retained pixels are the before of a frame that is gone.
            state.NotifySourceReplaced();
            viewer.Draws.Clear();
            viewer.Render(null, state);

            viewer.HasBeforeImageTextures.ShouldBeFalse();
            viewer.Split.Fraction.ShouldBeNull("a pixel split with nothing retained must not draw a divider");
            viewer.Draws.Count.ShouldBe(1);
        }

        [Fact]
        public void Case5_MovingASliderUnderAPixelSplitMovesBothHalvesTogether()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            ApplyEnhance(viewer, state);
            viewer.Split.Toggle(viewer.HasBeforeImageTextures);

            state.CurvesBoost = 1.5f;
            viewer.Draws.Clear();
            viewer.Render(null, state);

            // Only the PIXELS differ: both halves take the live rendition, so a slider is not a
            // difference between them and the labels must not imply one.
            viewer.Draws[0].CurvesBoost.ShouldBe(viewer.Draws[1].CurvesBoost);
            viewer.Draws[0].SampleBefore.ShouldBeTrue();
            viewer.Split.HalfLabels(DisplayControls.FromState(state)).ShouldBe(("Before", "After"));
        }

        [Fact]
        public void Case3b_WhenTheBeforeCacheIsSkippedTheSplitStaysOnSettings()
        {
            // Retention is budget-gated, so on a large frame under memory pressure there ARE no
            // pre-enhance pixels to compare -- and a pixel split with nothing retained draws no divider
            // at all, which is worse than comparing settings. So the split stays on settings, and the
            // labels have to state the enhance themselves: it is true of BOTH halves, so the delta label
            // correctly says nothing about it, which would otherwise leave the image visibly enhanced and
            // nothing on screen admitting it.
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            viewer.Split.Toggle(viewer.HasBeforeImageTextures);
            viewer.Render(null, state);

            viewer.PretendMemoryIsShort = true;
            ApplyEnhance(viewer, state);
            viewer.Draws.Clear();
            viewer.Render(null, state);

            viewer.HasBeforeImageTextures.ShouldBeFalse();
            viewer.Split.Mode.ShouldBe(SplitCompare.PinnedSettings);
            viewer.Draws.Count.ShouldBe(2, "a settings comparison still has two halves to draw");
            viewer.Draws[0].SampleBefore.ShouldBeFalse();

            var (left, right) = viewer.Split.HalfLabels(DisplayControls.FromState(state),
                pixelsEnhanced: true);
            left.ShouldEndWith(" + AI");
            // On BOTH: a fact true of both halves, stated on one, reads as a difference.
            right.ShouldEndWith(" + AI");
        }

        [Fact]
        public void APixelSplitDoesNotRepeatTheEnhanceInItsLabels()
        {
            // Before/After already IS the enhance, so appending it would say the same thing twice and
            // imply it is additionally true of the "Before" half, which is exactly false.
            var split = new SplitCompareController { Mode = SplitCompare.BeforePixels };
            split.HalfLabels(DisplayControls.Defaults, pixelsEnhanced: true)
                .ShouldBe(("Before", "After"));
        }

        [Fact]
        public void TheEnhanceSuffixAppearsOnlyWhileTheEnhanceIsApplied()
        {
            // Memoisation guard: the label cache keys on the controls, and the enhance flag is not one
            // of them -- so without it in the key, toggling the enhance off would keep the stale suffix.
            var split = PinnedAt(DisplayControls.Defaults);
            split.HalfLabels(DisplayControls.Defaults, pixelsEnhanced: true).Left.ShouldEndWith(" + AI");
            split.HalfLabels(DisplayControls.Defaults, pixelsEnhanced: false).Left.ShouldNotEndWith(" + AI");
        }

        [Fact]
        public void Case6_ClosingAndReopeningTheSplitAfterAnEnhanceComparesPixels()
        {
            using var renderer = new ClipRecordingRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);
            var state = NewState();

            viewer.Split.Toggle(viewer.HasBeforeImageTextures);
            ApplyEnhance(viewer, state);
            viewer.Split.Toggle(viewer.HasBeforeImageTextures);   // off
            viewer.Split.IsOn.ShouldBeFalse();
            viewer.Split.Toggle(viewer.HasBeforeImageTextures);   // on again

            viewer.Draws.Clear();
            viewer.Render(null, state);

            viewer.Split.Mode.ShouldBe(SplitCompare.BeforePixels);
            viewer.Draws[0].SampleBefore.ShouldBeTrue();
        }
    }
}
