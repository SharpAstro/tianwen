using System;
using System.Collections.Generic;
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
    /// A toolbar button that RESERVES width for a mark has to paint one.
    /// </summary>
    /// <remarks>
    /// Reserving and drawing are two separate switches, <c>HasToolbarMark</c> and <c>DrawToolbarMark</c>,
    /// and nothing paired them. An action added to the first and forgotten in the second reserves its
    /// 13 px and paints nothing, so the button shows a hole with the label pushed across it, which reads
    /// as a missing font glyph rather than as a missing case. That is what the Crop button shipped with
    /// in P25, found by eye against the running viewer on 2026-09-08.
    /// <para>
    /// The assertion takes either signal because the two mark families reach the surface by different
    /// routes: the stroked marks go through the abstract overlay calls, which a headless viewer overrides,
    /// while the baked ones and the swatches go through <c>DrawCoverageMask</c> / <c>FillRect</c> onto the
    /// real raster. Counting only pixels would fail every stroked mark; counting only calls would fail
    /// every baked one.
    /// </para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerToolbarMarkTests
    {
        private const uint SurfaceW = 96;
        private const uint SurfaceH = 96;

        private sealed class MarkViewer : ImageRendererBase<RgbaImage>
        {
            public MarkViewer(RgbaImageRenderer renderer) : base(renderer)
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
                float rotationRad, RGBAColor32 color, float thickness) => OverlayDraws++;

            protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color)
                => OverlayDraws++;

            protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
                RGBAColor32 color, float thickness) => OverlayDraws++;

            protected override void OnResize(uint width, uint height) { }

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int width, int height) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;

            /// <summary>Stroked marks reach the surface through the overlay calls a headless viewer owns.</summary>
            public int OverlayDraws { get; private set; }

            /// <summary>Baked marks and swatches reach the raster directly.</summary>
            public byte[] Pixels => ((RgbaImageRenderer)Renderer).Surface.Pixels;
        }

        private static readonly RGBAColor32 Ink = new RGBAColor32(255, 255, 255, 255);

        private static MarkViewer NewViewer() => new MarkViewer(new RgbaImageRenderer(SurfaceW, SurfaceH));

        /// <summary>
        /// The invariant, over every action rather than the one that was reported: reserve the width and
        /// you owe a picture.
        /// </summary>
        [Fact]
        public void EveryButtonThatReservesMarkWidthPaintsOne()
        {
            var state = new ViewerState();
            var silent = new List<ToolbarAction>();
            var checkedAny = 0;

            foreach (var action in Enum.GetValues<ToolbarAction>())
            {
                var viewer = NewViewer();
                if (!viewer.ReservesToolbarMark(action, state))
                {
                    continue;
                }

                checkedAny++;
                var before = (byte[])viewer.Pixels.Clone();
                viewer.DrawToolbarMarkForTest(action, 8f, 8f, 48f, state, Ink);

                if (viewer.OverlayDraws == 0 && viewer.Pixels.SequenceEqual(before))
                {
                    silent.Add(action);
                }
            }

            // Without this the test passes on a viewer that reserves nothing at all, which is the shape
            // the bound would take if HasToolbarMark ever returned false everywhere.
            checkedAny.ShouldBeGreaterThan(4);
            silent.ShouldBeEmpty(
                $"these reserve mark width but paint nothing: {string.Join(", ", silent)}");
        }

        /// <summary>
        /// Two right angles, two strokes each. Pins the shape rather than only its presence, because a
        /// single stroke would satisfy the guard above and would not read as a crop.
        /// </summary>
        [Fact]
        public void TheCropMarkIsTwoRightAngles()
        {
            var viewer = NewViewer();

            viewer.DrawToolbarMarkForTest(ToolbarAction.AutoCrop, 8f, 8f, 48f, new ViewerState(), Ink);

            viewer.OverlayDraws.ShouldBe(4);
        }
    }
}
