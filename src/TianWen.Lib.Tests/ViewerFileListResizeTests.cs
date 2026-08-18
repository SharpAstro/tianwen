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
    /// The file-list divider must stay draggable in a NARROW window.
    /// </summary>
    /// <remarks>
    /// <para>Reported from a tall, ~500 px wide window: the divider would not move at all. The width the
    /// user sees is what <c>Layout.Builder.Split</c> GRANTED, which is not the width that was requested --
    /// so a request the layout can never honour looks exactly like a dead handle.</para>
    /// <para>These assert the granted rect, never the stored request, because the stored value was
    /// changing the whole time the bug was live. That is the whole reason it presented as "the drag does
    /// nothing" rather than as a clamp being wrong.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerFileListResizeTests
    {
        // The reported window: tall, and narrow enough that the file list cannot have its nominal minimum.
        private const uint NarrowW = 503;
        private const uint NarrowH = 1533;

        private sealed class ResizeViewer : ImageRendererBase<RgbaImage>
        {
            public ResizeViewer(RgbaImageRenderer renderer, float dpiScale)
                : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
                DpiScale = dpiScale;
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

            public RectF32 FileList => FileListRect;

            public RectF32 ImageArea => ImageAreaRect;

            public RectF32 InfoPanel => InfoPanelRect;
        }

        private static ViewerState NewState() => new ViewerState
        {
            ShowFileList = true,
            ShowInfoPanel = true,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
        };

        private static ResizeViewer NewViewer(RgbaImageRenderer renderer, float dpiScale = 1f)
        {
            var viewer = new ResizeViewer(renderer, dpiScale);
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, 400, 300);
            return viewer;
        }

        /// <summary>Drives a press-drag-release over the divider and reports the granted width after it.</summary>
        private static float DragDividerTo(ResizeViewer viewer, ViewerState state, float targetX)
        {
            viewer.Render(null, state);
            var divider = viewer.FileList.Right;

            viewer.HandleInput(new InputEvent.MouseDown(divider + 1f, NarrowH * 0.5f, MouseButton.Left));
            viewer.HandleInput(new InputEvent.MouseMove(targetX, NarrowH * 0.5f));
            viewer.HandleInput(new InputEvent.MouseUp(targetX, NarrowH * 0.5f, MouseButton.Left));

            viewer.Render(null, state);
            return viewer.FileList.Width;
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        [InlineData(2f)]
        public void InANarrowWindowTheFileListCanStillBeDraggedNarrower(float dpiScale)
        {
            using var renderer = new RgbaImageRenderer(NarrowW, NarrowH);
            var viewer = NewViewer(renderer, dpiScale);
            var state = NewState();

            viewer.Render(null, state);
            var granted = viewer.FileList.Width;
            granted.ShouldBeGreaterThan(0f);

            // Ask for roughly half of what the layout is currently granting.
            var after = DragDividerTo(viewer, state, granted * 0.5f);

            after.ShouldBeLessThan(granted);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        [InlineData(2f)]
        public void InANarrowWindowTheFileListCanStillBeDraggedWider(float dpiScale)
        {
            using var renderer = new RgbaImageRenderer(NarrowW, NarrowH);
            var viewer = NewViewer(renderer, dpiScale);
            var state = NewState();

            viewer.Render(null, state);
            var granted = viewer.FileList.Width;

            // First make room to grow into, so this is a genuine widen and not a clamp bumping the floor.
            var narrowed = DragDividerTo(viewer, state, granted * 0.5f);
            var widened = DragDividerTo(viewer, state, narrowed + granted * 0.25f);

            widened.ShouldBeGreaterThan(narrowed);
        }

        /// <summary>
        /// However hard the divider is dragged, the panes tile the band: none goes negative and none
        /// overhangs.
        /// </summary>
        /// <remarks>
        /// <para>This is the invariant the <c>DockLayout</c> clamp establishes, and the one whose absence
        /// caused the reported bug. Before it, dragging into a narrow window gave the info panel its full
        /// requested width regardless -- placing it at <c>parent.Right - requested</c>, i.e. LEFT of its
        /// parent and straight over the split divider -- and handed the image pane the negative
        /// remainder. An occluded divider cannot be grabbed, which is what "I cannot resize the file list"
        /// actually was.</para>
        /// <para>Deliberately NOT asserted: that the image pane keeps a MINIMUM. It can still legitimately
        /// arrive at zero here, because nothing yet decides what yields when the band cannot seat both
        /// panels -- that is a policy choice (hide the info panel? cap the file list?) and is queued in
        /// docs/todo/ui.md rather than invented here.</para>
        /// </remarks>
        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        [InlineData(2f)]
        public void HoweverFarTheDividerIsDraggedThePanesStillTileTheBand(float dpiScale)
        {
            using var renderer = new RgbaImageRenderer(NarrowW, NarrowH);
            var viewer = NewViewer(renderer, dpiScale);
            var state = NewState();

            // Far past the right edge, which is what over-commits the band.
            DragDividerTo(viewer, state, NarrowW * 4f);

            var fileList = viewer.FileList;
            var image = viewer.ImageArea;
            var info = viewer.InfoPanel;

            // Nothing negative.
            fileList.Width.ShouldBeGreaterThanOrEqualTo(0f);
            image.Width.ShouldBeGreaterThanOrEqualTo(0f);
            info.Width.ShouldBeGreaterThanOrEqualTo(0f);

            // Nothing overhangs the surface, and the info panel does not reach back over the divider.
            info.Right.ShouldBeLessThanOrEqualTo(NarrowW + 0.5f);
            info.X.ShouldBeGreaterThanOrEqualTo(fileList.Right);

            // The three panes plus the divider never claim more than the band has.
            (fileList.Width + image.Width + info.Width).ShouldBeLessThanOrEqualTo(NarrowW + 0.5f);
        }

        [Fact]
        public void InAWideWindowTheNominalMinimumStillApplies()
        {
            // The narrow-window fix must not remove the floor where there IS room for it: a 20 px file
            // list on a desktop window is a mis-drag, not a preference.
            using var renderer = new RgbaImageRenderer(2400, 1200);
            var viewer = NewViewer(renderer);
            var state = NewState();

            var after = DragDividerTo(viewer, state, 20f);

            after.ShouldBeGreaterThanOrEqualTo(ViewerState.FileListWidthBaseMin - 1f);
        }
    }
}
