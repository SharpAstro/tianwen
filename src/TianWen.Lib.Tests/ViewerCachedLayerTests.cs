using System;
using System.Collections.Generic;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The cached image layer: image content is rendered into an offscreen target and blitted for as long
    /// as it stays valid, so a redraw that only changes the chrome stops re-running the demosaic + stretch
    /// over the whole pane.
    /// </summary>
    /// <remarks>
    /// <para><b>Every assertion here is a COUNT, and it has to be.</b> A working cache and a re-render
    /// produce the identical frame -- that is the entire point -- so no pixel comparison, screenshot or
    /// layout assertion can tell them apart. The only observable difference is how often the expensive
    /// operation ran, the same reasoning that put a counter behind
    /// <c>SkyMapTab.PrimOverlayGathers</c>.</para>
    /// <para>The seam is faked rather than driven on a GPU because what is under test is the POLICY --
    /// when a slot may be reused and what UV window to sample -- which is renderer-agnostic. The Vulkan
    /// side of it (that a layer survives its render pass and can be sampled later) is pinned upstream by
    /// SdlVulkan.Renderer's own real-device test.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerCachedLayerTests
    {
        private const uint SurfaceW = 1000;
        private const uint SurfaceH = 600;
        private const int ImageW = 400;
        private const int ImageH = 300;

        // Pane is the whole surface (HideChrome), so the margin is a quarter of 1000 x 600.
        private const float MarginX = SurfaceW * 0.25f;

        [Fact]
        public void AFirstFrameBuildsTheLayerAndDrawsFromIt()
        {
            var viewer = NewViewer();
            var state = NewState();

            Frame(viewer, state);

            viewer.LayerPasses.Count.ShouldBe(1, "the layer had to be built");
            viewer.LayerDraws.ShouldBe(1, "the image is rendered INTO the layer");
            viewer.DirectDraws.ShouldBe(0, "and not also straight to the surface");
            viewer.Blits.Count.ShouldBe(1, "the frame is drawn from the layer");
            viewer.CachedLayerStats.Renders.ShouldBe(1);
            viewer.CachedLayerStats.Blits.ShouldBe(1);

            // Capacity is the pane plus a quarter of it on each side, so a pan has somewhere to go.
            viewer.LayerPasses[0].ShouldBe(((int)(SurfaceW * 1.5f), (int)(SurfaceH * 1.5f)));
        }

        /// <summary>
        /// THE test. A frame that returns to an already-built slot with nothing changed must not re-render
        /// the image. If this passes and everything else fails, the feature still works; if this fails,
        /// nothing else matters -- the cache would be a pure cost.
        /// </summary>
        [Fact]
        public void ReturningToAWarmSlotWithNothingChangedDoesNotReRender()
        {
            var viewer = NewViewer();
            var state = NewState();

            Frame(viewer, state);              // slot 0 built
            viewer.SlotIndex = 1;
            Frame(viewer, state);              // slot 1 built -- a second target, cold on its first turn
            viewer.CachedLayerStats.Renders.ShouldBe(2, "each slot is built once");

            viewer.SlotIndex = 0;              // back round to a warm slot
            Frame(viewer, state);

            viewer.CachedLayerStats.Renders.ShouldBe(2, "a warm slot must NOT be re-rendered");
            viewer.CachedLayerStats.Blits.ShouldBe(3, "and it must still be drawn");
            viewer.DirectDraws.ShouldBe(0, "nothing fell back to a direct render");
        }

        [Fact]
        public void PanningInsideTheMarginIsAnOffsetIntoTheSameLayer()
        {
            var viewer = NewViewer();
            var state = NewState();
            Frame(viewer, state);
            var atRest = viewer.Blits[0];

            // Well inside a 250px margin.
            state.PanOffset = (100f, 0f);
            Frame(viewer, state);

            viewer.CachedLayerStats.Renders.ShouldBe(1, "a pan inside the margin is a UV offset, not a re-render");
            viewer.CachedLayerStats.Blits.ShouldBe(2);
            viewer.Blits[1].U0.ShouldBeLessThan(atRest.U0, "the sampled window must move with the pan");
            (viewer.Blits[1].U1 - viewer.Blits[1].U0)
                .ShouldBe(atRest.U1 - atRest.U0, 1e-5f, "and keep its width, or the image would scale");
        }

        [Fact]
        public void PanningBeyondTheMarginReRenders()
        {
            var viewer = NewViewer();
            var state = NewState();
            Frame(viewer, state);

            // Past the margin: the layer simply does not hold those pixels.
            state.PanOffset = (MarginX + 30f, 0f);
            Frame(viewer, state);

            viewer.CachedLayerStats.Renders.ShouldBe(2);
        }

        [Fact]
        public void AChangedShaderInputInvalidatesEverySlotAndNotJustThisOne()
        {
            var viewer = NewViewer();
            var state = NewState();

            Frame(viewer, state);
            viewer.SlotIndex = 1;
            Frame(viewer, state);
            viewer.CachedLayerStats.Renders.ShouldBe(2);

            // A dial moved. The other slot is stale too, and it is the one the NEXT frame will reach for
            // -- so a change that only invalidated the current slot would blit the old dials one frame
            // later, which is the bug that would be blamed on the stretch rather than the cache.
            viewer.UniformsChanged = true;
            viewer.SlotIndex = 0;
            Frame(viewer, state);
            viewer.CachedLayerStats.Renders.ShouldBe(3);

            viewer.SlotIndex = 1;
            Frame(viewer, state);
            viewer.CachedLayerStats.Renders.ShouldBe(4, "the second slot was invalidated by the same change");
        }

        [Fact]
        public void AZoomChangeReRenders()
        {
            var viewer = NewViewer();
            var state = NewState();
            Frame(viewer, state);

            state.Zoom = 2f;
            Frame(viewer, state);

            viewer.CachedLayerStats.Renders.ShouldBe(2, "the layer holds content at one zoom");
        }

        [Fact]
        public void ATextureUploadInvalidatesTheLayer()
        {
            var viewer = NewViewer();
            var state = NewState();
            Frame(viewer, state);
            viewer.CachedLayerStats.Renders.ShouldBe(1);

            // The ordering hazard: the host uploads textures inside its render callback, AFTER the
            // pre-pass that built the layer. Without invalidation here, swapping document would blit a
            // layer drawn from the previous document's pixels.
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);
            viewer.SlotIndex = 0;
            Frame(viewer, state);

            viewer.CachedLayerStats.Renders.ShouldBe(2);
        }

        [Fact]
        public void WithoutTheOptInTheImageIsDrawnDirectlyAsBefore()
        {
            var viewer = NewViewer();
            viewer.UseCachedImageLayer = false;
            var state = NewState();

            Frame(viewer, state);
            Frame(viewer, state);

            viewer.CachedLayerStats.Renders.ShouldBe(0);
            viewer.CachedLayerStats.Blits.ShouldBe(0);
            viewer.LayerPasses.ShouldBeEmpty();
            viewer.DirectDraws.ShouldBe(2, "every frame renders the image, exactly as it always did");
        }

        [Fact]
        public void ABackendThatCannotAnswerNeverGetsACacheHit()
        {
            // The seam's defaults all mean "unsupported", so a viewer that overrides nothing keeps the
            // old behaviour. That is what makes adding this safe for the GUI's embedded viewers.
            var viewer = new PlainViewer(new RgbaImageRenderer(SurfaceW, SurfaceH)) { UseCachedImageLayer = true };
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);
            var state = NewState();

            Frame(viewer, state);

            viewer.CachedLayerStats.Renders.ShouldBe(0);
            viewer.DirectDraws.ShouldBe(1);
        }

        /// <summary>
        /// The FITS viewer's "the image gets compressed when I widen the file list". A backend allocates
        /// once and answers a smaller request out of the same texture, so once the pane has shrunk the
        /// layer occupies the top-left of something larger. UVs are texture coordinates: normalised by the
        /// layer's size instead of the texture's they sample capacity/layer more texels than the pane
        /// holds, and the image draws squashed by that factor (0.72x on a 2957 px texture with a 2132 px
        /// layer, measured) and shifted by the margin's share of the difference.
        /// </summary>
        [Fact]
        public void TheBlitSamplesInTextureSpaceWhenTheTargetIsLargerThanTheLayer()
        {
            var viewer = NewViewer();
            // A texture allocated when the pane was twice as wide: the request is 1500 x 900, the
            // texture 3000 x 900.
            viewer.Capacity = ((int)(SurfaceW * 3f), (int)(SurfaceH * 1.5f));
            var state = NewState();

            Frame(viewer, state);

            viewer.Blits.Count.ShouldBe(1);
            var blit = viewer.Blits[0];
            // The sampled window covers exactly the pane, in TEXTURE pixels...
            ((blit.U1 - blit.U0) * viewer.Capacity.W).ShouldBe(SurfaceW, 1e-2f,
                "the blit must sample one texel per pane pixel; dividing by the layer size samples twice as many and squashes the image 2x");
            ((blit.V1 - blit.V0) * viewer.Capacity.H).ShouldBe(SurfaceH, 1e-2f);
            // ...and starts one margin in, where the layer put the pane.
            (blit.U0 * viewer.Capacity.W).ShouldBe(MarginX, 1e-2f, "the pane sits one margin inside the layer, in texture pixels");
            (blit.V0 * viewer.Capacity.H).ShouldBe(SurfaceH * 0.25f, 1e-2f);
        }

        [Fact]
        public void ABackendWhoseCapacityIsBelowTheRequestIsNotSampled()
        {
            var viewer = NewViewer();
            viewer.Capacity = ((int)SurfaceW, (int)SurfaceH);
            var state = NewState();

            Frame(viewer, state);

            viewer.CachedLayerStats.Renders.ShouldBe(0, "nothing may be rendered into a texture the layer does not fit");
            viewer.Blits.Count.ShouldBe(0);
            viewer.DirectDraws.ShouldBe(1, "the safe answer is the direct render");
        }

        /// <summary>
        /// The crash. A document swap recreates the channel textures, which destroys the previous views
        /// after draining PRIOR frames; it cannot un-record what THIS frame's command buffer already holds.
        /// When the hosts uploaded from their render callbacks, the cached-layer pre-pass had already bound
        /// the old views, the upload destroyed them, and the frame was submitted with a dangling view:
        /// under the validation layer "vkCmdBindDescriptorSets(): ... invalid state ... VkImageView was
        /// destroyed", then VK_ERROR_DEVICE_LOST; on the Store build, a GPU watchdog (2026-08-27). The
        /// upload therefore belongs to PrepareFrame, ahead of anything that samples.
        /// </summary>
        [Fact]
        public void ANewDocumentIsUploadedBeforeTheLayerPassThatSamplesIt()
        {
            var viewer = new CachingViewer(new RgbaImageRenderer(SurfaceW, SurfaceH));
            var source = new LiveFramePreviewSource();
            source.AcceptFrame(MonoImage(ImageW, ImageH), freezeStats: false);
            var state = NewState();
            state.NeedsTextureUpdate = true;

            // The standalone host's frame: pre-pass (PrepareFrame + layer), then Render.
            viewer.PrepareFrame(source, state);
            viewer.PrepareCachedImageLayer();
            viewer.Render(source, state);

            state.NeedsTextureUpdate.ShouldBeFalse("PrepareFrame consumed the upload");
            viewer.Events.ShouldNotBeEmpty();
            var firstPass = viewer.Events.IndexOf("layerPass");
            var lastUpload = viewer.Events.FindLastIndex(e => e.StartsWith("upload:", StringComparison.Ordinal));
            lastUpload.ShouldBeGreaterThanOrEqualTo(0, "the new document's textures were uploaded");
            firstPass.ShouldBeGreaterThan(lastUpload,
                $"every upload must precede the layer pass that samples the textures; got [{string.Join(", ", viewer.Events)}]");
            // And the frame then USES the layer it built from the new textures, instead of invalidating it
            // and drawing directly as the old order did.
            viewer.LayerPasses.Count.ShouldBe(1);
            viewer.Blits.Count.ShouldBe(1, "the layer built this frame is the one drawn this frame");
            viewer.DirectDraws.ShouldBe(0);
        }

        private static Image MonoImage(int w, int h)
        {
            var ch = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    ch[y, x] = 500f + (x + y) % 7;
                }
            }
            var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
                FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
                float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            return new Image([ch], BitDepth.Float32, maxValue: 1000f, minValue: 0f, pedestal: 0f, imageMeta: meta);
        }

        private static void Frame(TestViewerBase viewer, ViewerState state)
        {
            viewer.PrepareFrame(null, state);
            viewer.PrepareCachedImageLayer();
            viewer.Render(null, state);
        }

        private static CachingViewer NewViewer()
        {
            var viewer = new CachingViewer(new RgbaImageRenderer(SurfaceW, SurfaceH));
            // Stamps ImageWidth/ImageHeight, which is what gates the image draw at all.
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);
            return viewer;
        }

        private static ViewerState NewState() => new ViewerState
        {
            HideChrome = true,
            ShowFileList = false,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
            Zoom = 1f,
            ZoomToFit = false,
        };

        /// <summary>Shared no-op GPU seam, so each viewer below only states what it is testing.</summary>
        private abstract class TestViewerBase : ImageRendererBase<RgbaImage>
        {
            protected TestViewerBase(RgbaImageRenderer renderer) : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
            }

            public int DirectDraws { get; protected set; }

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

        /// <summary>Overrides nothing of the cached-layer seam: the unsupported-backend case.</summary>
        private sealed class PlainViewer(RgbaImageRenderer renderer) : TestViewerBase(renderer)
        {
            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels)
                => DirectDraws++;
        }

        /// <summary>Fakes the seam and records what the policy asked it to do.</summary>
        private sealed class CachingViewer : TestViewerBase
        {
            private readonly HashSet<int> _built = [];
            private bool _inLayer;

            public CachingViewer(RgbaImageRenderer renderer) : base(renderer)
                => UseCachedImageLayer = true;

            public int SlotIndex { get; set; }

            /// <summary>What the next <see cref="ImageShaderInputChanged"/> answers. Cleared once read,
            /// mirroring the real signal, which reports a change only for the write that made it.</summary>
            public bool UniformsChanged { get; set; } = true;

            public List<(int W, int H)> LayerPasses { get; } = [];
            public List<(float U0, float V0, float U1, float V1)> Blits { get; } = [];
            public int LayerDraws { get; private set; }

            /// <summary>Texture uploads and layer passes in the order the frame issued them. A layer pass
            /// samples the channel textures, so an upload that recreates them must come first.</summary>
            public List<string> Events { get; } = [];

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel, int imageWidth, int imageHeight)
                => Events.Add($"upload:{channel}");

            /// <summary>The texture size the fake "allocated". Zero means exactly what was asked for,
            /// the case every earlier test ran in; a real backend allocates once and keeps answering
            /// smaller requests out of the same texture, which is the case the capacity test sets up.</summary>
            public (int W, int H) Capacity { get; set; }

            protected override int CachedLayerSlotCount => 2;
            protected override int CachedLayerSlotIndex => SlotIndex;
            protected override bool TryEnsureCachedLayerTargets(int width, int height, out int capacityWidth, out int capacityHeight)
            {
                (capacityWidth, capacityHeight) = Capacity == default ? (width, height) : Capacity;
                return capacityWidth >= width && capacityHeight >= height;
            }

            protected override bool TryBeginCachedLayerPass(int width, int height)
            {
                LayerPasses.Add((width, height));
                Events.Add("layerPass");
                _inLayer = true;
                return true;
            }

            protected override void EndCachedLayerPass()
            {
                _inLayer = false;
                _built.Add(SlotIndex);
            }

            protected override bool TryDrawCachedLayer(int slot, float x, float y, float w, float h,
                float u0, float v0, float u1, float v1)
            {
                if (!_built.Contains(slot))
                {
                    return false;
                }

                Blits.Add((u0, v0, u1, v1));
                return true;
            }

            protected override bool TryWriteImageUniforms(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? gridWcs, RenditionSlot slot) => true;

            protected override bool ImageShaderInputChanged(RenditionSlot slot)
            {
                var changed = UniformsChanged;
                UniformsChanged = false;
                return changed;
            }

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels)
            {
                if (_inLayer)
                {
                    LayerDraws++;
                }
                else
                {
                    DirectDraws++;
                }
            }
        }
    }
}
