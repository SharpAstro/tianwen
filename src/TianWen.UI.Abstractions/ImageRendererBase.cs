// TODO high priority: cached offscreen framebuffer for partial UI redraws
//
// Problem: every mouse move that changes the status bar pixel readout triggers a full
// Vulkan render pass (image quad + stretch shader + histogram + stars + toolbar + status bar).
// Even with the pixel-change gate and 30fps throttle, GPU usage spikes to 15-25% on mouse hover.
//
// Solution: two-layer rendering with a cached offscreen framebuffer.
//
// Layer 1: Image content (expensive, rarely changes):
//   Render image quad + stretch shader + star overlay + WCS grid + histogram
//   into a VkImage offscreen framebuffer. Only re-render when:
//   - New image loaded (NeedsTextureUpdate)
//   - Stretch parameters changed (mode, shadows, midtones, highlights, boost, HDR)
//   - Zoom changed, or a pan left the cached margin (constraint 3 below)
//   - Star overlay toggled
//   - WCS grid toggled
//   - Channel view changed
//
// Layer 2: UI chrome (cheap, changes on mouse move):
//   Each frame: blit cached Layer 1 framebuffer → render toolbar, status bar,
//   file list, info panel on top. This is just text quads; very cheap.
//
// THREE CONSTRAINTS, each already paid for elsewhere. Read them before writing the code.
//
// 1. The cache render MUST ride the frame: no extra submit, no extra fence, no wait, and never a
//    barrier against an image this process does not currently own. That last one is exactly the
//    inspector screenshot mistake (SdlVulkan.Renderer 57eceb8) -- ReadbackSwapchainRgba ran after
//    vkQueuePresentKHR and transitioned the presented image without re-acquiring it, which the
//    Khronos layer reports as a WRITE_AFTER_PRESENT hazard and which entitles the driver to park the
//    whole queue, making it the leading candidate for the Adreno stuck-fence wedge. Note this is NOT
//    a new upstream feature: VulkanContext.ThumbnailCapture.cs is already a secondary render target
//    on the LIVE device, opened on the frame's own command buffer from the OnPreRenderPass hook,
//    with the projection redirected and the pre-baked pipelines binding unchanged (its render pass
//    keeps the swapchain's attachment formats, sample count and subpass refs). The cached layer is
//    that class with three changes: finalLayout ShaderReadOnlyOptimal rather than TransferSrcOptimal,
//    no readback buffer and no copy, and it persists across frames instead of being consumed once.
//
// 2. ONE TARGET PER FRAME-IN-FLIGHT, never one shared target. Re-rendering a shared cache writes an
//    image the previous submitted frame is still sampling -- the hazard VkFontAtlas.Grow guards with
//    a drain, and which "the Adreno X1-85 punishes by failing the next vkQueueSubmit". Draining per
//    dirty frame is not the answer here: during a zoom drag EVERY frame is dirty, so the stall would
//    land on the hot path. With MaxFramesInFlight targets a content change marks all of them dirty
//    and each re-renders on its next turn, so a change costs 2 image renders and then nothing, and a
//    continuous zoom is never worse than today.
//
// 3. The cache is IMAGE-space with a margin, not pane-space, or panning gains nothing -- a pane-sized
//    cache invalidates on every pan, which is a case this exists to fix. Allocate pane + margin once
//    at capacity and never resize on the render thread (ThumbnailCapture holds the same discipline:
//    "fixed allocated capacity, never resized on the render thread"); re-allocate only on window
//    resize, which already drains. A pan inside the margin is then a blit at an offset and a pan
//    beyond it re-renders. At 1.5x linear margin on a 1920x1080 pane that is 18.7 MB per slot, 37 MB
//    for two.
//
// Expected impact: mouse-hover GPU usage drops from ~20% to <2% (just text rendering).
// The full image render only runs on actual content changes (~1-5 fps during interaction).
// Ablation on a solved frame, 1:1 and maximized: 2.94 ms baseline, 4.22 ms with the A/B split,
// 4.97 ms with Calibrate + NeutBg -- so the shader, not the chrome, is what a redraw costs.
//
// Files to change:
//   - SdlVulkan.Renderer: a sampleable variant of ThumbnailCapture (finalLayout, no readback). The
//     bounded-drain half is DONE (7.25.2661): TryWaitAllFramesIdle is public, and it is the form a
//     consumer owning GPU images should call to honour the rule in constraint 2 -- NOT
//     TryWaitPriorFramesIdle, which skips the current frame's fence by design and is therefore wrong
//     for a destroy that runs between frames (SharpAstro/tianwen#197)
//   - TianWen.UI.Shared/VkFitsImagePipeline.cs: offscreen framebuffer management
//   - TianWen.UI.Shared/VkImageRenderer.cs: split RenderImageQuad into cached/blit paths
//   - TianWen.UI.Abstractions/ImageRendererBase.cs: add ImageContentDirty flag logic
//   - TianWen.UI.Abstractions/ViewerState.cs: add ImageContentDirty state (a per-slot dirty mask,
//     not one bool, per constraint 2)

