using System;
using System.Collections.Generic;
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
    /// What the SCREEN looks like after a narrowed repaint, as opposed to which rects were declared.
    /// </summary>
    /// <remarks>
    /// <para><b>The sibling suite <see cref="ViewerFrameDamageTests"/> asserts the declaration; this one
    /// asserts the consequence, and only this one can catch a rect that is honestly declared and simply
    /// too small.</b> Every residue bug found here passed there: the mouse-move narrowing really does
    /// declare the two rects a readout is shown in, and the split drag really does declare the strip its
    /// divider crossed. Neither covers the chrome that TRAVELS with the pointer.</para>
    /// <para>It models the real thing, and the model got one thing wrong for a while. The host preserves
    /// the previous frame and confines painting to the union of the declared rects (a bounding box, one
    /// scissor per draw), so the screen is the OLD frame everywhere and, inside the box, whatever this
    /// frame actually DREW. The first version read that second half as "the new frame's pixels", which is
    /// the same statement only where the app paints every pixel of the box. It does not: nothing filled
    /// the image pane, so its letterbox was the render pass's clear colour on a full frame and last
    /// frame's contents on a partial one. That is P24, and this suite could not see it (an unpainted pixel
    /// is the same black in both frames when both are painted over a cleared surface).</para>
    /// <para>So which pixels are drawn is MEASURED: the frame is painted over two different sentinel
    /// grounds, and a pixel is untouched exactly when it comes back as its own sentinel both times.
    /// Compositing the new frame straight onto the old one would answer the same question and answer it
    /// wrongly, because an alpha-blended panel over its own previous pixels converges to a different
    /// colour than the same panel over a cleared surface: that effect differed a no-input control frame
    /// from its predecessor by 150,307 pixels, swamping any real residue and reading exactly like one.
    /// Every frame here is therefore painted over a uniform ground, never over another frame.</para>
    /// <para>The control test below is what says the number means anything at all: it must be zero, or
    /// every assertion here is measuring the harness.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerRepaintResidueTests
    {
        private const uint SurfaceW = 2000;
        private const uint SurfaceH = 900;
        private const int ImageW = 900;
        private const int ImageH = 500;

        /// <summary>
        /// Two frames with nothing happening between them must be identical, or nothing else here is a
        /// measurement. This is the test that made the first run of the others meaningless.
        /// </summary>
        [Fact]
        public async Task TwoFramesWithNoInputAreTheSameFrame()
        {
            var (viewer, state, document) = await NewViewerAsync();
            Paint(viewer, document, state);
            var one = Snapshot(viewer);
            Paint(viewer, document, state);

            Differences(one, Snapshot(viewer)).ShouldBe(0);
        }

        /// <summary>
        /// Dragging the before/after divider must leave nothing of the half labels behind.
        /// </summary>
        /// <remarks>
        /// The labels are right- and left-aligned AGAINST the divider, so they travel with it and the
        /// strip that changed is wider than the divider's own path. That was covered by a guessed
        /// constant -- 220 design units of slack each side, applied to a surface-pixel coordinate, so it
        /// was also short by the DPI factor on any scaled display. The left label is the pinned one,
        /// which names what differs, and it outgrows 220 units as soon as two controls differ.
        /// </remarks>
        [Theory]
        [InlineData(1.0f)]
        [InlineData(1.5f)]
        public async Task DraggingTheSplitDividerLeavesNoLabelBehind(float dpi)
        {
            var (viewer, state, document) = await NewViewerAsync(dpi);

            // Both labels name what differs from the other half, so both are made long: pin one set of
            // controls, then move to a different one. A bare "Pinned"/"Live" pair fits inside any
            // guessed margin, which is why the guess survived until a real comparison was on screen.
            state.CurvesBoost = 0.6f;
            state.HdrAmount = 0.4f;
            viewer.Split.Toggle(hasBeforePixels: false);
            Paint(viewer, document, state);
            state.CurvesBoost = 0f;
            state.HdrAmount = 0f;
            state.ManualWhiteBalance = (1.3f, 1f, 0.8f);
            state.BackgroundNeutralizationEnabled = true;
            Paint(viewer, document, state);
            viewer.TryTakeFrameDamage([]);

            var track = viewer.ImageArea;
            viewer.Split.BeginDrag();
            var before = Snapshot(viewer);

            // A slow, deliberate drag to the RIGHT -- the gesture the narrowing exists for.
            viewer.HandleInput(new InputEvent.MouseMove(track.X + track.Width * 0.56f, track.Y + 40f));

            LoadOpResidue(viewer, document, state, before, out _).ShouldBe(0);
        }

        /// <summary>
        /// The divider's BAR, where the picture is not: reported 2026-09-07 as
        /// <i>"the A|B slider vertical bar can leave residue in the non-imaging canvas area"</i> (P24).
        /// </summary>
        /// <remarks>
        /// <para>Same class as the label residue above and the same gesture, but a different question, and
        /// the reason the label test could not see it: the sweep DOES cover this strip
        /// (<c>Split.SetTrack(_layout.ImageArea)</c> is the pane and <c>SweepBetween</c> spans its full
        /// height), so damage is not under-declared. What fails is the assumption that a declared pixel is
        /// a repainted one.</para>
        /// <para>Measured through <see cref="LoadOpResidue"/>, which is the host's real arithmetic: a
        /// partial frame loads the previous image and scissors to the box, so a pixel nobody draws keeps
        /// what it had. Every other test in this file paints over a cleared surface, where an undrawn
        /// pixel is black in both frames and cannot differ.</para>
        /// </remarks>
        [Fact]
        public async Task DraggingTheSplitDividerLeavesNothingBehindWhereThePictureIsNot()
        {
            var (viewer, state, document) = await NewViewerAsync();

            state.CurvesBoost = 0.6f;
            viewer.Split.Toggle(hasBeforePixels: false);
            Paint(viewer, document, state);
            state.CurvesBoost = 0f;
            state.ManualWhiteBalance = (1.3f, 1f, 0.8f);
            Paint(viewer, document, state);
            viewer.TryTakeFrameDamage([]);

            var track = viewer.ImageArea;
            viewer.Split.BeginDrag();
            var before = Snapshot(viewer);

            viewer.HandleInput(new InputEvent.MouseMove(track.X + track.Width * 0.56f, track.Y + 40f));

            LoadOpResidue(viewer, document, state, before, out var where).ShouldBe(0, where);
        }

        /// <summary>
        /// Moving off a toolbar button onto the canvas must take its tooltip with it.
        /// </summary>
        /// <remarks>
        /// That move changes two things at once: the hovered button (whose branch asks for a repaint and
        /// falls THROUGH) and the pixel readout (whose branch narrows). The narrowing wins, so the
        /// tooltip's own rect is never declared. It is drawn last of all, over everything, so the part of
        /// it outside the declared box survives -- and because damage is tracked per swapchain image, it
        /// then survives in some images and not others, which is why this reads as a flicker rather than
        /// as a stuck tooltip.
        /// </remarks>
        [Fact]
        public async Task LeavingAToolbarButtonForTheCanvasTakesItsTooltipWithIt()
        {
            var (viewer, state, document) = await NewViewerAsync();
            Paint(viewer, document, state);

            // Onto a toolbar button, so its tooltip is up.
            var toolbar = viewer.Toolbar;
            viewer.HandleInput(new InputEvent.MouseMove(toolbar.X + 60f, toolbar.Y + toolbar.Height / 2f));
            Paint(viewer, document, state);
            viewer.TryTakeFrameDamage([]);
            var before = Snapshot(viewer);

            // ... and off it, onto the image.
            var image = viewer.CurrentImageRect;
            viewer.HandleInput(new InputEvent.MouseMove(image.Center.X, image.Center.Y));

            LoadOpResidue(viewer, document, state, before, out _).ShouldBe(0);
        }

        /// <summary>
        /// Moving off the image into the file list must bring the row highlight with it.
        /// </summary>
        /// <remarks>
        /// The same collision as the tooltip above, and the gesture the original report named. The row
        /// hover branch asks for a repaint and falls through to a narrowing that does not include the
        /// pane -- so with the info panel CLOSED (the declared box is then the status strip alone) the
        /// highlight does not arrive until something unrelated forces a frame.
        /// </remarks>
        [Fact]
        public async Task MovingIntoTheFileListBringsItsRowHighlightWithIt()
        {
            var (viewer, state, document) = await NewViewerAsync();
            state.ShowInfoPanel = false;
            Paint(viewer, document, state);

            var image = viewer.CurrentImageRect;
            viewer.HandleInput(new InputEvent.MouseMove(image.X + 20f, image.Center.Y));
            Paint(viewer, document, state);
            viewer.TryTakeFrameDamage([]);
            var before = Snapshot(viewer);

            var list = viewer.FileList;
            viewer.HandleInput(new InputEvent.MouseMove(list.X + list.Width / 2f, list.Y + 40f));

            LoadOpResidue(viewer, document, state, before, out _).ShouldBe(0);
        }

        /// <summary>
        /// The case the narrowing exists for still narrows -- or the fix above has quietly turned the
        /// most frequent redraw in the app back into a full repaint.
        /// </summary>
        [Fact]
        public async Task AMoveWithinTheImageStillRepaintsOnlyTheReadout()
        {
            var (viewer, state, document) = await NewViewerAsync();
            Paint(viewer, document, state);

            // ON the image, not merely in its pane: the picture is centred in the pane, so a point near
            // the pane's top edge resolves to no pixel at all and the readout never changes.
            var image = viewer.CurrentImageRect;
            viewer.HandleInput(new InputEvent.MouseMove(image.X + image.Width * 0.4f, image.Y + image.Height * 0.4f))
                .ShouldBeTrue("the readout has to actually change for there to be anything to narrow");
            viewer.TryTakeFrameDamage([]);
            viewer.HandleInput(new InputEvent.MouseMove(image.X + image.Width * 0.6f, image.Y + image.Height * 0.6f))
                .ShouldBeTrue();

            var damage = new List<RectF32>();
            viewer.TryTakeFrameDamage(damage).ShouldBeTrue(
                "a pointer travelling across the image is the 8%-GPU case this whole mechanism exists for");
            damage.ShouldContain(viewer.StatusBar);
        }

        /// <summary>
        /// How many pixels the screen would show stale: the previous frame everywhere, the new frame
        /// inside the declared box, compared against the new frame painted in full.
        /// </summary>
        /// <summary>
        /// The residue a partial frame really shows, which is stricter than <see cref="Residue"/> in the
        /// one way that matters outside a filled region.
        /// </summary>
        /// <remarks>
        /// <para><b>The host does not clear a partial frame.</b> <c>VulkanContext.BeginFrameRenderPass</c>
        /// takes the <c>VkAttachmentLoadOp.Load</c> pass and sets the scissor to the damage box, so inside
        /// that box a pixel is replaced only by what the app actually DRAWS. <see cref="Residue"/> models
        /// the box as "the new frame's pixels", which is the same thing only where the app paints every
        /// pixel in it, and the image pane's letterbox is where it does not.</para>
        /// <para><b>Which pixels are drawn is measured, not assumed</b>, by painting the frame twice over
        /// two different sentinel grounds: a pixel is untouched exactly when it comes back as its own
        /// sentinel both times. Compositing the new frame straight onto the old one would answer this
        /// question too, and wrongly: an alpha-blended panel over its own previous pixels converges to a
        /// different colour than the same panel over a cleared surface, which is the effect that swamped
        /// the first version of this file (150,307 pixels on a no-input frame) and swamps this measurement
        /// the same way.</para>
        /// </remarks>
        private static int LoadOpResidue(ResidueViewer viewer, IPreviewSource source, ViewerState state,
            byte[] before, out string where)
        {
            where = "none";
            var damage = new List<RectF32>();
            var narrow = viewer.TryTakeFrameDamage(damage);

            // What a full repaint would show: the answer the partial frame has to match.
            Paint(viewer, source, state);
            var expected = Snapshot(viewer);
            if (!narrow)
            {
                return 0;
            }

            var onMagenta = PaintOver(viewer, source, state, new RGBAColor32(0xff, 0x00, 0xff, 0xff));
            var onGreen = PaintOver(viewer, source, state, new RGBAColor32(0x00, 0xff, 0x00, 0xff));

            var box = BoundingBox(damage);
            var x0 = box.X < 0f ? 0 : (int)box.X;
            var y0 = box.Y < 0f ? 0 : (int)box.Y;
            var x1 = Math.Min((int)SurfaceW, (int)MathF.Ceiling(box.X + box.Width));
            var y1 = Math.Min((int)SurfaceH, (int)MathF.Ceiling(box.Y + box.Height));

            // The frame as the host builds it: last frame's pixels everywhere, replaced inside the box
            // only where this frame drew something.
            var shown = (byte[])before.Clone();
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var i = ((y * (int)SurfaceW) + x) * 4;
                    var untouched = onMagenta[i] == 0xff && onMagenta[i + 1] == 0x00 && onMagenta[i + 2] == 0xff
                        && onGreen[i] == 0x00 && onGreen[i + 1] == 0xff && onGreen[i + 2] == 0x00;
                    if (untouched)
                    {
                        continue;
                    }

                    shown[i] = expected[i];
                    shown[i + 1] = expected[i + 1];
                    shown[i + 2] = expected[i + 2];
                    shown[i + 3] = expected[i + 3];
                }
            }

            where = $"{DiffBounds(shown, expected)}; damage box {box}";
            return Differences(shown, expected);
        }

        /// <summary>A frame painted over a uniform sentinel ground, for the drawn-pixel mask.</summary>
        private static byte[] PaintOver(ResidueViewer viewer, IPreviewSource source, ViewerState state,
            RGBAColor32 ground)
        {
            var pixels = viewer.Pixels;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = ground.Red;
                pixels[i + 1] = ground.Green;
                pixels[i + 2] = ground.Blue;
                pixels[i + 3] = ground.Alpha;
            }

            viewer.Render(source, state);
            return Snapshot(viewer);
        }

        private static string DiffBounds(byte[] shown, byte[] expected)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, n = 0;
            for (var y = 0; y < (int)SurfaceH; y++)
            {
                for (var x = 0; x < (int)SurfaceW; x++)
                {
                    var i = ((y * (int)SurfaceW) + x) * 4;
                    if (shown[i] != expected[i] || shown[i + 1] != expected[i + 1] || shown[i + 2] != expected[i + 2])
                    {
                        n++;
                        if (x < x0) { x0 = x; }
                        if (x > x1) { x1 = x; }
                        if (y < y0) { y0 = y; }
                        if (y > y1) { y1 = y; }
                    }
                }
            }
            return n == 0 ? "none" : $"{n} px in x[{x0}..{x1}] y[{y0}..{y1}]";
        }

        /// <summary>The one scissor the host sets: a bounding box over every declared rect.</summary>
        private static RectF32 BoundingBox(List<RectF32> rects)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var r in rects)
            {
                x0 = MathF.Min(x0, r.X);
                y0 = MathF.Min(y0, r.Y);
                x1 = MathF.Max(x1, r.X + r.Width);
                y1 = MathF.Max(y1, r.Y + r.Height);
            }
            return new RectF32(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// A frame painted from a CLEARED surface -- what a full repaint puts in a swapchain image.
        /// Without the clear an alpha-blended fill converges over repeated paints and every frame
        /// differs from the last.
        /// </summary>
        private static void Paint(ResidueViewer viewer, IPreviewSource source, ViewerState state)
        {
            Array.Clear(viewer.Pixels);
            viewer.Render(source, state);
        }

        private static byte[] Snapshot(ResidueViewer viewer) => (byte[])viewer.Pixels.Clone();

        private static int Differences(byte[] shown, byte[] expected)
        {
            var count = 0;
            for (var i = 0; i < shown.Length; i += 4)
            {
                if (shown[i] != expected[i] || shown[i + 1] != expected[i + 1] || shown[i + 2] != expected[i + 2])
                {
                    count++;
                }
            }
            return count;
        }

        private static async Task<(ResidueViewer Viewer, ViewerState State, AstroImageDocument Document)>
            NewViewerAsync(float dpi = 1.0f)
        {
            var viewer = new ResidueViewer(new RgbaImageRenderer(SurfaceW, SurfaceH)) { DpiScale = dpi };
            var plane = new float[ImageH, ImageW];
            for (var y = 0; y < ImageH; y++)
            {
                for (var x = 0; x < ImageW; x++)
                {
                    plane[y, x] = 1000f + y * ImageW + x;
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
                HideChrome = false,
                ShowFileList = true,
                ShowInfoPanel = true,
                ShowHistogram = false,
                StretchMode = StretchMode.None,
                Zoom = 1f,
                ZoomToFit = false,
            };
            state.ImageFileNames.Add("frame_01.fits");
            state.ImageFileNames.Add("frame_02.fits");
            state.ImageFileNames.Add("frame_03.fits");
            return (viewer, state, document);
        }

        /// <summary>
        /// A viewer whose GPU work is stubbed but whose CHROME really paints, into a pixel buffer that
        /// can be read back. A real font is resolved on purpose: every residue here is text.
        /// </summary>
        private sealed class ResidueViewer : ImageRendererBase<RgbaImage>
        {
            public ResidueViewer(RgbaImageRenderer renderer) : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
                var fonts = BundledFonts.Resolve();
                FontPath = fonts.Text ?? string.Empty;
            }

            public byte[] Pixels => ((RgbaImageRenderer)Renderer).Surface.Pixels;

            /// <summary>The surface itself, for the clip that models the host's scissor.</summary>
            public RgbaImage Surface => ((RgbaImageRenderer)Renderer).Surface;

            public RectF32 StatusBar => StatusBarRect;

            public RectF32 InfoPanel => InfoPanelRect;

            public RectF32 FileList => FileListRect;

            public RectF32 ImageArea => ImageAreaRect;

            public RectF32 Toolbar => ToolbarRect;

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels) { }

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
        }
    }
}
