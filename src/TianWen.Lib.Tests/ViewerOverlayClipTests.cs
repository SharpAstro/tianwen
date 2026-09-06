using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// A sky overlay may not paint outside the image pane.
    /// </summary>
    /// <remarks>
    /// <para>Found by looking at the running viewer rather than by any test: with the object overlay on
    /// and the image zoomed, "NGC 2546" was painted across the toolbar and a column of Dobashi labels
    /// down the metadata panel. Markers are placed from the WCS, so an object near the frame's edge --
    /// or merely near it in the sky, since the gather covers the view rather than the sensor --
    /// projects outside the picture, and its label is drawn from the marker.</para>
    ///
    /// <para>The star overlay is the one exercised here because it needs neither a WCS nor a catalog,
    /// and it shares the defect for the same reason: its per-star test culls the WHOLLY outside and
    /// never trimmed a marker straddling the pane edge. The fix is one clip around every sky overlay,
    /// so this stands for all four.</para>
    /// </remarks>
    public sealed class ViewerOverlayClipTests
    {
        private const uint SurfaceW = 1000;
        private const uint SurfaceH = 700;
        private const int ImageW = 400;
        private const int ImageH = 300;

        [Fact]
        public async Task AZoomedStarOverlayStaysInsideTheImagePane()
        {
            var (viewer, state, document) = await NewViewerAsync();

            // Zoomed well past the pane, so a large share of the image -- and of its stars -- projects
            // outside it. At 1:1 the picture sits inside the pane and nothing could spill, which is why
            // the defect survived: the ordinary view never shows it.
            state.Zoom = 6f;
            state.ZoomToFit = false;

            // TWO renders, differing only in where the stars ARE. Both have 40 of them, so the
            // toolbar is pixel-identical between the two -- which matters, because its Stars button
            // lights up with the overlay and a diff against an overlay-off frame reports that button
            // as a spill (it did: 2,122 pixels, none of them a spill, and then 52 of the button's own
            // glyph when the diff was replaced by a colour test).
            //
            // Clustered at the centre, no marker can reach the pane edge at any zoom. Spread corner to
            // corner at zoom 6, most project outside it. So any pixel outside the pane that differs
            // between the two is overlay ink that escaped.
            state.ShowStarOverlay = true;

            document.Stars = Stars(centreOnly: true);
            var clustered = Snapshot(viewer, document, state);

            document.Stars = Stars(centreOnly: false);
            var spread = Snapshot(viewer, document, state);

            var pane = viewer.ImageArea;
            var insideChanged = 0;
            var outside = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (var y = 0; y < (int)SurfaceH; y++)
            {
                for (var x = 0; x < (int)SurfaceW; x++)
                {
                    var at = ((y * (int)SurfaceW) + x) * 4;
                    if (spread[at] == clustered[at]
                        && spread[at + 1] == clustered[at + 1]
                        && spread[at + 2] == clustered[at + 2])
                    {
                        continue;
                    }

                    if (x >= pane.X && x < pane.X + pane.Width && y >= pane.Y && y < pane.Y + pane.Height)
                    {
                        insideChanged++;
                    }
                    else
                    {
                        outside++;
                        minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                    }
                }
            }

            insideChanged.ShouldBeGreaterThan(0, "the two star sets drew the same pixels, so this proves nothing");
            outside.ShouldBe(0, $"{outside} pixels of star overlay landed outside the image pane ({pane.X},{pane.Y},{pane.Width}x{pane.Height}); they span ({minX},{minY})-({maxX},{maxY})");
        }

        /// <summary>
        /// Forty stars either clustered in the middle of the image or spread corner to corner. The
        /// COUNT is the same both ways on purpose: it is what keeps the chrome identical.
        /// </summary>
        private static StarList Stars(bool centreOnly)
        {
            var bag = new ConcurrentBag<ImagedStar>();
            for (var i = 0; i < 40; i++)
            {
                var t = i / 39f;
                var x = centreOnly ? (ImageW * 0.5f) + ((t - 0.5f) * 8f) : t * (ImageW - 1);
                var y = centreOnly ? (ImageH * 0.5f) + ((t - 0.5f) * 8f) : t * (ImageH - 1);
                bag.Add(new ImagedStar(HFD: 10f, StarFWHM: 10f, SNR: 100f, Flux: 1000f,
                    XCentroid: x, YCentroid: y, Ellipticity: 0f));
            }
            return new StarList(bag);
        }

        private static byte[] Snapshot(ClipViewer viewer, IPreviewSource source, ViewerState state)
        {
            Array.Clear(viewer.Pixels);
            viewer.Render(source, state);
            return (byte[])viewer.Pixels.Clone();
        }

        private static async Task<(ClipViewer Viewer, ViewerState State, AstroImageDocument Document)> NewViewerAsync()
        {
            var viewer = new ClipViewer(new RgbaImageRenderer(SurfaceW, SurfaceH));

            var plane = new float[ImageH, ImageW];
            for (var y = 0; y < ImageH; y++)
            {
                for (var x = 0; x < ImageW; x++)
                {
                    plane[y, x] = 1000f + (y * ImageW) + x;
                }
            }

            var document = await AstroImageDocument.AdoptImageAsync(
                new Image([plane], BitDepth.Int16, 65535f, 0f, 0f,
                    new ImageMeta("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                        0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                        RowOrder.TopDown, float.NaN, float.NaN)),
                DebayerAlgorithm.None);

            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);

            var state = new ViewerState
            {
                // Chrome ON: the panes it occupies are exactly where a spill would land, and a
                // chromeless viewer has no chrome to spill onto.
                HideChrome = false,
                ShowFileList = true,
                ShowInfoPanel = true,
                ShowHistogram = false,
                StretchMode = StretchMode.None,
            };
            state.ImageFileNames.Add("frame_01.fits");

            return (viewer, state, document);
        }

        /// <summary>
        /// A viewer whose image quad is stubbed but whose OVERLAYS really paint, into a buffer that can
        /// be read back. The quad is stubbed on purpose: what is under the overlay is not the question,
        /// and leaving it out makes any pixel that changes attributable to the overlay alone.
        /// </summary>
        private sealed class ClipViewer : ImageRendererBase<RgbaImage>
        {
            private readonly RgbaImageRenderer _renderer;

            public ClipViewer(RgbaImageRenderer renderer) : base(renderer)
            {
                _renderer = renderer;
                Width = renderer.Width;
                Height = renderer.Height;
                FontPath = BundledFonts.Resolve().Text ?? string.Empty;
            }

            public byte[] Pixels => _renderer.Surface.Pixels;

            public RectF32 ImageArea => ImageAreaRect;

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels) { }

            protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
                ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

            protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
                float angleRad, RGBAColor32 color, float thickness) =>
                _renderer.DrawEllipse(
                    new RectInt(
                        new PointInt((int)MathF.Round(cx - semiMajor), (int)MathF.Round(cy - semiMinor)),
                        new PointInt((int)MathF.Round(cx + semiMajor), (int)MathF.Round(cy + semiMinor))),
                    color, MathF.Max(1f, thickness));

            protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color)
            {
                _renderer.DrawLine(cx - armLength, cy, cx + armLength, cy, color, 1);
                _renderer.DrawLine(cx, cy - armLength, cx, cy + armLength, color, 1);
            }

            protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
                RGBAColor32 color, float thickness) =>
                _renderer.DrawLine(x0, y0, x1, y1, color, Math.Max(1, (int)MathF.Round(thickness)));

            protected override void OnResize(uint width, uint height) { }

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int imageWidth, int imageHeight) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;
        }
    }
}
