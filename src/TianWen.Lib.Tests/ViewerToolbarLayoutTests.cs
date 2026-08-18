using System;
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
    /// Offline tests for the viewer toolbar's geometry over the CPU <see cref="RgbaImageRenderer"/>.
    /// </summary>
    /// <remarks>
    /// <para>Two things here are only checkable from the arranged rects, never from a screenshot: that the
    /// help button is <b>pinned</b> to the right edge rather than merely near it, and that it <b>stays</b>
    /// there when a neighbour relabels. A rendered bar looks correct in both the fixed and the drifting
    /// case, for whichever frame you happened to capture.</para>
    /// <para>The other half is that hit-testing now answers from the painted rects, so a hit test and a
    /// paint can no longer disagree. That is asserted directly: the centre of the rect each button was
    /// painted at must resolve back to that button.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerToolbarLayoutTests
    {
        private const uint SurfaceW = 1400;
        private const uint SurfaceH = 700;

        // The toolbar spans the full content width and the layout insets it by PanelPadding (6 design
        // units) at DpiScale 1. Restated rather than read off the widget: a test that sources the constant
        // it is checking cannot notice the constant changing.
        private const float ExpectedRightInset = 6f;

        private sealed class ToolbarViewer : ImageRendererBase<RgbaImage>
        {
            public ToolbarViewer(RgbaImageRenderer renderer) : base(renderer)
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
        }

        private static ViewerState NewState() => new ViewerState
        {
            ShowFileList = false,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
        };

        private static ToolbarViewer NewViewer(RgbaImageRenderer renderer)
        {
            var viewer = new ToolbarViewer(renderer);
            // Gives the widget an image without a source, so the buttons gated on one are enabled.
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, 400, 300);
            return viewer;
        }

        // Placed rect for any button, enabled or not. TryGetPaintedToolbarRect only answers for
        // REGISTERED buttons, and a button can legitimately be laid out while disabled (Boost needs a
        // document); the layout question is about placement, not about whether it can be clicked.
        private static RectF32 PlacedRect(ToolbarViewer viewer, ToolbarAction action)
            => viewer.PaintedToolbarButtons.Single(b => b.Action == action).Rect;

        [Fact]
        public void TheHelpButtonIsPinnedToTheRightEdge()
        {
            using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);

            viewer.Render(null, NewState());

            viewer.TryGetPaintedToolbarRect(ToolbarAction.Shortcuts, out var help).ShouldBeTrue();
            help.Right.ShouldBe(SurfaceW - ExpectedRightInset, 0.5);
        }

        [Fact]
        public void NoOtherButtonOverlapsTheHelpButton()
        {
            using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);

            viewer.Render(null, NewState());

            viewer.TryGetPaintedToolbarRect(ToolbarAction.Shortcuts, out var help).ShouldBeTrue();
            var others = viewer.PaintedToolbarButtons
                .Where(b => b.Action is not ToolbarAction.Shortcuts)
                .ToArray();

            others.ShouldNotBeEmpty();
            foreach (var (action, rect) in others)
            {
                // An overlapped button is worse than an absent one: it is still registered, so it takes
                // the click aimed at whatever is drawn over it.
                rect.Right.ShouldBeLessThanOrEqualTo(help.X, $"{action} runs into the help button");
            }
        }

        [Fact]
        public void TheHelpButtonDoesNotMoveWhenANeighbourRelabels()
        {
            using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);

            var state = NewState();
            viewer.Render(null, state);
            viewer.TryGetPaintedToolbarRect(ToolbarAction.Shortcuts, out var before).ShouldBeTrue();
            var plainBoost = PlacedRect(viewer, ToolbarAction.CurvesBoost);

            // "Boost" becomes "Boost 25%", widening a button to the LEFT of help. Under a purely
            // left-to-right bar that shifts everything after it, which is the drift that makes a help
            // button unfindable.
            state.CurvesBoost = 0.25f;
            viewer.Render(null, state);
            var wideBoost = PlacedRect(viewer, ToolbarAction.CurvesBoost);
            viewer.TryGetPaintedToolbarRect(ToolbarAction.Shortcuts, out var after).ShouldBeTrue();

            // Guard the guard FIRST: if the label did not actually widen, the pin assertion proves nothing.
            wideBoost.Width.ShouldBeGreaterThan(plainBoost.Width);
            after.ShouldBe(before);
        }

        [Fact]
        public void HitTestingAnswersFromTheRectTheButtonWasPaintedAt()
        {
            using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
            var viewer = NewViewer(renderer);

            viewer.Render(null, NewState());

            var checked_ = 0;
            foreach (var (action, rect) in viewer.PaintedToolbarButtons.ToArray())
            {
                if (!viewer.TryGetPaintedToolbarRect(action, out _))
                {
                    continue; // disabled this frame, so deliberately never registered
                }

                viewer.HitTestToolbar(rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f)
                    .ShouldBe(action);
                checked_++;
            }

            checked_.ShouldBeGreaterThan(0);
        }

        [Fact]
        public void OnANarrowSurfaceTheLeftRunStopsShortInsteadOfSlidingUnderTheHelpButton()
        {
            const uint narrow = 360;
            using var wideRenderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
            var wide = NewViewer(wideRenderer);
            wide.Render(null, NewState());
            var wideCount = wide.PaintedToolbarButtons.Count();

            // Far too narrow for the full set -- the case that used to overflow past the right edge.
            using var renderer = new RgbaImageRenderer(narrow, SurfaceH);
            var viewer = NewViewer(renderer);
            viewer.Render(null, NewState());

            viewer.TryGetPaintedToolbarRect(ToolbarAction.Shortcuts, out var help).ShouldBeTrue();
            help.Right.ShouldBe(narrow - ExpectedRightInset, 0.5);

            var placed = viewer.PaintedToolbarButtons.ToArray();
            // Buttons had to be dropped for this to be the narrow case at all.
            placed.Length.ShouldBeLessThan(wideCount);
            foreach (var (action, rect) in placed.Where(b => b.Action is not ToolbarAction.Shortcuts))
            {
                rect.Right.ShouldBeLessThanOrEqualTo(help.X, $"{action} runs into the help button");
            }
        }
    }
}
