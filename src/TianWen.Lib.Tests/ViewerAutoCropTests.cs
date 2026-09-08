using System;
using System.Drawing;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The viewer half of P25: what a crop does to the placement, and what it deliberately does not do.
    /// </summary>
    /// <remarks>
    /// It is a VIEW crop, so the assertions are about geometry rather than pixels: the crop is what gets
    /// fitted and centred, the drawn quad still covers the whole image behind a clip, and a rectangle that
    /// does not fit the loaded frame is ignored rather than obeyed.
    /// </remarks>
    [Collection("UI")]
    public class ViewerAutoCropTests
    {
        private const uint SurfaceW = 800;
        private const uint SurfaceH = 600;
        private const int ImageW = 400;
        private const int ImageH = 300;

        private sealed class CropViewer : ImageRendererBase<RgbaImage>
        {
            public CropViewer(RgbaImageRenderer renderer) : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
                FontPath = FontResolver.ResolveSystemFont();
            }

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels)
            {
                QuadLeft = left;
                QuadTop = top;
                QuadWidth = right - left;
                QuadHeight = bottom - top;
                Quads++;
            }

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

            public float QuadLeft { get; private set; }

            public float QuadTop { get; private set; }

            public float QuadWidth { get; private set; }

            public float QuadHeight { get; private set; }

            public int Quads { get; private set; }

            public RectF32 Shown => ShownImageRect;

            public RectF32 ImageArea => ImageAreaRect;
        }

        private static (CropViewer Viewer, ViewerState State) NewViewer()
        {
            var viewer = new CropViewer(new RgbaImageRenderer(SurfaceW, SurfaceH));
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);

            var state = new ViewerState
            {
                ShowFileList = false,
                ShowInfoPanel = false,
                ShowHistogram = false,
                StretchMode = StretchMode.None,
                ZoomToFit = true,
            };

            return (viewer, state);
        }

        /// <summary>
        /// Fit is against the CROP: a crop half the width fits at twice the scale, which is the whole point
        /// of cropping a border away rather than zooming past it.
        /// </summary>
        [Fact]
        public void FitScalesToTheCropRatherThanTheFrame()
        {
            var (viewer, state) = NewViewer();
            viewer.Render(null, state);
            var fullFrameZoom = state.Zoom;

            state.DisplayCrop = new Rectangle(0, 0, ImageW / 2, ImageH / 2);
            state.ZoomToFit = true;
            viewer.Render(null, state);

            state.Zoom.ShouldBe(fullFrameZoom * 2f, 0.001f);
        }

        /// <summary>
        /// The quad still covers the WHOLE image: nothing is resampled or discarded, the border is simply
        /// not rasterised. Its origin moves back by the crop's own offset so the crop lands where the
        /// frame used to.
        /// </summary>
        [Fact]
        public void TheQuadStillCoversTheWholeImage()
        {
            var (viewer, state) = NewViewer();
            state.DisplayCrop = new Rectangle(40, 30, 200, 150);
            viewer.Render(null, state);

            var scale = state.Zoom;
            viewer.QuadWidth.ShouldBe(ImageW * scale, 0.01f);
            viewer.QuadHeight.ShouldBe(ImageH * scale, 0.01f);

            // The crop's top-left, in screen coordinates, is where the shown region starts.
            (viewer.QuadLeft + (40 * scale)).ShouldBe(viewer.Shown.X, 0.01f);
            (viewer.QuadTop + (30 * scale)).ShouldBe(viewer.Shown.Y, 0.01f);
        }

        /// <summary>The shown region is the crop at the current scale, and it is what the clip narrows to.</summary>
        [Fact]
        public void TheShownRegionIsTheCrop()
        {
            var (viewer, state) = NewViewer();
            state.DisplayCrop = new Rectangle(40, 30, 200, 150);
            viewer.Render(null, state);

            viewer.Shown.Width.ShouldBe(200 * state.Zoom, 0.01f);
            viewer.Shown.Height.ShouldBe(150 * state.Zoom, 0.01f);
        }

        /// <summary>
        /// A crop that does not fit the loaded frame is IGNORED, not obeyed and not an error. That is what
        /// lets a crop survive a step to the next file: a folder of masters off one rig shares its ring,
        /// and a frame of another size simply shows in full.
        /// </summary>
        [Theory]
        [InlineData(-5, 0, 100, 100)]
        [InlineData(0, 0, ImageW + 1, 100)]
        [InlineData(ImageW - 10, 0, 100, 100)]
        [InlineData(0, 0, 0, 0)]
        public void ACropThatDoesNotFitIsIgnored(int x, int y, int w, int h)
        {
            var (viewer, state) = NewViewer();
            viewer.Render(null, state);
            var uncropped = viewer.Shown;

            state.DisplayCrop = new Rectangle(x, y, w, h);
            state.ZoomToFit = true;
            viewer.Render(null, state);

            viewer.Shown.Width.ShouldBe(uncropped.Width, 0.01f);
            viewer.Shown.Height.ShouldBe(uncropped.Height, 0.01f);
        }

        /// <summary>With no crop the placement is what it always was, which is the regression that matters
        /// most: every existing frame has to render byte-identically.</summary>
        [Fact]
        public void WithoutACropNothingMoves()
        {
            var (viewer, state) = NewViewer();
            viewer.Render(null, state);

            var area = viewer.ImageArea;
            var scale = state.Zoom;
            viewer.QuadWidth.ShouldBe(ImageW * scale, 0.01f);
            viewer.QuadLeft.ShouldBe(area.X + ((area.Width - (ImageW * scale)) / 2f), 0.01f);
            viewer.Shown.Width.ShouldBe(ImageW * scale, 0.01f);
        }
    }
}
