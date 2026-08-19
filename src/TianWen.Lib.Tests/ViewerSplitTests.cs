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

        // A pin taken with everything at its default, which is what the viewer pins when nothing has
        // been touched yet.
        private static SplitCompareController PinnedAt(DisplayControls controls)
        {
            var split = new SplitCompareController { Mode = SplitCompare.PinnedSettings };
            split.RequestPin();
            split.ConsumePinRequest(default, controls);
            return split;
        }

        [Theory]
        [InlineData(SplitCompare.BeforePixels, "Before", "After")]
        [InlineData(SplitCompare.PinnedSettings, "Pinned", "Live (no change)")]
        public void EachHalfIsNamedForWhatItActuallyShows(SplitCompare mode, string left, string right)
        {
            // The complaint this answers: after pressing a few buttons it is not clear WHAT the two
            // halves are, and the answer differs by mode -- pre-enhance PIXELS on one, the same pixels
            // under frozen display SETTINGS on the other. Neither is derivable from the picture.
            var split = new SplitCompareController { Mode = mode };

            split.HalfLabels(default).ShouldBe((left, right));
        }

        [Fact]
        public void TheLiveHalfNamesWhatActuallyDiffers()
        {
            var split = PinnedAt(default);

            var live = default(DisplayControls) with { HdrPresetIndex = 2 };

            split.HalfLabels(live).Right.ShouldBe("Live: HDR");
        }

        [Fact]
        public void DifferencesStackAndAreNamedTogether()
        {
            var split = PinnedAt(default);

            var live = default(DisplayControls) with
            {
                HdrPresetIndex = 2,
                ManualWhiteBalance = (1.2f, 1f, 0.9f),
            };

            split.HalfLabels(live).Right.ShouldBe("Live: HDR, WB");
        }

        [Fact]
        public void TheOrderOfTheNamesDoesNotDependOnTheOrderOfTheClicks()
        {
            // The label describes two STATES, not the sequence that produced them. If it were built in
            // change order it would reshuffle while the user works, and the same picture would carry
            // two different labels depending on how it was reached.
            var split = PinnedAt(default);

            var hdrThenWb = default(DisplayControls) with { HdrPresetIndex = 2 };
            hdrThenWb = hdrThenWb with { ManualWhiteBalance = (1.2f, 1f, 0.9f) };

            var wbThenHdr = default(DisplayControls) with { ManualWhiteBalance = (1.2f, 1f, 0.9f) };
            wbThenHdr = wbThenHdr with { HdrPresetIndex = 2 };

            split.HalfLabels(hdrThenWb).Right.ShouldBe(split.HalfLabels(wbThenHdr).Right);
        }

        [Fact]
        public void ChangingSomethingBackStopsItBeingNamed()
        {
            var split = PinnedAt(default);

            split.HalfLabels(default(DisplayControls) with { CurvesBoostIndex = 3 }).Right
                .ShouldBe("Live: Boost");

            // Back to the pinned value: the halves genuinely agree again, and saying so is the point --
            // two identical halves with a line between them is the one state that reads as a bug.
            split.HalfLabels(default).Right.ShouldBe("Live (no change)");
        }

        [Fact]
        public void ALongListCollapsesIntoACount()
        {
            var split = PinnedAt(default);

            var live = default(DisplayControls) with
            {
                StretchPresetIndex = 1,
                CurvesBoostIndex = 2,
                HdrPresetIndex = 3,
                ColorCalibrationEnabled = true,
            };

            // Named in declaration order, then a count -- a label that grew without bound would run off
            // its own half of the pane.
            split.HalfLabels(live).Right.ShouldBe("Live: Strength, Boost +2");
        }

        [Fact]
        public void WhiteBalanceAloneDoesNotAlsoReportTheStretch()
        {
            // The reason this diffs CONTROLS and not the rendition. ComputeStretchUniforms scales the
            // per-channel stats by white balance before deriving shadows/midtones/rescale, so a
            // rendition diff would report the stretch moving too -- naming a control the user never
            // pressed.
            var split = PinnedAt(default);

            var live = default(DisplayControls) with { ManualWhiteBalance = (1.3f, 1f, 0.8f) };

            split.HalfLabels(live).Right.ShouldBe("Live: WB");
        }

        [Fact]
        public void APixelComparisonNamesNoSettings()
        {
            // Both halves share the live rendition in pixel mode, so the settings are identical by
            // construction and naming any would be a lie.
            var split = new SplitCompareController { Mode = SplitCompare.BeforePixels };

            split.HalfLabels(default(DisplayControls) with { HdrPresetIndex = 2 })
                .ShouldBe(("Before", "After"));
        }

        [Fact]
        public void TheTwoLabelsNeverReadAsOneBeingBetter()
        {
            // Both sides share one grey on purpose, so the labels stay descriptive. The pair below must
            // also stay distinguishable: telling the two MODES apart is the whole point.
            var pixels = new SplitCompareController { Mode = SplitCompare.BeforePixels }.HalfLabels(default);
            var pinned = new SplitCompareController { Mode = SplitCompare.PinnedSettings }.HalfLabels(default);

            pixels.Left.ShouldNotBe(pixels.Right);
            pinned.Left.ShouldNotBe(pinned.Right);
            pixels.ShouldNotBe(pinned);
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
            viewer.Draws[0].Clip.ShouldBeNull();
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
    }
}
