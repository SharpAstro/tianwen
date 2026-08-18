using System;
using System.IO;
using System.Linq;
using DIR.Lib;
using SharpAstro.Png;
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
        // Wide enough to seat the whole run on ONE row, so the pinning tests below exercise the plain
        // bar. It has to be stated: at 1400 the full set already needs two rows, which is exactly the
        // case that used to lose its tail silently.
        private const uint SurfaceW = 2400;
        private const uint SurfaceH = 700;

        // An ordinary desktop window, and narrower than the run -- the wrap case in normal use rather
        // than at a contrived size.
        private const uint OrdinaryWindowW = 1400;

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

        // The invariant is that no button overlaps another, NOT that every button is left of the help
        // button -- once the run can wrap, a second-row button legitimately extends past its x. Stating
        // it as an intersection is also the stronger form: it was only ever "left of" because a
        // single-row bar made the two equivalent.
        private static bool Overlaps(in RectF32 a, in RectF32 b)
            => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

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
                Overlaps(rect, help).ShouldBeFalse($"{action} runs into the help button");
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
            // Buttons had to be dropped for this to be the narrow case at all -- two rows is the cap, and
            // 360 px is far below what even two rows can seat.
            placed.Length.ShouldBeLessThan(wideCount);
            foreach (var (action, rect) in placed.Where(b => b.Action is not ToolbarAction.Shortcuts))
            {
                Overlaps(rect, help).ShouldBeFalse($"{action} runs into the help button");
            }
        }

        [Fact]
        public void AnOrdinaryWindowWrapsToASecondRowInsteadOfDroppingButtons()
        {
            using var wideRenderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
            var wide = NewViewer(wideRenderer);
            wide.Render(null, NewState());
            var everything = wide.PaintedToolbarButtons.Select(b => b.Action).ToArray();
            var oneRowBand = wide.ScaledToolbarHeight;

            // Guard the guard: if the baseline is not actually one row, "twice the band" below means
            // nothing and the whole test degenerates.
            wide.PaintedToolbarButtons.Select(b => MathF.Round(b.Rect.Y)).Distinct().Count().ShouldBe(1);

            using var renderer = new RgbaImageRenderer(OrdinaryWindowW, SurfaceH);
            var viewer = NewViewer(renderer);
            viewer.Render(null, NewState());

            var placed = viewer.PaintedToolbarButtons.ToArray();
            var rowTops = placed.Select(b => MathF.Round(b.Rect.Y)).Distinct().ToArray();

            rowTops.Length.ShouldBe(2, "the run should have wrapped, not run off the edge");
            // Nothing dropped: keeping the buttons IS the point of wrapping, and this is an ordinary
            // window -- before the wrap the tail of the bar was simply missing at this size.
            placed.Select(b => b.Action).ShouldBe(everything, ignoreOrder: true);
            // ...and the band grew to hold the second row, rather than that row painting over the image.
            viewer.ScaledToolbarHeight.ShouldBe(oneRowBand * 2f, 0.5);

            var bandBottom = viewer.ScaledToolbarHeight;
            foreach (var (action, rect) in placed)
            {
                rect.Bottom.ShouldBeLessThanOrEqualTo(bandBottom, $"{action} hangs below the toolbar band");
            }
        }

        [Fact]
        public void NoButtonOverlapsAnyOtherWhenTheRunWraps()
        {
            using var renderer = new RgbaImageRenderer(OrdinaryWindowW, SurfaceH);
            var viewer = NewViewer(renderer);
            viewer.Render(null, NewState());

            var placed = viewer.PaintedToolbarButtons.ToArray();
            placed.Select(b => MathF.Round(b.Rect.Y)).Distinct().Count().ShouldBe(2);

            for (var i = 0; i < placed.Length; i++)
            {
                for (var j = i + 1; j < placed.Length; j++)
                {
                    Overlaps(placed[i].Rect, placed[j].Rect)
                        .ShouldBeFalse($"{placed[i].Action} overlaps {placed[j].Action}");
                }
            }
        }

        [Fact]
        public void TheHelpButtonKeepsTheTopRightCornerWhenTheRunWraps()
        {
            using var renderer = new RgbaImageRenderer(OrdinaryWindowW, SurfaceH);
            var viewer = NewViewer(renderer);
            viewer.Render(null, NewState());

            // Guard the guard: on a one-row bar the corner assertions below are trivially true, so this
            // has to be the wrapped case for the test to be testing anything.
            viewer.PaintedToolbarButtons.Select(b => MathF.Round(b.Rect.Y)).Distinct().Count().ShouldBe(2);

            viewer.TryGetPaintedToolbarRect(ToolbarAction.Shortcuts, out var help).ShouldBeTrue();
            var firstRowTop = viewer.PaintedToolbarButtons.Min(b => b.Rect.Y);

            // The FIRST row, not the last. A corner that moves down whenever the wrap count changes is
            // exactly the drift the pin exists to prevent -- and the wrap count changes with the window.
            help.Y.ShouldBe(firstRowTop, 0.5);
            help.Right.ShouldBe(OrdinaryWindowW - ExpectedRightInset, 0.5);
        }

        [Fact]
        public void TheSecondRowIsActuallyPAINTEDAndNotJustLaidOut()
        {
            using var renderer = new RgbaImageRenderer(OrdinaryWindowW, SurfaceH);
            var viewer = NewViewer(renderer);
            viewer.Render(null, NewState());

            var placed = viewer.PaintedToolbarButtons.ToArray();
            var rowTops = placed.Select(b => b.Rect.Y).Distinct().OrderBy(y => y).ToArray();
            rowTops.Length.ShouldBe(2);

            // Correct rects and a painted bar are separate claims: a row laid out below a clip, or past a
            // band the paint still sizes for one row, arranges perfectly and draws nothing. Only the
            // pixels answer that, so this reads them.
            var pixels = renderer.Surface.Pixels;
            var bandBottom = (int)MathF.Ceiling(viewer.ScaledToolbarHeight);
            var secondRowTop = (int)MathF.Floor(rowTops[1]);

            var litFirst = CountLabelPixels(pixels, renderer.Surface.Width, (int)MathF.Floor(rowTops[0]), secondRowTop);
            var litSecond = CountLabelPixels(pixels, renderer.Surface.Width, secondRowTop, bandBottom);

            // Emit the frame beside the test binary so the wrap can also be eyeballed.
            var pngPath = Path.Combine(AppContext.BaseDirectory, "viewer-toolbar-wrapped.png");
            File.WriteAllBytes(pngPath, PngWriter.Encode(pixels, renderer.Surface.Width, renderer.Surface.Height));

            litFirst.ShouldBeGreaterThan(0, $"the first row drew no label text; PNG at {pngPath}");
            litSecond.ShouldBeGreaterThan(0, $"the second row was laid out but never painted; PNG at {pngPath}");
        }

        // A label is the only grey in the band brighter than this. Enabled text draws at 0.9 (229) and
        // DISABLED text at 0.45 (115), while the brightest grey fill is the hover button at 0.40 (102) --
        // so the bar sits between 102 and 115, not somewhere comfortable. It has to: the wrapped row here
        // holds NeutBg and SPCC, both of which are disabled without a document, and a threshold picked
        // for enabled text alone reads a perfectly painted row as blank. The coloured fills (active blue,
        // selection) are excluded by requiring all three channels, since none of them is grey.
        private const byte LabelInkLevel = 108;

        private static int CountLabelPixels(ReadOnlySpan<byte> pixels, int width, int yStart, int yEnd)
        {
            var lit = 0;
            for (var y = Math.Max(yStart, 0); y < yEnd; y++)
            {
                var row = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var i = row + x * 4;
                    if (i + 2 < pixels.Length
                        && pixels[i] > LabelInkLevel && pixels[i + 1] > LabelInkLevel && pixels[i + 2] > LabelInkLevel)
                    {
                        lit++;
                    }
                }
            }
            return lit;
        }
    }
}