using System;
using System.IO;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic base class for the FITS image viewer widget.
    /// Contains all layout, toolbar, file list, info panel, status bar, grid labels,
    /// star overlay, object overlay, histogram chrome, keyboard and mouse wheel handling.
    /// Subclasses implement 6 abstract methods for the GPU-specific rendering.
    /// </summary>
    public abstract partial class ImageRendererBase<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer), ISelfDispatchingInputWidget
    {
        /// <summary>Reference to the viewer state from the last Render call.</summary>
        private ViewerState? _state;

        /// <summary>Reference to the document from the last Render call.</summary>
        private AstroImageDocument? _document;

        // The source being previewed. For a still image this is the same object as _document
        // (AstroImageDocument implements IPreviewSource); for a SER it is a SerPreviewSource and
        // _document is null (still-only features inactive). The display path reads _source; the
        // still-only features (plate solve / stars / colour cal / info panel) read _document.
        private IPreviewSource? _source;

        /// <summary>Width of the viewport in pixels.</summary>
        protected uint Width { get; set; }

        /// <summary>Height of the viewport in pixels.</summary>
        protected uint Height { get; set; }

        /// <summary>Width of the loaded image in pixels.</summary>
        protected int ImageWidth { get; set; }

        /// <summary>Height of the loaded image in pixels.</summary>
        protected int ImageHeight { get; set; }

        /// <summary>Number of channel textures currently uploaded (1 = mono/single channel, 3 = RGB).</summary>
        public int ChannelTextureCount { get; set; }

        /// <summary>Image source mode for the GPU shader (processed channels, raw mono, or raw Bayer).</summary>
        public int ImageSourceMode { get; set; }

        /// <summary>Bayer pattern X offset (0 or 1).</summary>
        public int BayerOffsetX { get; set; }

        /// <summary>Bayer pattern Y offset (0 or 1).</summary>
        public int BayerOffsetY { get; set; }

        /// <summary>In-shader Bayer demosaic for the RawBayer path: 0 = bilinear, 1 = MHC.
        /// Derived from <see cref="ViewerState.DebayerAlgorithm"/> via <see cref="GpuDebayerMode"/>
        /// and refreshed in <see cref="UploadDocumentTextures"/>.</summary>
        public int RawBayerDebayerMode { get; set; } = 1;

        /// <summary>Maps a <see cref="DebayerAlgorithm"/> to the GPU live-demosaic mode written into
        /// <c>stretchBlend.z</c> for the RawBayer shader path: <c>0</c> = bilinear colour, <c>1</c> = MHC colour,
        /// <c>2</c> = raw mosaic (no demosaic, grey CFA pattern), <c>3</c> = monochrome. Each menu entry behaves
        /// as its name implies: <see cref="DebayerAlgorithm.None"/> shows the raw pattern,
        /// <see cref="DebayerAlgorithm.BilinearMono"/> is greyscale, <see cref="DebayerAlgorithm.VNG"/> is the
        /// simple colour demosaic, and <see cref="DebayerAlgorithm.AHD"/>/<see cref="DebayerAlgorithm.MHC"/> both
        /// use MHC (the GPU's best gradient-corrected colour demosaic; AHD has no GPU implementation). The default
        /// <see cref="DebayerAlgorithm.AHD"/> therefore gives MHC colour, matching the standalone SER viewer.</summary>
        public static int GpuDebayerMode(DebayerAlgorithm algorithm) => algorithm switch
        {
            DebayerAlgorithm.None => 2,         // raw mosaic, no demosaic
            DebayerAlgorithm.BilinearMono => 3, // monochrome
            DebayerAlgorithm.VNG => 0,          // bilinear colour (no GPU VNG)
            _ => 1,                             // MHC colour (AHD falls back to MHC)
        };

        // DPI scale is the inherited PixelWidgetBase.DpiScale -- set by the host (SDL DisplayScale) at
        // startup + resize; layout helpers and the ~16 derived px properties below all read it.

        /// <summary>
        /// Lazy-initialized celestial object database used for object overlays.
        /// </summary>
        public DotNext.Threading.AsyncLazy<ICelestialObjectDB>? CelestialObjectDB { get; set; }

        /// <summary>
        /// Caller-driven sky-position annotations rendered through the active WCS.
        /// Defaults to <see cref="WcsAnnotation.Empty"/>; consumers (polar-alignment
        /// mode, mosaic composer, plate-solve verification, etc.) push annotations
        /// in to overlay markers + rings on the live frame. Reset to
        /// <see cref="WcsAnnotation.Empty"/> when the consumer is done.
        /// </summary>
        public WcsAnnotation Annotation { get; set; } = WcsAnnotation.Empty;

        /// <summary>
        /// A WCS to project <see cref="Annotation"/> through when the current source is NOT an
        /// <see cref="AstroImageDocument"/> (so there is no <c>document.Wcs</c>). A document-less live preview
        /// (polar-align solving its preview frame) sets this to the solved WCS; the still-image path ignores it
        /// (the document's own WCS wins). Null = no override.
        /// </summary>
        public WCS? OverrideWcs { get; set; }

        // -----------------------------------------------------------------------
        // Base layout constants (at 1x scale)
        // -----------------------------------------------------------------------

        private const float BaseInfoPanelWidth = 300f;
        private const float BaseStatusBarHeight = 24f;
        private const float BaseToolbarHeight = 40f;
        private const float BaseFileListWidth = 300f;
        protected const float BaseFontSize = 18f;
        private const float BaseToolbarFontSize = 18f;
        private const float BasePanelPadding = 6f;
        private const float BaseButtonPaddingH = 12f;
        private const float BaseButtonSpacing = 4f;
        private const float BaseButtonGroupSpacing = 14f;

        // SER transport bar: a thin strip at the bottom of the image pane (shown only for a sequence).
        private const float BaseTransportHeight = 34f;

        // Histogram constants
        private const float BaseHistogramWidth = 256f;
        private const float BaseHistogramHeight = 128f;
        private const float BaseHistogramMargin = 8f;

        // Scaled accessors
        private float InfoPanelWidth => BaseInfoPanelWidth * DpiScale;
        private float StatusBarHeight => BaseStatusBarHeight * DpiScale;
        /// <summary>Height of ONE toolbar row. The band is this times the rows the run needed.</summary>
        private float ToolbarRowHeight => BaseToolbarHeight * DpiScale;
        // The band as it actually stands this frame, so every consumer of it (the histogram's
        // pre-arrangement fallback, GetImageAreaSize, the public ScaledToolbarHeight the GPU grid reads)
        // follows a wrap instead of assuming one row.
        private float ToolbarHeight => ToolbarRowHeight * _toolbarRows;
        private float TransportHeight => BaseTransportHeight * DpiScale;
        // Honor the user-resizable width when state is bound; fall back to the
        // historical 300px constant when state hasn't been attached yet (e.g.
        // during initial layout queries before Render(state) has run).
        private float FileListWidth =>
            (_state is { } s ? s.FileListWidthBase : BaseFileListWidth) * DpiScale;

        private float FontSize => BaseFontSize * DpiScale;
        private float ToolbarFontSize => BaseToolbarFontSize * DpiScale;
        private float PanelPadding => BasePanelPadding * DpiScale;
        private float ButtonPaddingH => BaseButtonPaddingH * DpiScale;
        private float ButtonSpacing => BaseButtonSpacing * DpiScale;
        private float ButtonGroupSpacing => BaseButtonGroupSpacing * DpiScale;

        /// <summary>Scaled toolbar height in pixels.</summary>
        public float ScaledToolbarHeight => ToolbarHeight;

        /// <summary>Scaled status bar height in pixels.</summary>
        public float ScaledStatusBarHeight => StatusBarHeight;

        /// <summary>Scaled file list width in pixels.</summary>
        public float ScaledFileListWidth => FileListWidth;

        /// <summary>Scaled info panel width in pixels.</summary>
        public float ScaledInfoPanelWidth => InfoPanelWidth;

        // -----------------------------------------------------------------------
        // Local chrome colours
        //
        // The shared/role colours (toolbar strip, panel backgrounds, header text,
        // separators, file-selection highlight) live on ViewerTheme. These are the
        // per-widget *state* colours -- button hover/active lerps, the file-list
        // hover band, scrollbar, resize handle, grid-label green, histogram LOG
        // toggle states -- which ViewerTheme deliberately keeps at the draw site.
        // Values match the float literals they replaced (via RGBAColor32.FromFloat)
        // so adopting them is a pure dedup with no visual change.
        // -----------------------------------------------------------------------

        private static readonly RGBAColor32 ToolbarButtonBg = RGBAColor32.FromFloat(0.25f, 0.25f, 0.28f, 1f);
        private static readonly RGBAColor32 ToolbarButtonHoverBg = RGBAColor32.FromFloat(0.35f, 0.35f, 0.40f, 1f);
        private static readonly RGBAColor32 ToolbarButtonActiveBg = RGBAColor32.FromFloat(0.20f, 0.30f, 0.50f, 1f);
        private static readonly RGBAColor32 ToolbarButtonDisabledBg = RGBAColor32.FromFloat(0.20f, 0.20f, 0.22f, 1f);

        private static readonly RGBAColor32 FileListHoverBg = RGBAColor32.FromFloat(0.22f, 0.22f, 0.28f, 1f);
        private static readonly RGBAColor32 FileListItemText = RGBAColor32.FromFloat(0.80f, 0.80f, 0.80f, 1f);
        private static readonly RGBAColor32 FileListItemTextSelected = RGBAColor32.FromFloat(1f, 1f, 1f, 1f);
        private static readonly RGBAColor32 ScrollBarColor = RGBAColor32.FromFloat(0.40f, 0.40f, 0.45f, 0.8f);
        private static readonly RGBAColor32 ResizeHandleActiveColor = RGBAColor32.FromFloat(0.45f, 0.55f, 0.70f, 1f);
        private static readonly RGBAColor32 ResizeHandleIdleColor = RGBAColor32.FromFloat(0.30f, 0.30f, 0.35f, 0.7f);

        private static readonly RGBAColor32 GridLabelColor = RGBAColor32.FromFloat(0f, 0.85f, 0f, 1f);

        // SER transport bar: strip background, scrub track (unfilled), played-portion fill, and handle.
        private static readonly RGBAColor32 TransportBg = RGBAColor32.FromFloat(0.16f, 0.16f, 0.18f, 0.95f);
        private static readonly RGBAColor32 TransportTrackBg = RGBAColor32.FromFloat(0.30f, 0.30f, 0.34f, 1f);
        private static readonly RGBAColor32 TransportTrackFill = RGBAColor32.FromFloat(0.30f, 0.50f, 0.80f, 1f);
        private static readonly RGBAColor32 TransportHandle = RGBAColor32.FromFloat(0.85f, 0.85f, 0.90f, 1f);
        // Shared chrome for every track slider (WB / wavelet / scrub) -- identical at all call sites; the
        // DrawTrackSlider control lives in DIR.Lib PixelWidgetBase and takes these two colours as a param.
        private static readonly TrackSliderChrome TrackChrome = new TrackSliderChrome(TransportTrackBg, TransportHandle);

        // Histogram LOG-scale toggle button: log-on (blue) and log-off (grey) families,
        // each with a hover-brightened variant. Alpha 0.9 (the histogram is an overlay).
        private static readonly RGBAColor32 HistogramLogOnBg = RGBAColor32.FromFloat(0.20f, 0.30f, 0.50f, 0.9f);
        private static readonly RGBAColor32 HistogramLogOnHoverBg = RGBAColor32.FromFloat(0.25f, 0.35f, 0.55f, 0.9f);
        private static readonly RGBAColor32 HistogramLogOffBg = RGBAColor32.FromFloat(0.25f, 0.25f, 0.28f, 0.9f);
        private static readonly RGBAColor32 HistogramLogOffHoverBg = RGBAColor32.FromFloat(0.35f, 0.35f, 0.40f, 0.9f);

        // -----------------------------------------------------------------------
        // Abstract methods: GPU-specific rendering
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders the image quad with stretch uniforms, optional WCS grid, and viewport placement.
        /// </summary>
        /// <param name="rendition">How to display the frame. The split passes a DIFFERENT rendition
        /// per half, which is the whole point of it being a parameter rather than read from state.</param>
        /// <param name="slot">Which display-parameter slot the backend should read. The split's two
        /// draws share one command buffer, so they must not share a slot.</param>
        /// <param name="sampleBeforeChannels">Sample the retained pre-enhance pixels rather than the
        /// live ones. A backend with nothing retained must fall back to the live pixels.</param>
        protected abstract void RenderImageQuad(IPreviewSource? source, ViewerState state,
            in DisplayRendition rendition, WCS? wcs,
            float left, float top, float right, float bottom, uint projW, uint projH,
            RenditionSlot slot, bool sampleBeforeChannels);

        /// <summary>
        /// Renders the histogram quad with the given stretch uniforms.
        /// </summary>
        protected abstract void RenderHistogramQuad(StretchUniforms stretch,
            HistogramDisplay histogram, ViewerState state,
            float left, float top, float right, float bottom, uint projW, uint projH);

        /// <summary>
        /// Draws an ellipse overlay (outline or filled) at the given screen position.
        /// </summary>
        protected abstract void DrawEllipseOverlay(float cx, float cy,
            float semiMajor, float semiMinor, float angleRad, RGBAColor32 color, float thickness);

        /// <summary>
        /// Draws a cross marker at the given screen position.
        /// </summary>
        protected abstract void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color);

        /// <summary>
        /// Draws a straight line between two screen positions. Used by the
        /// polar-alignment overlay's correction-direction arrow shaft (the
        /// arrowhead is composed from two additional line segments).
        /// </summary>
        protected abstract void DrawLineOverlay(float x0, float y0, float x1, float y1, RGBAColor32 color, float thickness);

        /// <summary>
        /// Called when the viewport is resized.
        /// </summary>
        protected abstract void OnResize(uint width, uint height);

        /// <summary>
        /// Uploads image texture data for the given channel.
        /// </summary>
        public abstract void UploadImageTexture(ReadOnlySpan<float> data, int channel,
            int imageWidth, int imageHeight);

        /// <summary>
        /// Uploads 8-bit image texture data for the given channel, as UNORM texels that sample to the
        /// same [0,1] the float path uploads. False means this backend has no 8-bit path, and the
        /// caller uploads the widened floats instead.
        /// </summary>
        /// <remarks>
        /// <para><b>The override IS the capability declaration.</b> That is why no companion
        /// "supports 8-bit textures" flag exists to consult: a flag beside this method would state one
        /// fact twice and let a backend advertise a texel format it never implemented, a disagreement
        /// only a runtime throw could then catch. Here the sole way to claim 8-bit uploads is to
        /// perform one, so there is nothing to disagree with, and a further format later costs ONE
        /// member rather than two.</para>
        /// <para>A backend that declines must upload NOTHING, because the caller goes on to fill the
        /// same channel slot from the floats. Declining is always safe: the float plane draws the same
        /// picture, and for every source except a retained 8-bit raster it is the only one there is.</para>
        /// </remarks>
        protected virtual bool TryUploadImageTexture(ReadOnlySpan<byte> data, int channel,
            int imageWidth, int imageHeight) => false;

        /// <summary>
        /// Lets a backend free per-channel device storage that the view just uploaded no longer
        /// samples. <paramref name="liveSlotCount"/> is the number of texture SLOTS the pass populated.
        /// </summary>
        /// <remarks>
        /// <para>A no-op default is right here, unlike the 8-bit upload above: a backend with no
        /// per-channel device storage genuinely has nothing to release, so silence is the correct answer
        /// rather than a missing implementation.</para>
        /// <para><b>Populated slots is NOT <see cref="ChannelTextureCount"/>.</b> That uniform is the
        /// shader's output arity -- raw Bayer sets it to 3 while uploading a single mosaic texture that
        /// the shader demosaics. Passing it would keep two stale full-size textures alive on exactly the
        /// path that needs one; passing too few would free one the shader samples.</para>
        /// </remarks>
        protected virtual void ReleaseUnusedChannelTextures(int liveSlotCount) { }

        /// <summary>
        /// Uploads histogram data from a preview source. Called once per image load (or sequence open).
        /// </summary>
        public abstract void UploadHistogramData(IPreviewSource source);

        /// <summary>
        /// Returns the histogram display, or null if not yet initialized.
        /// </summary>
        protected abstract HistogramDisplay? GetHistogramDisplay();

        /// <summary>
        /// Whether to skip caching the before pixels because memory is short. The cache is worth ~100 MB
        /// of device memory on a large frame, and on a shared-memory (UMA) box that is the same pool the
        /// image decode competes for -- so the point is to avoid BEING the reason the machine runs out.
        /// </summary>
        /// <param name="bytes">Estimated size of the retention, so the question can be "would THIS push
        /// us over" rather than "are we near the line already".</param>
        /// <remarks>
        /// <para>Overridable because the default reads machine-global state that moves underneath it: two
        /// runs of the same action minutes apart legitimately answer differently, which is untestable and,
        /// worse, unexplainable to a user. A test pins it.</para>
        /// <para>Two things the obvious spelling gets wrong, both found by testing rather than by reading:
        /// <see cref="GC.GetGCMemoryInfo()"/> returns ZEROES until the first collection, so a
        /// freshly-started process reads 0 bytes of load against a real threshold and concludes there is
        /// room whatever the truth; and comparing the CURRENT load against the threshold refuses a 100 MB
        /// cache on a box with 1.4 GiB of headroom, because it answers a question nobody asked.</para>
        /// <para>It stays a pre-check, never a prediction. The authoritative answer is the allocation
        /// attempt, which the backend catches and falls back from.</para>
        /// </remarks>
        protected virtual bool ShouldSkipBeforePixelCache(long bytes)
        {
            var info = GC.GetGCMemoryInfo();
            if (info.MemoryLoadBytes <= 0 || info.HighMemoryLoadThresholdBytes <= 0)
            {
                // No reading yet -- which is NOT the same as "plenty of room". Defer to the allocation.
                return false;
            }

            return info.MemoryLoadBytes + bytes >= info.HighMemoryLoadThresholdBytes;
        }

        // What retaining the current channel textures would cost, from the geometry already uploaded.
        private long EstimatedBeforePixelBytes =>
            (long)ImageWidth * ImageHeight * Math.Max(1, ChannelTextureCount) * sizeof(float);

        /// <summary>
        /// Whether pre-enhance pixels are currently retained for the split's left half.
        /// </summary>
        /// <remarks>
        /// Virtual with a no-op default rather than abstract: a backend without a before slot (an
        /// offline raster renderer hosting this chrome for a layout test) is still a legitimate host,
        /// and the split degrades to unavailable on its own. Nothing here is a shim -- a backend that
        /// answers false is answering correctly.
        /// </remarks>
        public virtual bool HasBeforeImageTextures => false;

        /// <summary>Device memory held by the retained before pixels, in bytes; 0 when none.</summary>
        public virtual long BeforeImageTextureBytes => 0;

        /// <summary>
        /// Retains the current image textures as the split's before pixels, so the next upload leaves
        /// them intact. Returns false when the backend cannot or has nothing worth retaining.
        /// </summary>
        public virtual bool TryRetainImageTexturesAsBefore() => false;

        /// <summary>Frees any retained before pixels. Safe when none are held.</summary>
        public virtual void ReleaseBeforeImageTextures() { }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Resizes the viewport. Delegates to <see cref="OnResize"/>.
        /// </summary>
        public void Resize(uint width, uint height)
        {
            Width = width;
            Height = height;
            OnResize(width, height);
        }

        /// <summary>
        /// Sets the surface dimensions used for GPU projection WITHOUT triggering <see cref="OnResize"/>. For
        /// an embedded host that shares the renderer's already-sized surface (the GUI live preview / guide cam),
        /// the projection must match the window but the GPU resources belong to the host, so they must not be
        /// re-created. Call each frame with the renderer's current size (no-op when unchanged).
        /// </summary>
        public void SetSurfaceSize(uint width, uint height)
        {
            if (width != Width || height != Height)
            {
                Width = width;
                Height = height;
            }
        }

        /// <summary>
        /// Returns the image area dimensions (excluding toolbar, sidebar, info panel, status bar).
        /// Derived from the single <see cref="ComputeLayout"/> pass so every consumer agrees with the
        /// arranged image-pane rect rather than recomputing the fileListW/panelW formula independently.
        /// </summary>
        public (float Width, float Height) GetImageAreaSize(ViewerState state)
        {
            if (_layout.ImageArea is { Width: > 0 } area)
            {
                return (area.Width, area.Height);
            }

            // Pre-first-frame fallback (no arrangement computed yet -- e.g. an early layout query).
            var fileListW = state.ShowFileList ? FileListWidth : 0;
            var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
            var region = ContentRegion;
            return (region.Width - fileListW - panelW, region.Height - ToolbarHeight - StatusBarHeight);
        }

        /// <summary>
        /// Uploads per-channel R32f textures. Convenience alias for
        /// <see cref="UploadImageTexture(ReadOnlySpan{float}, int, int, int)"/>.
        /// </summary>
        public void UploadChannelTexture(ReadOnlySpan<float> data, int channel, int imageWidth, int imageHeight)
        {
            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
            UploadImageTexture(data, channel, imageWidth, imageHeight);

            // Textures are the image shader's OTHER input, and the UBO byte comparison says
            // nothing about them. This is also the cached layer's one ordering hazard: a host
            // uploads textures from its render callback, which runs AFTER the pre-pass that built
            // the layer, so without this a frame that swapped document would blit a layer drawn
            // from the previous document's pixels. It belongs on the upload itself rather than on
            // UploadDocumentTextures because this is where the content actually changes -- a
            // caller reaching past that wrapper (the channel-view re-upload does) must invalidate
            // too, and here it cannot forget.
            InvalidateCachedImageLayer();
        }

        /// <summary>
        /// Uploads a channel as 8-bit UNORM texels, or returns false when this backend has no 8-bit
        /// path. Alias for <see cref="TryUploadImageTexture"/>.
        /// </summary>
        /// <remarks>
        /// The geometry is stamped only on success, so a decline leaves this widget exactly as it was
        /// and the float upload that follows is the first thing to touch it.
        /// </remarks>
        public bool TryUploadChannelTexture(ReadOnlySpan<byte> data, int channel, int imageWidth, int imageHeight)
        {
            if (!TryUploadImageTexture(data, channel, imageWidth, imageHeight))
            {
                return false;
            }

            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
            InvalidateCachedImageLayer();   // same reason as the float overload above
            return true;
        }

        /// <summary>
        /// Uploads <paramref name="sourceChannel"/> from the document's RETAINED 8-bit samples if they
        /// are available, returning false to mean "caller should upload the floats instead".
        /// </summary>
        /// <remarks>
        /// <para>Where the saving comes from (D3' of <c>docs/plans/viewer-memory-footprint.md</c>): a
        /// float channel costs 4 B/px of device memory, an R8Unorm one costs 1. For a document whose
        /// source container WAS 8-bit this is lossless rather than a quality trade, because the float
        /// plane was widened FROM these very bytes (pinned by
        /// <c>SourceRasterRetentionTests.An8BitTiffKeepsSamplesThatExactlyReproduceTheFloatPlane</c>),
        /// so it also skips a re-quantise.</para>
        /// <para><b>Reaching the raster through the document is deliberate, and does not widen
        /// <see cref="IPreviewSource"/>.</b> That interface is the per-frame playback surface shared
        /// with SER video, and its own remarks establish the pattern: document-only features are
        /// reached by testing <c>source is AstroImageDocument</c> and are simply inactive for a video.
        /// A live camera frame has no retained raster and takes the float path, as before.</para>
        /// <para>Safe because <c>GetChannelData</c> and the raster are the SAME image: the interface
        /// forwards to <c>UnstretchedImage</c>, and any transform that recomputes pixels builds a new
        /// <c>Image</c>, which drops the raster rather than carrying a stale one.</para>
        /// </remarks>
        private bool TryUploadRetainedRaster(IPreviewSource source, int sourceChannel, int textureSlot,
            int imageWidth, int imageHeight)
        {
            if (source is not AstroImageDocument document
                || !document.UnstretchedImage.TryGetSourceRaster(sourceChannel, out var raster))
            {
                return false;
            }

            // The upload is sized from the SOURCE's dimensions, so a raster that disagrees with them
            // would be uploaded as the wrong number of texels and draw a plausible-looking wrong
            // picture. Decline instead; the float path is always correct.
            if (raster.Length != (long)imageWidth * imageHeight)
            {
                return false;
            }

            return TryUploadChannelTexture(raster, textureSlot, imageWidth, imageHeight);
        }

        /// <summary>
        /// Uploads document textures based on the current channel view.
        /// Call when <see cref="ViewerState.NeedsTextureUpdate"/> is true.
        /// </summary>
        public void UploadDocumentTextures(IPreviewSource source, ViewerState state)
        {
            state.NeedsTextureUpdate = false;
            state.StatusMessage = "Preparing display...";


            // Retain the OUTGOING pixels before anything overwrites them. This is the only moment they
            // are still resident, which is why the request is consumed here and not where it was made.
            if (state.RetainBeforePixelsRequested)
            {
                state.RetainBeforePixelsRequested = false;
                Split.PixelsGeneration =
                    !ShouldSkipBeforePixelCache(EstimatedBeforePixelBytes) && TryRetainImageTexturesAsBefore()
                        ? state.SourceGeneration
                        : null;

                if (Split.PixelsGeneration is not null)
                {
                    // An enhance that lands while the split is already up switches it to comparing
                    // pixels. Only here, because this is the one place that knows the retention actually
                    // SUCCEEDED -- it can be skipped on memory pressure, and a split that flipped to a
                    // pixel comparison with nothing retained would draw no divider at all.
                    Split.AdoptBeforePixels();
                }
            }

            var pixelWidth = source.Width;
            var pixelHeight = source.Height;
            var uploadedSlots = 0;

            // Raw Bayer: upload single channel, GPU shader debayers (bilinear or MHC per DebayerAlgorithm)
            if (source.SensorType is TianWen.Lib.Imaging.SensorType.RGGB && source.ChannelCount == 1
                && state.ChannelView is ChannelView.Composite)
            {
                ChannelTextureCount = 3; // shader produces RGB
                ImageSourceMode = 2; // RawBayer
                uploadedSlots = 1;     // ... from ONE mosaic texture; see ReleaseUnusedChannelTextures
                BayerOffsetX = source.BayerOffsetX;
                BayerOffsetY = source.BayerOffsetY;
                RawBayerDebayerMode = GpuDebayerMode(state.DebayerAlgorithm);
                if (!TryUploadRetainedRaster(source, 0, 0, pixelWidth, pixelHeight))
                {
                    UploadChannelTexture(source.GetChannelData(0), 0, pixelWidth, pixelHeight);
                }
            }
            else if (state.ChannelView is ChannelView.Composite && source.ChannelCount >= 3)
            {
                ChannelTextureCount = 3;
                ImageSourceMode = 0; // ProcessedChannels
                uploadedSlots = 3;

                for (var i = 0; i < 3; i++)
                {
                    if (!TryUploadRetainedRaster(source, i, i, pixelWidth, pixelHeight))
                    {
                        UploadChannelTexture(source.GetChannelData(i), i, pixelWidth, pixelHeight);
                    }
                }
            }
            else
            {
                ChannelTextureCount = 1;
                ImageSourceMode = source.ChannelCount == 1 ? 1 : 0; // RawMono or ProcessedChannels
                uploadedSlots = 1;

                // Composite reaches this branch only for an image with fewer than 3 channels (the
                // 3-channel composite is handled above), so channel 0 is what it displays. The
                // mapping itself lives on ChannelView so the cursor readout resolves it the same way.
                var channelIndex = state.ChannelView.DisplayedSourceChannel(source.ChannelCount) ?? 0;

                // Note the asymmetry: the SOURCE channel is channelIndex, the texture slot is 0. A
                // single-channel view of a 3-channel image uploads (say) blue into slot 0, so the
                // raster lookup has to use the source index while the upload uses the slot.
                if (!TryUploadRetainedRaster(source, channelIndex, 0, pixelWidth, pixelHeight))
                {
                    UploadChannelTexture(source.GetChannelData(channelIndex), 0, pixelWidth, pixelHeight);
                }
            }

            // The view may sample fewer channels than the last one did, and nothing else shrinks the
            // slots it stopped using.
            ReleaseUnusedChannelTextures(uploadedSlots);

            UploadHistogramData(source);

            // A DOCUMENT load ends a burst of uploads, so the host-visible scratch the uploads went
            // through can go back. A LIVE frame does not -- this same method runs per frame for a camera
            // feed (LiveSessionTab, GuiderTab), where releasing the buffer would mean an alloc/free every
            // frame on the imaging hot path.
            //
            // SourceGeneration is what tells them apart, and it is an existing fact rather than a new
            // flag: ViewerController bumps it on each source replacement and nothing in the live path
            // touches it, so a camera feed holds one generation for its whole life while every opened
            // file gets a fresh one. Deliberately not a size threshold -- a large live frame and a
            // document look identical by size, which is exactly the case that must not churn.
            if (state.SourceGeneration != _lastUploadSourceGeneration)
            {
                _lastUploadSourceGeneration = state.SourceGeneration;
                TrimUploadScratch();
            }

            state.StatusMessage = null;
        }

        /// <summary>
        /// The <see cref="ViewerState.SourceGeneration"/> the last upload belonged to. Starts at 0, which
        /// is the generation a never-replaced source has, so a live feed never trims.
        /// </summary>
        private int _lastUploadSourceGeneration;

        /// <summary>
        /// Releases whatever host-visible scratch the texture uploads went through. Called once per
        /// document load, after the last channel. No-op by default: a backend with no such buffer, and
        /// the offline renderer used by the layout tests, have nothing to release.
        /// </summary>
        protected virtual void TrimUploadScratch()
        {
        }

        // -----------------------------------------------------------------------
        // Font resolution
        // -----------------------------------------------------------------------

        /// <summary>
        /// Resolves the faces this viewer draws with, for any role a host has not already supplied.
        /// </summary>
        /// <remarks>
        /// <para>Per ROLE rather than all-or-nothing. A STANDALONE host (tianwen-fits) is the whole app and
        /// has no chrome to inherit from, so it must resolve everything itself; EMBEDDED in a window that
        /// already has a face, that face is kept -- this viewer sits inside that window and should not
        /// label itself differently, and it must certainly not overwrite the window's font for everyone
        /// else sharing those settings. Guarding each role separately rather than returning early on the
        /// text face means a host that pushes one role and not another cannot leave the rest unresolved.</para>
        /// <para>Every caller of an emoji mark still needs a non-emoji fallback: an unavailable glyph does
        /// not draw a placeholder, it draws NOTHING, and a button whose only mark silently disappears is
        /// worse than one built from rectangles. <c>VkGuiRenderer</c> deliberately does not push its fonts
        /// into the preview / guide-cam viewers, which is why this resolves rather than waiting to be told.</para>
        /// </remarks>
        protected void ResolveFontPath()
        {
            // The same single resolve the GUI chrome runs, cached process-wide. Two copies of the probe is
            // how this viewer ended up on the host's monospace default while the GUI had a known face --
            // and Consolas carries no check mark, so the plate-solve tick drew nothing at all.
            var fonts = BundledFonts.Resolve();

            if (string.IsNullOrEmpty(FontPath) && fonts.Text.Length > 0)
            {
                // FontPath is the inherited PixelWidgetBase owner (the layout helpers default to it).
                FontPath = fonts.Text;
            }

            if (string.IsNullOrEmpty(EmojiFontPath))
            {
                EmojiFontPath = fonts.Emoji;
            }

            // The viewer had NO coverage chain before this: only the GUI chrome built one, so a codepoint
            // the primary face lacks drew nothing here while the same string rendered fine in the GUI.
            //
            // Adopted only when the face actually in use IS the one the chain was built over. A host that
            // pushed its own text face would otherwise get coverage answers about a DIFFERENT primary --
            // the chain would report a rune drawable because the bundled face carries it, while this
            // viewer draws with the pushed one and shows nothing. Today no host pushes a font here, so
            // this is a guard against a future one rather than a live bug.
            if (FontFallback is null
                && fonts.Fallback is { } chain
                && string.Equals(FontPath, chain.PrimaryFontPath, StringComparison.Ordinal))
            {
                FontFallback = chain;
            }
        }

        /// <summary>
        /// Draws one glyph from a named face, centred in the given box. Used for the emoji marks, whose
        /// face is not <see cref="PixelWidgetBase{TSurface}.FontPath"/>.
        /// </summary>
        protected void DrawGlyphCentred(string glyph, string fontPath, float x, float y,
            float boxW, float boxH, float fontSize, RGBAColor32 color)
        {
            if (string.IsNullOrEmpty(fontPath) || string.IsNullOrEmpty(glyph))
            {
                return;
            }

            // Clipped to the box, unlike DrawText's full-surface-width rect: a colour emoji's advance is
            // wider than its ink and must not spill into the label beside it.
            var rect = new RectInt(
                new PointInt((int)(x + boxW), (int)(y + boxH)),
                new PointInt((int)x, (int)y));
            Renderer.DrawText(glyph.AsSpan(), fontPath, fontSize, color, rect,
                TextAlign.Center, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Main render orchestration
        // -----------------------------------------------------------------------

        public void Render(IPreviewSource? source, ViewerState state)
        {
            BeginFrame();

            // Tell the widget base where the pointer is, for THIS frame's layout: it resolves the
            // dropdown row highlight and any Layout HoverBackground during paint, because the widget
            // that drew the geometry is the only thing that can say what the pointer is over. Set here
            // rather than in the move handlers so both hosts get it from the one position they already
            // track, and so the value can never be a frame stale. Nothing set it before, which is why
            // no menu in the viewer -- Zoom, the "?" panel, the new context menu -- ever lit a row
            // under the mouse; only the keyboard's HighlightIndex showed.
            Pointer = state.MouseScreenPosition;

            // One preparation pass per frame, wherever it happens. A host that caches the image layer
            // has to render it BEFORE the main render pass opens (render passes cannot nest), which
            // means the layout and the uniforms must already be decided by the time this method runs.
            // PrepareFrame is idempotent, so a host that does none of that just gets the work here and
            // no caching -- the fallback is "behaves exactly as it always did", not "renders wrong".
            PrepareFrame(source, state);
            var document = _document;

            // Draw image FIRST so UI chrome paints on top of it. The stretch and the grid WCS were
            // resolved in PrepareFrame, because the cached image layer needs them before this point.
            if (ImageWidth > 0 && ImageHeight > 0)
            {
                RenderImage(source, state, _preparedStretch, _preparedGridWcs);
            }

            // Hand-off point for the AI capability probe: the Task IS the synchronisation primitive,
            // so nothing shared crosses threads and there is no lock on the render path.
            CollectAiCapabilities(state);

            // UI chrome (drawn on top of image). Skipped wholesale for an embedded chromeless preview.
            if (!state.HideChrome)
            {
                RenderToolbar(document, state);
            }

            if (state.ShowFileList)
            {
                RenderFileList(state);
            }

            // Paint + hit-bind the file-list resize divider (the Split's draw==hit node) from the
            // single layout pass -- the grab region is exactly the drawn bar. No-op when there is no
            // file list (no divider node was arranged).
            PaintLayout(_layoutArranged);

            if (state.ShowGrid && document?.Wcs is { HasCDMatrix: true } wcs)
            {
                RenderGridLabels(state, wcs);
            }

            if (state.ShowStarOverlay && document?.Stars is { Count: > 0 } stars)
            {
                RenderStarOverlay(state, stars);
            }

            if (state.ShowOverlays && document?.Wcs is { HasCDMatrix: true } overlayWcs && CelestialObjectDB?.Value?.Value is { } db)
            {
                RenderOverlays(state, overlayWcs, db);
            }

            // Caller-driven sky annotations (polar alignment, plate-solve verification,
            // target markers, mosaic panel boundaries...). Generic primitive; the
            // renderer doesn't know what the markers represent.
            // The annotation WCS is the document's (still image) or, for a document-less live source, the
            // caller-supplied OverrideWcs (polar-align solves the live preview frame and hands the WCS in).
            var annotationWcs = document?.Wcs is { HasCDMatrix: true } docWcs
                ? docWcs
                : (OverrideWcs is { HasCDMatrix: true } ovrWcs ? ovrWcs : null as WCS?);
            if (!Annotation.IsEmpty && annotationWcs is { } annWcs)
            {
                RenderWcsAnnotation(state, annWcs);
            }

            if (state.ShowHistogram && source is not null)
            {
                RenderHistogram(source, state);
            }

            if (state.ShowInfoPanel && source is not null)
            {
                RenderInfoPanel(source, state);
            }

            // SER transport bar in its reserved strip (only present for a multi-frame sequence).
            if (state.IsSequence)
            {
                RenderTransportBar(state);
            }

            if (!state.HideChrome)
            {
                RenderStatusBar(document, state);
            }

            // Dropdown overlays: rendered last so their clickables win z-order
            // (RegisterClickable resolves by paint order). RenderDropdownMenu is
            // a no-op when the state is closed. Toolbar-driven, so skipped with the chrome.
            if (!state.HideChrome && !string.IsNullOrEmpty(FontPath))
            {
                RenderDropdownMenu(state.ToolbarDropdown, FontPath, ToolbarFontSize,
                    bgColor: GuiTheme.Palette.PanelBg,
                    highlightColor: GuiTheme.Palette.Selection,
                    textColor: GuiTheme.Palette.BodyText,
                    borderColor: GuiTheme.Palette.SeparatorStrong,
                    viewportWidth: Width,
                    viewportHeight: Height);
            }

            // Last of all, so it paints over every other piece of chrome.
            if (!state.HideChrome)
            {
                RenderHoverTooltip(state);
            }

            // Consumed: the next frame prepares again, here or in its host's pre-pass.
            _framePrepared = false;
        }

        // Set by PrepareFrame, read by Render. Not a per-call parameter because the whole point is that
        // the decisions can be made in a different call than the one that draws them.
        private bool _framePrepared;
        private StretchUniforms _preparedStretch;
        private WCS? _preparedGridWcs;

        /// <summary>
        /// How many times <see cref="PrepareFrame"/> has actually done its work. Exposed because the
        /// guard it counts is otherwise UNOBSERVABLE: preparing twice in one frame happens to be
        /// harmless (measuring, arranging and clamping are all idempotent), so a test asserting on the
        /// resulting layout passes whether the guard is there or not. What the guard buys is the work
        /// not being repeated every single frame, and this is the only way to see that.
        /// </summary>
        internal int FramePreparations { get; private set; }

        /// <summary>
        /// Everything a frame decides before anything is drawn: per-document calibration restore, the
        /// toolbar band measurement, the one layout pass, the image placement, and the stretch uniforms
        /// plus grid WCS the image draw consumes. Idempotent within a frame.
        /// </summary>
        /// <remarks>
        /// <para>Separate from <see cref="Render"/> because a cached image layer has to be rendered
        /// before the main render pass opens, and deciding what to render into it needs the pane rect,
        /// the placement and the uniforms -- all of which used to be computed inside Render, i.e. too
        /// late to be of any use. A host that renders a cached layer calls this first; Render then finds
        /// the work already done.</para>
        /// <para>A host that does NOT call it loses nothing: Render calls it itself and the viewer
        /// behaves exactly as before. That is deliberate -- the failure mode for forgetting to wire a
        /// pre-pass is "no caching", never "a wrong frame".</para>
        /// </remarks>
        public void PrepareFrame(IPreviewSource? source, ViewerState state)
        {
            if (_framePrepared)
            {
                return;
            }

            _state = state;
            _source = source;
            // Still-only features (plate solve, stars, colour calibration, WCS overlays, info panel)
            // operate on a document; a SER source is not one, so document is null and they stay inactive.
            var document = source as AstroImageDocument;
            _document = document;

            // Textures FIRST, before anything in this frame samples them. A document swap recreates the
            // channel textures, and the recreate destroys the old views after draining PRIOR frames; it
            // cannot un-record what THIS frame's command buffer already holds. The upload used to run
            // from each host's render callback, which comes AFTER the cached-layer pre-pass: the pass
            // bound the old views, the upload destroyed them, the frame was submitted with a dangling
            // view and the GPU faulted. That is the watchdog (LiveKernelEvent 141) that took the Store
            // viewer down on 2026-08-27 and, reproduced under the validation layer, reads
            // "vkCmdBindDescriptorSets(): ... invalid state ... VkImageView was destroyed" followed by
            // VK_ERROR_DEVICE_LOST. PrepareFrame is the one point every host passes before recording
            // anything that samples, so the upload lives here and nowhere else; it also means the layout
            // below already sees the new document's size instead of lagging it by a frame.
            if (source is not null && state.NeedsTextureUpdate && source.Width > 0 && source.Height > 0)
            {
                UploadDocumentTextures(source, state);
            }

            RestoreDocumentCalibration(document, state);

            // The toolbar band's HEIGHT is an input to the layout pass below, and what decides it is the
            // measured labels (which change: "Stars" -> "Stars: 5893") against the window width. So the
            // toolbar is measured FIRST and the widths are kept; the placement pass inside RenderToolbar
            // re-walks arithmetic only, and no label is measured twice.
            PrepareToolbarLayout(document, state);

            // Single layout pass: every pane rect (file list / image / info panel) and the image
            // placement derive from this ONE arrangement -- no per-consumer recomputation.
            ComputeLayout(state);
            ComputeImagePlacement(state);

            _preparedStretch = source?.ComputeStretchUniforms(
                    state.StretchMode, state.StretchParameters,
                    bgNeutralizationStrength: state.BackgroundNeutralizationStrength,
                    manualWhiteBalance: state.ManualWhiteBalance,
                    applyColorCalibration: state.ColorCalibrationEnabled)
                ?? new StretchUniforms(StretchMode.None, 1f, default, default, default, default, default);

            // Grid WCS: the document's (still image), or the caller-supplied OverrideWcs for a
            // document-less live source (a plate-solved preview frame). GPU grid only; the RA/Dec labels
            // stay document-gated in RenderGridLabels (a live preview shows grid lines, not labels).
            _preparedGridWcs = !state.ShowGrid
                ? null as WCS?
                : (document?.Wcs is { HasCDMatrix: true } w
                    ? w
                    : (OverrideWcs is { HasCDMatrix: true } ow ? ow : null as WCS?));

            _framePrepared = true;
            FramePreparations++;
        }

        /// <summary>
        /// Per-document calibration caches (BackgroundNeutralization, ColorCalibration) are null on a
        /// freshly loaded doc. If the user had the toggle on for the previous file, restore the visual by
        /// recomputing for the new doc -- otherwise the stretch falls back to identity gains and the
        /// image looks cast-coloured until the user re-clicks Calibrate/NeutBg.
        /// </summary>
        private void RestoreDocumentCalibration(AstroImageDocument? document, ViewerState state)
        {
            if (document is null)
            {
                return;
            }

            // P19: point this frame at the run's display anchor (or make it one) BEFORE anything below
            // reads a statistic off it -- the background neutralisation two lines down is solved from
            // PerChannelBackground, which is the anchor's while one is held.
            ReconcileDisplayAnchor(document, state);

            // Always reapply the current method when the toggle is on -- not just when the doc's cached
            // gain is null. Otherwise a cached doc that was previously viewed under a different method
            // (e.g. Mean) keeps its stale Mean gains even though the toolbar shows Min pivot. The doc's
            // per-method dict makes the re-call a cheap dictionary lookup.
            if (state.BackgroundNeutralizationEnabled)
            {
                // Solved for the WB that will actually be applied: with the calibration toggled off the
                // gains for the calibrated triple would re-tint a frame that no longer receives it.
                document.ComputeBackgroundNeutralization(state.BackgroundNeutralizationMethod, state.ColorCalibrationEnabled);
            }

            // ColorCalibration auto-retrigger on file switch. The ColorCalibrationInFlight guard inside
            // TryStartColorCalibration ensures we don't spawn a new SPCC task every frame while the
            // previous one is still running (which would freeze the UI).
            if (state.ColorCalibrationEnabled
                && document.ColorCalibration is null
                && !document.ColorCalibrationInFlight
                && !document.ColorCalibrationAttempted
                && document.Stars is { Count: >= 5 })
            {
                TryStartColorCalibration(state);
            }
        }

        // The frame whose display statistics every comparable frame of this folder is shown with, and
        // the folder it belongs to. Held here rather than on ViewerState because it is a DOCUMENT: the
        // state object carries no pixels, and scoping the anchor to the folder is what keeps a run's
        // worth of retained image from outliving the browsing run that wanted it.
        private AstroImageDocument? _displayAnchor;
        private string? _displayAnchorFolder;

        /// <summary>
        /// Reconciles <paramref name="document"/> against the run's display anchor, per
        /// <see cref="DisplayCarry"/>. Idempotent and re-run every frame, so a document served again
        /// from the cache is pointed by the same rule that pointed it the first time.
        /// </summary>
        private void ReconcileDisplayAnchor(AstroImageDocument document, ViewerState state)
        {
            // A different folder is a different run. Dropping the reference here is also what releases
            // the previous folder's retained document.
            if (!string.Equals(_displayAnchorFolder, state.CurrentFolder, StringComparison.OrdinalIgnoreCase))
            {
                _displayAnchor = null;
                _displayAnchorFolder = state.CurrentFolder;
            }

            var previous = _displayAnchor;
            _displayAnchor = DisplayCarry.Apply(document, previous, state.CarryDisplayAcrossFrames);

            // A blink that walks onto a frame the anchor cannot describe is comparing two different
            // fields, so it stops and says which file ended it -- rather than carrying on past the point
            // where the comparison meant anything. Only a CHANGE of anchor counts: the first frame of a
            // run installs one from nothing, which is not a mismatch.
            if (state.IsBlinking && previous is not null && !ReferenceEquals(previous, _displayAnchor))
            {
                state.IsBlinking = false;
                state.StatusMessage =
                    $"Blink stopped: {Path.GetFileName(document.FilePath)} does not match the first frame";
            }
        }

        // -----------------------------------------------------------------------
        // Image rendering: computes placement, delegates to abstract
        // -----------------------------------------------------------------------

        private void RenderImage(IPreviewSource? source, ViewerState state, StretchUniforms stretch, WCS? gridWcs)
        {
            // Placement (fit/zoom/pan/centering) was computed once in ComputeImagePlacement
            // from the arranged image-pane rect -- read it rather than recompute the formula.
            var p = _placement;
            var live = DisplayRendition.FromState(stretch, state);

            // Restate the track, settle the pin against the rendition being shown right now (the one
            // place the live uniforms exist), and drop a comparison whose source has been replaced.
            Split.SetTrack(_layout.ImageArea);
            Split.ConsumePinRequest(live, DisplayControls.FromState(state));
            if (Split.DropIfStale(state.SourceGeneration))
            {
                ReleaseBeforeImageTextures();
            }

            var area = _layout.ImageArea;

            if (Split.ResolveDividerX(HasBeforeImageTextures, DpiScale) is not { } splitX)
            {
                // Clipped to the pane, for the same reason the split halves below are: the quad is
                // sized to the ZOOMED image, so once it exceeds the pane it reaches under the file
                // list, the info panel and the toolbar -- and DIR.Lib's clip stack is a real
                // vkCmdSetScissor (VkRenderer.ApplyClip), so those fragments are rejected before the
                // shader runs rather than shaded and painted over. Every one of them would otherwise
                // pay the full demosaic + stretch to end up behind opaque chrome.
                //
                // This path had NO clip at all while the split path had one per half, so the ordinary
                // single-image view was the worse-bounded of the two.
                // A cached layer holding this exact content is a textured quad instead of a
                // demosaic + stretch over every pixel of the pane. It answers false for anything
                // it is not certain about, and then this draws as it always did.
                if (TryDrawImageFromCachedLayer())
                {
                    return;
                }

                PushClip(area.X, area.Y, area.Width, area.Height);
                RenderImageQuad(source, state, live, gridWcs,
                    p.OffsetX, p.OffsetY, p.OffsetX + p.DrawW, p.OffsetY + p.DrawH, Width, Height,
                    RenditionSlot.Live, sampleBeforeChannels: false);
                PopClip();
                return;
            }

            var comparesPixels = Split.ComparesPixels;
            var comparison = Split.ComparisonRendition(live);

            // Both halves draw the WHOLE quad and are cut down by the clip, so the two renditions stay
            // in identical pan/zoom/projection space and features line up across the divider.
            //
            // Clipping goes through DIR.Lib's stack, which intersects with whatever the host already
            // clipped and restores it on pop. Two reasons it must not be a raw scissor: the quad extends
            // BEYOND the image area when zoomed in (ConfineToViewport lets it cover the pane rather than
            // sit inside it), and a scissor set here would REPLACE an enclosing clip instead of narrowing
            // it, with nothing to put it back.
            PushClip(area.X, area.Y, splitX - area.X, area.Height);
            RenderImageQuad(source, state, comparison, gridWcs,
                p.OffsetX, p.OffsetY, p.OffsetX + p.DrawW, p.OffsetY + p.DrawH, Width, Height,
                RenditionSlot.Comparison, sampleBeforeChannels: comparesPixels);
            PopClip();

            PushClip(splitX, area.Y, area.Right - splitX, area.Height);
            RenderImageQuad(source, state, live, gridWcs,
                p.OffsetX, p.OffsetY, p.OffsetX + p.DrawW, p.OffsetY + p.DrawH, Width, Height,
                RenditionSlot.Live, sampleBeforeChannels: false);
            PopClip();

            RenderSplitDivider(area, splitX);
            RenderSplitLabels(area, splitX, state);
        }

        /// <summary>
        /// The before/after split (docs/plans/before-after-slider.md). A control that owns its whole
        /// state, so nothing about it lives on <see cref="ViewerState"/> and no host dispatcher needs a
        /// branch for it.
        /// </summary>
        public SplitCompareController Split { get; } = new SplitCompareController();

        /// <summary>
        /// Tracker for background work the viewer starts, set by the host. Null leaves the work
        /// guarded but untracked, so it is still logged and still cannot fault silently -- it just is
        /// not drained at shutdown.
        /// </summary>
        public BackgroundTaskTracker? Tracker { get; set; }

        /// <summary>Logger for background work and diagnostics, set by the host.</summary>
        public ILogger? Logger { get; set; }

        /// <summary>
        /// The host's lifetime token, so background work the viewer starts ends when the app does.
        /// </summary>
        public CancellationToken AppToken { get; set; }

        /// <summary>
        /// Optional host hook that reports what AI capability this install has, already formatted for
        /// display. Shown in the "?" panel; <c>null</c> means the host wired no AI stack and the panel
        /// says so rather than implying anything is broken.
        /// <para>
        /// Returns lines rather than a report object on purpose: this assembly does not reference
        /// <c>TianWen.AI.Imaging</c>, and it should not start doing so to render a list of strings.
        /// The host owns the model resolver and the RC-Astro wrapper, so it is also the only place
        /// that can answer honestly for the install it IS.
        /// </para>
        /// <para>
        /// It is a delegate rather than a value because the probe launches processes (one license
        /// check per RC product) and must never run at DI time or on the render thread -- the whole
        /// point of <c>AddRcAstroAi()</c> deferring its license probe to first use. So the panel asks
        /// for it, once, on the first open.
        /// </para>
        /// </summary>
        /// <remarks><c>IReadOnlyList</c> rather than <c>ImmutableArray</c> because
        /// <c>BackgroundTaskTracker.TryCollect&lt;TResult&gt;</c> constrains TResult to a reference
        /// type, and ImmutableArray is a struct.</remarks>
        public Func<CancellationToken, Task<IReadOnlyList<string>>>? AiCapabilityProbe { get; set; }

        /// <summary>Design-unit width of the drawn before/after divider.</summary>
        private const float BaseSplitDividerWidth = 2f;

        /// <summary>Design-unit gap between the divider and the label on either side of it.</summary>
        private const float BaseSplitLabelGap = 10f;

        // Both halves get the SAME grey, deliberately. Colouring them differently would read as one
        // side being the good one, and the entire point of the control is to let the eye decide that.
        private static readonly RGBAColor32 SplitLabelText = RGBAColor32.FromFloat(0.78f, 0.78f, 0.78f, 1f);

        /// <summary>Design-unit width of the divider's grab band -- wider than the line it draws, so the
        /// thing is actually catchable with a mouse.</summary>
        private const float BaseSplitGrabWidth = 12f;

        // The divider line plus its grab band, registered from the SAME rect it painted -- so the grab
        // is the line by construction, and the press ARMS THE DRAG from here through the region's own
        // onClick. That is what keeps the press out of every host's dispatcher: the viewer has two of
        // them, and a branch added to one of them alone is invisible in the other.
        private void RenderSplitDivider(RectF32 area, float splitX)
        {
            var lineW = MathF.Max(1f, BaseSplitDividerWidth * DpiScale);
            var grabW = MathF.Max(lineW, BaseSplitGrabWidth * DpiScale);
            var color = Split.IsDragging ? ResizeHandleActiveColor : ResizeHandleIdleColor;

            FillRect(splitX - lineW / 2f, area.Y, lineW, area.Height, color);
            RegisterClickable(splitX - grabW / 2f, area.Y, grabW, area.Height,
                new ResizeHandleHit("Split"), onClick: _ => Split.BeginDrag(), cursor: CursorKind.ResizeEW);
        }

        /// <summary>
        /// Names the two halves, on the side of the divider each one occupies.
        /// </summary>
        /// <remarks>
        /// Drawn AFTER the divider so it is never overdrawn, and at the top of the image pane rather
        /// than beside the handle, so the labels do not move while the divider is being dragged --
        /// text that slides under the cursor is harder to read at exactly the moment it is wanted.
        /// </remarks>
        private void RenderSplitLabels(in RectF32 area, float splitX, ViewerState state)
        {
            if (string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var (leftText, rightText) = Split.HalfLabels(DisplayControls.FromState(state),
                pixelsEnhanced: state.IsEnhanced);
            var fontSize = ToolbarFontSize;
            var gap = BaseSplitLabelGap * DpiScale;
            var top = area.Y + PanelPadding;

            // Right-aligned into the left half, left-aligned into the right half, so each label sits
            // against the divider it belongs to and reads as attached to its own side.
            //
            // A label is DROPPED when its own half is too narrow to hold it, rather than clamped into
            // the pane. Clamping alone put the left label across the divider, where the right label's
            // backing then painted over its tail and left a fragment ("Pi | Live") that reads as a
            // rendering fault. Half a name is worse than no name: the whole point is to say which side
            // is which, and a truncated one says something else.
            var leftW = LabelWidth(leftText, fontSize);
            if (splitX - gap - leftW >= area.X + PanelPadding)
            {
                DrawSplitLabel(leftText, area, splitX - gap - leftW, top, fontSize);
            }

            if (splitX + gap + LabelWidth(rightText, fontSize) <= area.Right - PanelPadding)
            {
                DrawSplitLabel(rightText, area, splitX + gap, top, fontSize);
            }
        }

        private float LabelWidth(string text, float fontSize) => MeasureText(text, fontSize) + PanelPadding * 2f;

        private void DrawSplitLabel(string text, in RectF32 area, float desiredX, float top, float fontSize)
        {
            var w = LabelWidth(text, fontSize);
            var h = fontSize + PanelPadding;
            // A floor only -- the caller has already refused to draw a label that does not fit its own
            // half, so this just guarantees a sane rect for the degenerate pane sizes.
            var x = Math.Clamp(desiredX, area.X + PanelPadding, MathF.Max(area.X + PanelPadding, area.Right - w - PanelPadding));

            // Same backing as the toolbar tooltip -- a label over a bright star field is unreadable
            // without one, and matching an existing surface keeps it from looking like new chrome.
            FillRect(x, top, w, h, ViewerTheme.Palette.PanelBg);
            DrawText(text, x + PanelPadding, top + (h - fontSize) / 2f, fontSize, SplitLabelText);
        }
        // -----------------------------------------------------------------------
        // Text helpers
        // -----------------------------------------------------------------------

        private void DrawTextLine(ref float y, float x, string text, RGBAColor32 color)
        {
            // Canonical RGBAColor32 path (the inherited PixelWidgetBase.DrawText), fed from ViewerTheme.
            // Near/Near + a generous width preserves the old left-aligned, non-clipped behaviour.
            // The inherited DrawText no-ops on an empty FontPath, so no null-forgiving is needed.
            DrawText(text.AsSpan(), FontPath, x, y, Width - x, FontSize * 1.3f, FontSize, color, TextAlign.Near, TextAlign.Near);
            y += TextLineAdvance;
        }

        // Cached per (face, size) rather than per line: the extent is a property of the font, and this
        // runs once per text line in a panel that can hold thirty of them.
        private float _lineAdvance;
        private float _lineAdvanceForSize = -1f;
        private string _lineAdvanceForFont = string.Empty;

        /// <summary>
        /// How far down to step for the next line of panel text.
        /// </summary>
        /// <remarks>
        /// <para>Derived from the FACE's own ascender-to-descender extent, not from the font size plus a
        /// constant. It used to be <c>FontSize + 2f</c>, which is a gap tuned to exactly one font: at
        /// size 13 Consolas measures 12 px tall and DejaVu Sans measures 13, so bundling DejaVu left one
        /// pixel of leading and the info panel visibly crammed. A face-derived advance cannot acquire
        /// that bug again when the face changes.</para>
        /// <para>The old value is kept as a FLOOR so no existing layout can tighten, only loosen.
        /// Measured with "Mgjq" because it spans cap height and descender; measuring the actual line
        /// would make the step depend on whether that line happens to contain a descender, so
        /// consecutive lines would sit at uneven distances.</para>
        /// </remarks>
        private float TextLineAdvance
        {
            get
            {
                if (_lineAdvanceForSize != FontSize
                    || !string.Equals(_lineAdvanceForFont, FontPath, StringComparison.Ordinal))
                {
                    var floor = FontSize + 2f;
                    if (FontPath.Length == 0)
                    {
                        // No face resolved: DrawText no-ops anyway, so any advance is arbitrary. Keep the
                        // historical one rather than asking the renderer to measure with no font.
                        _lineAdvance = floor;
                    }
                    else
                    {
                        var extent = Renderer.MeasureText("Mgjq".AsSpan(), FontPath, FontSize).Height;
                        _lineAdvance = MathF.Max(floor, extent + MathF.Round(FontSize * 0.3f));
                    }
                    _lineAdvanceForSize = FontSize;
                    _lineAdvanceForFont = FontPath;
                }
                return _lineAdvance;
            }
        }

        /// <summary>
        /// A panel section heading: the name, then a hairline rule across the remaining width.
        /// </summary>
        /// <remarks>
        /// The headings used to be written as <c>"-- Statistics --"</c>, which spends four characters and
        /// two spaces of a narrow panel on decoration that the heading COLOUR already provides, and reads
        /// as ASCII art next to real text. A drawn rule is also font-independent: the tidier characters
        /// for the job (an em dash, box-drawing U+2500) are exactly the kind of codepoint a host face can
        /// lack, and a missing glyph here would draw nothing at all.
        /// </remarks>
        private void DrawSectionHeading(ref float y, float x, string title, float availableWidth)
        {
            var color = ViewerTheme.Palette.HeaderText;
            var titleWidth = MeasureText(title, FontSize);
            DrawText(title.AsSpan(), FontPath, x, y, Width - x, FontSize * 1.3f, FontSize, color,
                TextAlign.Near, TextAlign.Near);

            // Sits on the text's optical middle rather than its baseline, so the rule reads as continuing
            // through the words instead of underlining them.
            var ruleY = MathF.Round(y + FontSize * 0.5f);
            var ruleStart = x + titleWidth + FontSize * 0.5f;
            var ruleEnd = x + availableWidth;
            if (ruleEnd - ruleStart >= FontSize)
            {
                // Dimmed: a rule at full heading strength competes with the heading it is separating.
                var rule = new RGBAColor32(color.Red, color.Green, color.Blue, (byte)(color.Alpha / 2));
                DrawLine(ruleStart, ruleY, ruleEnd, ruleY, rule);
            }

            y += TextLineAdvance;
        }

        private void DrawWrappedTextLine(ref float y, float x, string text, float maxWidth, RGBAColor32 color)
        {
            var textWidth = MeasureText(text, FontSize);
            if (textWidth <= maxWidth)
            {
                DrawTextLine(ref y, x, text, color);
                return;
            }

            var colonIdx = text.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx < 0)
            {
                DrawTextLine(ref y, x, text, color);
                return;
            }

            var label = text[..(colonIdx + 2)];
            var value = text[(colonIdx + 2)..];
            var indent = new string(' ', label.Length);

            var remaining = value;
            var firstLine = true;
            while (remaining.Length > 0)
            {
                var prefix = firstLine ? label : indent;
                var lineText = prefix + remaining;
                if (MeasureText(lineText, FontSize) <= maxWidth)
                {
                    DrawTextLine(ref y, x, lineText, color);
                    break;
                }

                var fit = remaining.Length;
                while (fit > 1 && MeasureText(prefix + remaining[..fit], FontSize) > maxWidth)
                {
                    fit--;
                }

                var breakAt = -1;
                for (var i = fit; i > 0; i--)
                {
                    if (remaining[i - 1] is ' ' or '-')
                    {
                        breakAt = i;
                        break;
                    }
                }

                if (breakAt > 0)
                {
                    fit = breakAt;
                }

                DrawTextLine(ref y, x, prefix + remaining[..fit], color);
                remaining = remaining[fit..];
                firstLine = false;
            }
        }

        /// <summary>
        /// Measures the width of text in pixels.
        /// </summary>
        protected float MeasureText(string text, float fontSize)
        {
            if (string.IsNullOrEmpty(FontPath))
            {
                return text.Length * fontSize * 0.6f;
            }

            return Renderer.MeasureText(text.AsSpan(), FontPath, fontSize).Width;
        }

        /// <summary>
        /// Draws a single line of text at the given screen position, left/top-aligned.
        /// The destination rect spans to the viewport's right edge so the text is never
        /// clipped horizontally; vertical extent is one line height (fontSize * 1.3).
        /// This is the viewer's only string-overload text helper -- chrome colours come
        /// from <see cref="ViewerTheme"/> or the local state-colour fields.
        /// </summary>
        protected void DrawText(string text, float screenX, float screenY, float fontSize, RGBAColor32 color)
        {
            if (string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var lh = (int)(fontSize * 1.3f);
            var rect = new RectInt(
                new PointInt((int)(screenX + Width), (int)screenY + lh),
                new PointInt((int)screenX, (int)screenY));
            Renderer.DrawText(text.AsSpan(), FontPath, fontSize, color, rect, TextAlign.Near, TextAlign.Near);
        }

    }
}
