using System;
using System.Drawing;
using System.Threading.Tasks;
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
                if (slot == RenditionSlot.Comparison)
                {
                    ComparisonQuad = new RectF32(left, top, right - left, bottom - top);
                    ComparisonSampledBefore = sampleBeforeChannels;
                }
            }

            /// <summary>Where the A/B comparison half's quad was placed, and whether it sampled the
            /// retained pixels rather than the live ones.</summary>
            public RectF32 ComparisonQuad { get; private set; }

            public bool ComparisonSampledBefore { get; private set; }

            public override bool HasBeforeImageTextures => BeforeSize is { Width: > 0 };

            public override (int Width, int Height) BeforeImageSize => BeforeSize;

            public (int Width, int Height) BeforeSize { get; set; }

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

            // ---- the cached-layer seam, so a test can drive the path the GPU hosts take ----
            //
            // Every hook defaults to "unsupported", so the tests that do not opt in
            // (UseCachedImageLayer stays false) render exactly as they did.

            protected override int CachedLayerSlotCount => 1;

            protected override bool TryEnsureCachedLayerTargets(int width, int height,
                out int capacityWidth, out int capacityHeight)
            {
                capacityWidth = width;
                capacityHeight = height;
                CapacityW = width;
                CapacityH = height;
                return true;
            }

            protected override bool TryBeginCachedLayerPass(int width, int height) => true;

            protected override void EndCachedLayerPass() { }

            protected override bool TryDrawCachedLayer(int slot, float x, float y, float w, float h,
                float u0, float v0, float u1, float v1)
            {
                Blit = new RectF32(x, y, w, h);
                BlitU = (u0, u1);
                BlitV = (v0, v1);
                Blits++;
                return true;
            }

            public RectF32 Blit { get; private set; }

            public (float Lo, float Hi) BlitU { get; private set; }

            public (float Lo, float Hi) BlitV { get; private set; }

            public int Blits { get; private set; }

            public int CapacityW { get; private set; }

            public int CapacityH { get; private set; }

            public float QuadLeft { get; private set; }

            public float QuadTop { get; private set; }

            public float QuadWidth { get; private set; }

            public float QuadHeight { get; private set; }

            public int Quads { get; private set; }

            public RectF32 Shown => ShownImageRect;

            public RectF32 LastComparisonHalfClipForTest => LastComparisonHalfClip;

            public RectF32 LastLiveHalfClipForTest => LastLiveHalfClip;

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

        /// <summary>
        /// Nothing outside the crop can reach the screen, at ANY zoom or pan. Reported 2026-09-08 as a
        /// ragged top edge still showing with a crop applied, at a zoom well above fit.
        /// </summary>
        /// <remarks>
        /// Fit is the easy case and the other tests cover it. Zoomed IN, the shown region is larger than
        /// the pane, so the clip degenerates to the pane and the quad extends past it in both
        /// directions: what then keeps the border off screen is ConfineToViewport clamping the pan, not
        /// the clip. This maps the clip back through the placement into image coordinates and asserts
        /// the result lies inside the crop, which is the property the user was looking at.
        /// </remarks>
        [Theory]
        [InlineData(400f, 300f)]
        [InlineData(-400f, -300f)]
        [InlineData(5000f, 5000f)]
        [InlineData(-5000f, -5000f)]
        public void NothingOutsideTheCropReachesTheScreenWhenZoomedIn(float panX, float panY)
        {
            var (viewer, state) = NewViewer();
            var crop = new Rectangle(40, 30, 200, 150);
            state.DisplayCrop = crop;

            // Well above fit, so the shown region overflows the pane on both axes.
            state.ZoomToFit = false;
            state.Zoom = 4f;
            state.PanOffset = (panX, panY);
            viewer.Render(null, state);

            var area = viewer.ImageArea;
            var shown = viewer.Shown;
            var scale = state.Zoom;

            // The clip the renderer declares: the pane narrowed to the shown region.
            var clipX0 = MathF.Max(area.X, shown.X);
            var clipY0 = MathF.Max(area.Y, shown.Y);
            var clipX1 = MathF.Min(area.X + area.Width, shown.X + shown.Width);
            var clipY1 = MathF.Min(area.Y + area.Height, shown.Y + shown.Height);

            // Back through the placement into image pixels.
            var imgX0 = (clipX0 - viewer.QuadLeft) / scale;
            var imgY0 = (clipY0 - viewer.QuadTop) / scale;
            var imgX1 = (clipX1 - viewer.QuadLeft) / scale;
            var imgY1 = (clipY1 - viewer.QuadTop) / scale;

            const float Tolerance = 0.01f;
            imgX0.ShouldBeGreaterThanOrEqualTo(crop.X - Tolerance);
            imgY0.ShouldBeGreaterThanOrEqualTo(crop.Y - Tolerance);
            imgX1.ShouldBeLessThanOrEqualTo(crop.Right + Tolerance);
            imgY1.ShouldBeLessThanOrEqualTo(crop.Bottom + Tolerance);
        }

        /// <summary>
        /// The CACHED-LAYER blit is narrowed to the crop too. Reported 2026-09-09: zoom out and the
        /// discarded border comes back, while the status bar still says the frame is cropped.
        /// </summary>
        /// <remarks>
        /// <para>The uncached path clips the quad to the shown region; the cached path returned before
        /// ever reaching that, clipping to the PANE alone. At fit the border falls outside the pane, so
        /// the pane clip hid it -- zoom out and the whole frame fits inside the pane, and the blit
        /// painted the border back. With 1309 blits against 253 renders in the reported session, the
        /// cached path is the one a user actually looks at.</para>
        /// <para><b>This asserts what the renderer DECLARED, which is the point.</b> Its sibling above
        /// re-derives the clip from the placement inside the test body, so it models ClipToShown rather
        /// than observing it and stayed green through the whole bug. Delete the narrowing in
        /// TryDrawImageFromCachedLayer and this fails; that one still passes.</para>
        /// </remarks>
        [Fact]
        public void TheCachedLayerBlitIsNarrowedToTheCrop()
        {
            var (viewer, state) = NewViewer();
            var crop = new Rectangle(40, 30, 200, 150);
            viewer.UseCachedImageLayer = true;
            state.DisplayCrop = crop;

            // Zoomed OUT far enough that the whole frame, border included, fits inside the pane. That is
            // the regime the pane clip cannot help with.
            state.ZoomToFit = false;
            state.Zoom = 0.5f;

            // PrepareCachedImageLayer renders the layer, Render then blits out of it -- both in the
            // same frame, which is how the hosts drive it.
            viewer.PrepareFrame(null, state);
            viewer.PrepareCachedImageLayer();
            viewer.Render(null, state);

            viewer.Blits.ShouldBe(1, "the frame must come from the layer, or this proves nothing");

            var shown = viewer.Shown;
            var blit = viewer.Blit;
            const float Tolerance = 0.01f;
            blit.X.ShouldBeGreaterThanOrEqualTo(shown.X - Tolerance);
            blit.Y.ShouldBeGreaterThanOrEqualTo(shown.Y - Tolerance);
            (blit.X + blit.Width).ShouldBeLessThanOrEqualTo(shown.X + shown.Width + Tolerance);
            (blit.Y + blit.Height).ShouldBeLessThanOrEqualTo(shown.Y + shown.Height + Tolerance);

            // Not degenerate: the crop itself is what is drawn, at this zoom entirely inside the pane.
            blit.Width.ShouldBe(crop.Width * state.Zoom, Tolerance);
            blit.Height.ShouldBe(crop.Height * state.Zoom, Tolerance);

            // The SOURCE rectangle has to shrink with the destination. Narrowing the destination alone
            // would sample the whole pane into the crop's rectangle, which squashes the picture rather
            // than cropping it -- and it would still look plausible at a glance.
            ((viewer.BlitU.Hi - viewer.BlitU.Lo) * viewer.CapacityW).ShouldBe(blit.Width, Tolerance);
            ((viewer.BlitV.Hi - viewer.BlitV.Lo) * viewer.CapacityH).ShouldBe(blit.Height, Tolerance);
        }

        /// <summary>
        /// Once an enhance has BAKED a crop in, the crop button is disabled: the pixels are the crop, so
        /// there is nothing left to take off -- and nothing to put back either, since both scan tiers are
        /// blind on enhanced pixels. Reverting the enhance is what restores the full frame and its crop.
        /// </summary>
        /// <remarks>
        /// Asserted through <c>TryGetPaintedToolbarRect</c>, which answers only for REGISTERED buttons,
        /// i.e. the clickable ones. <c>PaintedToolbarButtons</c> would not do: a disabled button is still
        /// laid out, so it stays in that list by design.
        /// </remarks>
        [Fact]
        public async Task TheCropButtonIsDisabledOnceAnEnhanceHasBakedACropIn()
        {
            var ct = TestContext.Current.CancellationToken;
            var (viewer, state) = NewViewer();

            var whole = await AstroImageDocument.AdoptImageAsync(SyntheticFrame(), DebayerAlgorithm.None,
                filePath: "master.fits", cancellationToken: ct);
            viewer.Render(whole, state);
            viewer.TryGetPaintedToolbarRect(ToolbarAction.AutoCrop, out _)
                .ShouldBeTrue("a whole frame is exactly what the crop button is for");

            var baked = await AstroImageDocument.AdoptImageAsync(SyntheticFrame(), DebayerAlgorithm.None,
                filePath: "master.fits", sourceCrop: new Rectangle(8, 6, ImageW, ImageH), cancellationToken: ct);
            viewer.Render(baked, state);
            viewer.TryGetPaintedToolbarRect(ToolbarAction.AutoCrop, out _)
                .ShouldBeFalse("these pixels ARE the crop");
        }

        /// <summary>
        /// The A/B "before" half is placed by ITS OWN frame. Reported 2026-09-09 as "A|B is warping",
        /// with the before half showing the uncropped image.
        /// </summary>
        /// <remarks>
        /// An enhance on a cropped view is cut before the pipeline sees it, so the live frame is the crop
        /// while the retained textures are the uncropped original -- two different frames in one
        /// comparison. Drawn on the live quad the before half stretches by the crop's ratio (here
        /// 400/384 across and 300/282 down) and brings the canvas ring back with it. The fix is pure
        /// geometry: same scale, origin backed off by the crop's offset, so the crop's pixel (0,0) lands
        /// where the live half draws it.
        /// </remarks>
        [Fact]
        public async Task TheBeforeHalfIsPlacedByItsOwnFrameWhenAnEnhanceBakedACropIn()
        {
            var ct = TestContext.Current.CancellationToken;
            var (viewer, state) = NewViewer();
            var crop = new Rectangle(8, 6, ImageW - 16, ImageH - 18);

            // The live document is the crop; the retained textures are the frame it came from.
            var baked = await AstroImageDocument.AdoptImageAsync(SyntheticFrame(crop.Width, crop.Height),
                DebayerAlgorithm.None, filePath: "master.fits", sourceCrop: crop, cancellationToken: ct);
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, crop.Width, crop.Height);
            viewer.BeforeSize = (ImageW, ImageH);
            viewer.Split.Mode = SplitCompare.BeforePixels;
            viewer.Split.Toggle(hasBeforePixels: true);

            viewer.Render(baked, state);

            viewer.ComparisonSampledBefore.ShouldBeTrue("the left half is the retained pixels");
            var scale = state.Zoom;
            var compare = viewer.ComparisonQuad;

            // Same scale as the live half, sized to the ORIGINAL frame...
            compare.Width.ShouldBe(ImageW * scale, 0.01f);
            compare.Height.ShouldBe(ImageH * scale, 0.01f);

            // ...and offset so the crop's own origin lands on the live half's origin.
            (compare.X + (crop.X * scale)).ShouldBe(viewer.QuadLeft, 0.01f);
            (compare.Y + (crop.Y * scale)).ShouldBe(viewer.QuadTop, 0.01f);
        }

        /// <summary>
        /// Crop, then A/B, with nothing enhanced: the halves compare CROPPED against UNCROPPED, which is
        /// the question a crop raises and which no other mode can answer.
        /// </summary>
        /// <remarks>
        /// Comparing pixels needs an enhance to have retained some, and comparing settings puts two
        /// identically-framed halves on screen. So a crop with nothing enhanced used to light up the
        /// settings comparison, i.e. the least informative of the three. The implementation is one
        /// clip: the quad already covers the whole frame at the right place, so the comparison half
        /// simply is not narrowed to the crop.
        /// </remarks>
        [Fact]
        public void ACropWithNoEnhanceComparesCroppedAgainstUncropped()
        {
            var (viewer, state) = NewViewer();
            state.DisplayCrop = new Rectangle(40, 30, 200, 150);
            viewer.Render(null, state);

            viewer.Split.Toggle(hasBeforePixels: false, hasCrop: true);
            viewer.Split.Mode.ShouldBe(SplitCompare.CropExtent);
            viewer.Split.HalfLabels(DisplayControls.Defaults).ShouldBe(("Uncropped", "Cropped"));

            viewer.Render(null, state);

            // Both halves must actually have been drawn: an empty clip would satisfy every bound below
            // by drawing nothing, which is how a split that never engaged reads as a passing test.
            viewer.LastComparisonHalfClipForTest.Width.ShouldBeGreaterThan(1f);
            viewer.LastLiveHalfClipForTest.Width.ShouldBeGreaterThan(1f);

            // The live half is bounded by the crop; the comparison half is not, which is what lets it
            // show the band the crop took off.
            var shown = viewer.Shown;
            const float Tolerance = 0.01f;
            viewer.LastLiveHalfClipForTest.Width.ShouldBeLessThanOrEqualTo(shown.Width + Tolerance);
            viewer.LastComparisonHalfClipForTest.X.ShouldBeLessThan(shown.X - Tolerance);
        }

        /// <summary>
        /// The BUTTON picks the same comparison as the key. The viewer has two press dispatchers and
        /// they have silently disagreed before (P17's single-click), so the toolbar path is asserted
        /// separately rather than assumed to share the keyboard's.
        /// </summary>
        [Fact]
        public void TheCompareButtonPicksTheCropComparisonToo()
        {
            var (viewer, state) = NewViewer();
            state.DisplayCrop = new Rectangle(40, 30, 200, 150);
            viewer.Render(null, state);

            ViewerActions.HandleToolbarAction(state, document: null, ToolbarAction.Compare,
                split: viewer.Split, hasBeforePixels: false, hasCrop: viewer.HasDisplayCrop);

            viewer.HasDisplayCrop.ShouldBeTrue("the renderer is what knows a crop is in force");
            viewer.Split.Mode.ShouldBe(SplitCompare.CropExtent);
            viewer.Split.IsOn.ShouldBeTrue();
        }

        /// <summary>
        /// After an enhance BAKED a crop in, the two halves show the SAME region and differ only by the
        /// enhancement -- the opposite rule to the one above, and deliberately so.
        /// </summary>
        /// <remarks>
        /// The retained texture is the uncropped original, so without this the left half also changes
        /// the FRAME and the comparison answers two questions at once. The placement already aligns the
        /// two; this bounds the before half to the live quad so it stops there.
        /// </remarks>
        [Fact]
        public async Task AnEnhancedCropComparesTheSameRegionOnBothHalves()
        {
            var ct = TestContext.Current.CancellationToken;
            var (viewer, state) = NewViewer();
            var crop = new Rectangle(8, 6, ImageW - 16, ImageH - 18);

            var baked = await AstroImageDocument.AdoptImageAsync(SyntheticFrame(crop.Width, crop.Height),
                DebayerAlgorithm.None, filePath: "master.fits", sourceCrop: crop, cancellationToken: ct);
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, crop.Width, crop.Height);
            viewer.BeforeSize = (ImageW, ImageH);
            viewer.Split.Mode = SplitCompare.BeforePixels;
            viewer.Split.Toggle(hasBeforePixels: true);

            viewer.Render(baked, state);

            // The comparison half stops at the live quad: no part of it shows frame the After half lacks.
            var compare = viewer.LastComparisonHalfClipForTest;
            compare.Width.ShouldBeGreaterThan(1f, "an empty clip would pass every bound below");
            const float Tolerance = 0.01f;
            compare.X.ShouldBeGreaterThanOrEqualTo(viewer.QuadLeft - Tolerance);
            compare.Y.ShouldBeGreaterThanOrEqualTo(viewer.QuadTop - Tolerance);
            (compare.X + compare.Width).ShouldBeLessThanOrEqualTo(viewer.QuadLeft + viewer.QuadWidth + Tolerance);
            (compare.Y + compare.Height).ShouldBeLessThanOrEqualTo(viewer.QuadTop + viewer.QuadHeight + Tolerance);
        }

        private static Image SyntheticFrame(int width = ImageW, int height = ImageH)
        {
            var plane = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    plane[y, x] = 0.25f;
                }
            }

            return new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
                imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
        }

        /// <summary>With no crop the blit is the whole pane, unchanged: the ordinary path must not pay
        /// for the crop's narrowing.</summary>
        [Fact]
        public void WithoutACropTheCachedLayerBlitIsTheWholePane()
        {
            var (viewer, state) = NewViewer();
            viewer.UseCachedImageLayer = true;
            state.ZoomToFit = false;
            state.Zoom = 0.5f;

            viewer.PrepareFrame(null, state);
            viewer.PrepareCachedImageLayer();
            viewer.Render(null, state);

            viewer.Blits.ShouldBe(1);
            var area = viewer.ImageArea;
            const float Tolerance = 0.01f;
            viewer.Blit.X.ShouldBe(area.X, Tolerance);
            viewer.Blit.Y.ShouldBe(area.Y, Tolerance);
            viewer.Blit.Width.ShouldBe(area.Width, Tolerance);
            viewer.Blit.Height.ShouldBe(area.Height, Tolerance);
        }
    }
}
