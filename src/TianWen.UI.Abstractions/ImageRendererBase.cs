// TODO high priority: cached offscreen framebuffer for partial UI redraws
//
// Problem: every mouse move that changes the status bar pixel readout triggers a full
// Vulkan render pass (image quad + stretch shader + histogram + stars + toolbar + status bar).
// Even with the pixel-change gate and 30fps throttle, GPU usage spikes to 15-25% on mouse hover.
//
// Solution: two-layer rendering with a cached offscreen framebuffer.
//
// Layer 1 — Image content (expensive, rarely changes):
//   Render image quad + stretch shader + star overlay + WCS grid + histogram
//   into a VkImage offscreen framebuffer. Only re-render when:
//   - New image loaded (NeedsTextureUpdate)
//   - Stretch parameters changed (mode, shadows, midtones, highlights, boost, HDR)
//   - Zoom or pan changed
//   - Star overlay toggled
//   - WCS grid toggled
//   - Channel view changed
//
// Layer 2 — UI chrome (cheap, changes on mouse move):
//   Each frame: blit cached Layer 1 framebuffer → render toolbar, status bar,
//   file list, info panel on top. This is just text quads — very cheap.
//
// Implementation steps:
//   1. Add offscreen VkImage + VkFramebuffer to VkFitsImagePipeline (same size as swapchain)
//   2. Add a "blit" shader (fullscreen quad sampling the offscreen texture)
//   3. Add ImageContentDirty flag to ViewerState — set by stretch/zoom/pan/toggle changes
//   4. In OnRender: if ImageContentDirty → render Layer 1 to offscreen → clear flag
//   5. Always: blit offscreen → render chrome overlay → present
//   6. Handle resize: recreate offscreen framebuffer
//
// Expected impact: mouse-hover GPU usage drops from ~20% to <2% (just text rendering).
// The full image render only runs on actual content changes (~1-5 fps during interaction).
//
// Files to change:
//   - SdlVulkan.Renderer: VkRenderer needs render-to-texture support (new feature)
//   - TianWen.UI.Shared/VkFitsImagePipeline.cs: offscreen framebuffer management
//   - TianWen.UI.Shared/VkImageRenderer.cs: split RenderImageQuad into cached/blit paths
//   - TianWen.UI.Abstractions/ImageRendererBase.cs: add ImageContentDirty flag logic
//   - TianWen.UI.Abstractions/ViewerState.cs: add ImageContentDirty property

using System;
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
        private string? _fontPath;

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

        /// <summary>DPI scale factor. Set from framebuffer size / window size ratio.</summary>
        public float DpiScale { get; set; } = 1f;

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
        private float ToolbarHeight => BaseToolbarHeight * DpiScale;
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

        // Histogram LOG-scale toggle button: log-on (blue) and log-off (grey) families,
        // each with a hover-brightened variant. Alpha 0.9 (the histogram is an overlay).
        private static readonly RGBAColor32 HistogramLogOnBg = RGBAColor32.FromFloat(0.20f, 0.30f, 0.50f, 0.9f);
        private static readonly RGBAColor32 HistogramLogOnHoverBg = RGBAColor32.FromFloat(0.25f, 0.35f, 0.55f, 0.9f);
        private static readonly RGBAColor32 HistogramLogOffBg = RGBAColor32.FromFloat(0.25f, 0.25f, 0.28f, 0.9f);
        private static readonly RGBAColor32 HistogramLogOffHoverBg = RGBAColor32.FromFloat(0.35f, 0.35f, 0.40f, 0.9f);

        // -----------------------------------------------------------------------
        // Abstract methods — GPU-specific rendering
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders the image quad with stretch uniforms, optional WCS grid, and viewport placement.
        /// </summary>
        protected abstract void RenderImageQuad(IPreviewSource? source, ViewerState state,
            StretchUniforms stretch, WCS? wcs,
            float left, float top, float right, float bottom, uint projW, uint projH);

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
        /// Uploads histogram data from a preview source. Called once per image load (or sequence open).
        /// </summary>
        public abstract void UploadHistogramData(IPreviewSource source);

        /// <summary>
        /// Returns the histogram display, or null if not yet initialized.
        /// </summary>
        protected abstract HistogramDisplay? GetHistogramDisplay();

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
        /// Uploads per-channel R32f textures. Convenience alias for <see cref="UploadImageTexture"/>.
        /// </summary>
        public void UploadChannelTexture(ReadOnlySpan<float> data, int channel, int imageWidth, int imageHeight)
        {
            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
            UploadImageTexture(data, channel, imageWidth, imageHeight);
        }

        /// <summary>
        /// Uploads document textures based on the current channel view.
        /// Call when <see cref="ViewerState.NeedsTextureUpdate"/> is true.
        /// </summary>
        public void UploadDocumentTextures(IPreviewSource source, ViewerState state)
        {
            state.NeedsTextureUpdate = false;
            state.StatusMessage = "Preparing display...";

            var pixelWidth = source.Width;
            var pixelHeight = source.Height;

            // Raw Bayer: upload single channel, GPU shader debayers (bilinear or MHC per DebayerAlgorithm)
            if (source.SensorType is TianWen.Lib.Imaging.SensorType.RGGB && source.ChannelCount == 1
                && state.ChannelView is ChannelView.Composite)
            {
                ChannelTextureCount = 3; // shader produces RGB
                ImageSourceMode = 2; // RawBayer
                BayerOffsetX = source.BayerOffsetX;
                BayerOffsetY = source.BayerOffsetY;
                RawBayerDebayerMode = GpuDebayerMode(state.DebayerAlgorithm);
                UploadChannelTexture(source.GetChannelData(0), 0, pixelWidth, pixelHeight);
            }
            else if (state.ChannelView is ChannelView.Composite && source.ChannelCount >= 3)
            {
                ChannelTextureCount = 3;
                ImageSourceMode = 0; // ProcessedChannels

                for (var i = 0; i < 3; i++)
                {
                    UploadChannelTexture(source.GetChannelData(i), i, pixelWidth, pixelHeight);
                }
            }
            else
            {
                ChannelTextureCount = 1;
                ImageSourceMode = source.ChannelCount == 1 ? 1 : 0; // RawMono or ProcessedChannels

                var channelIndex = state.ChannelView switch
                {
                    ChannelView.Composite or ChannelView.Channel0 or ChannelView.Red => 0,
                    ChannelView.Channel1 or ChannelView.Green => Math.Min(1, source.ChannelCount - 1),
                    ChannelView.Channel2 or ChannelView.Blue => Math.Min(2, source.ChannelCount - 1),
                    var cv => throw new InvalidOperationException($"Invalid channel view {cv}")
                };

                UploadChannelTexture(source.GetChannelData(channelIndex), 0, pixelWidth, pixelHeight);
            }

            UploadHistogramData(source);
            state.StatusMessage = null;
        }

        // -----------------------------------------------------------------------
        // Font resolution
        // -----------------------------------------------------------------------

        protected void ResolveFontPath()
        {
            var resolved = FontResolver.ResolveSystemFont();
            if (resolved.Length > 0)
            {
                _fontPath = resolved;
            }
        }

        /// <summary>Gets or sets the font path used for text rendering.</summary>
        protected string? FontPath
        {
            get => _fontPath;
            set => _fontPath = value;
        }

        // -----------------------------------------------------------------------
        // Main render orchestration
        // -----------------------------------------------------------------------

        public void Render(IPreviewSource? source, ViewerState state)
        {
            _state = state;
            _source = source;
            // Still-only features (plate solve, stars, colour calibration, WCS overlays, info panel)
            // operate on a document; a SER source is not one, so document is null and they stay inactive.
            var document = source as AstroImageDocument;
            _document = document;
            BeginFrame();

            // Per-document calibration caches (BackgroundNeutralization,
            // ColorCalibration) are null on a freshly loaded doc. If the user
            // had the toggle on for the previous file, restore the visual by
            // recomputing for the new doc -- otherwise the stretch falls back
            // to identity gains and the image looks cast-coloured until the
            // user re-clicks Calibrate/NeutBg.
            if (document is not null)
            {
                // Always reapply the current method when the toggle is on --
                // not just when the doc's cached gain is null. Otherwise a
                // cached doc that was previously viewed under a different
                // method (e.g. Mean) keeps its stale Mean gains even though
                // the toolbar shows Min pivot. The doc's per-method dict
                // makes the re-call a cheap dictionary lookup.
                if (state.BackgroundNeutralizationEnabled)
                {
                    document.ComputeBackgroundNeutralization(state.BackgroundNeutralizationMethod);
                }
                // ColorCalibration auto-retrigger on file switch. The
                // ColorCalibrationInFlight guard inside TryStartColorCalibration
                // ensures we don't spawn a new SPCC task every frame while
                // the previous one is still running (which would freeze the UI).
                if (state.ColorCalibrationEnabled
                    && document.ColorCalibration is null
                    && !document.ColorCalibrationInFlight
                    && document.Stars is { Count: >= 5 })
                {
                    TryStartColorCalibration(state);
                }
            }

            // Single layout pass: every pane rect (file list / image / info panel) and the image
            // placement below derive from this ONE arrangement -- no per-consumer recomputation.
            ComputeLayout(state, _fontPath ?? string.Empty);
            ComputeImagePlacement(state);

            // Draw image FIRST so UI chrome paints on top of it
            if (ImageWidth > 0 && ImageHeight > 0)
            {
                var stretch = source?.ComputeStretchUniforms(
                        state.StretchMode, state.StretchParameters,
                        bgNeutralizationStrength: state.BackgroundNeutralizationStrength,
                        manualWhiteBalance: state.ManualWhiteBalance)
                    ?? new StretchUniforms(StretchMode.None, 1f, default, default, default, default, default);
                // Grid WCS: the document's (still image), or the caller-supplied OverrideWcs for a
                // document-less live source (a plate-solved preview frame). GPU grid only; the RA/Dec labels
                // stay document-gated in RenderGridLabels (a live preview shows grid lines, not labels).
                var gridWcs = !state.ShowGrid
                    ? null as WCS?
                    : (document?.Wcs is { HasCDMatrix: true } w
                        ? w
                        : (OverrideWcs is { HasCDMatrix: true } ow ? ow : null as WCS?));
                RenderImage(source, state, stretch, gridWcs);
            }

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
            PaintLayout(_layoutArranged, _fontPath ?? string.Empty, DpiScale);

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
            // target markers, mosaic panel boundaries...). Generic primitive — the
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

            // Dropdown overlays — rendered last so their clickables win z-order
            // (RegisterClickable resolves by paint order). RenderDropdownMenu is
            // a no-op when the state is closed. Toolbar-driven, so skipped with the chrome.
            if (!state.HideChrome && _fontPath is { } fontPath)
            {
                RenderDropdownMenu(state.ToolbarDropdown, fontPath, ToolbarFontSize,
                    bgColor: new RGBAColor32(0x33, 0x33, 0x38, 0xff),
                    highlightColor: new RGBAColor32(0x33, 0x4d, 0x80, 0xff),
                    textColor: new RGBAColor32(0xe6, 0xe6, 0xe6, 0xff),
                    borderColor: new RGBAColor32(0x59, 0x59, 0x66, 0xff),
                    viewportWidth: Width,
                    viewportHeight: Height);
            }
        }

        // -----------------------------------------------------------------------
        // Image rendering — computes placement, delegates to abstract
        // -----------------------------------------------------------------------

        private void RenderImage(IPreviewSource? source, ViewerState state, StretchUniforms stretch, WCS? gridWcs)
        {
            // Placement (fit/zoom/pan/centering) was computed once in ComputeImagePlacement
            // from the arranged image-pane rect -- read it rather than recompute the formula.
            var p = _placement;
            RenderImageQuad(source, state, stretch, gridWcs,
                p.OffsetX, p.OffsetY, p.OffsetX + p.DrawW, p.OffsetY + p.DrawH, Width, Height);
        }
        // -----------------------------------------------------------------------
        // Text helpers
        // -----------------------------------------------------------------------

        private void DrawTextLine(ref float y, float x, string text, RGBAColor32 color)
        {
            // Canonical RGBAColor32 path (the inherited PixelWidgetBase.DrawText), fed from ViewerTheme.
            // Near/Near + a generous width preserves the old left-aligned, non-clipped behaviour.
            DrawText(text.AsSpan(), _fontPath!, x, y, Width - x, FontSize * 1.3f, FontSize, color, TextAlign.Near, TextAlign.Near);
            y += FontSize + 2f;
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
            if (_fontPath is null)
            {
                return text.Length * fontSize * 0.6f;
            }

            return Renderer.MeasureText(text.AsSpan(), _fontPath, fontSize).Width;
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
            if (_fontPath is null)
            {
                return;
            }

            var lh = (int)(fontSize * 1.3f);
            var rect = new RectInt(
                new PointInt((int)(screenX + Width), (int)screenY + lh),
                new PointInt((int)screenX, (int)screenY));
            Renderer.DrawText(text.AsSpan(), _fontPath, fontSize, color, rect, TextAlign.Near, TextAlign.Near);
        }

    }
}
