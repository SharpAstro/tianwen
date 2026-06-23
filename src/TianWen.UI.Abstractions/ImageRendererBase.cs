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
    public abstract class ImageRendererBase<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
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

        // Toolbar button definitions (label, action, group)
        private static readonly (string Label, ToolbarAction Action, int Group)[] ToolbarButtons =
        [
            ("Open", ToolbarAction.Open, 0),
            ("STF", ToolbarAction.StretchToggle, 1),
            ("Link", ToolbarAction.StretchLink, 1),
            ("Params", ToolbarAction.StretchParams, 1),
            ("Channel", ToolbarAction.Channel, 2),
            ("Debayer", ToolbarAction.Debayer, 2),
            ("Boost", ToolbarAction.CurvesBoost, 2),
            ("HDR", ToolbarAction.Hdr, 2),
            ("Fit", ToolbarAction.ZoomFit, 3),
            ("1:1", ToolbarAction.ZoomActual, 3),
            ("Plate Solve", ToolbarAction.PlateSolve, 4),
            ("Grid", ToolbarAction.Grid, 4),
            ("Objects", ToolbarAction.Overlays, 4),
            ("Stars", ToolbarAction.Stars, 4),
            ("Calibrate", ToolbarAction.ColorCalibrate, 4),
            ("NeutBg", ToolbarAction.BackgroundNeutralize, 4),
            ("SPCC", ToolbarAction.SpccCalibrate, 4),
        ];

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
            return (Width - fileListW - panelW, Height - ToolbarHeight - StatusBarHeight);
        }

        // -----------------------------------------------------------------------
        // Single-source layout pass
        //
        // The whole below-toolbar chrome derives its geometry from ONE arrangement:
        // Layout.Node.Split (file list + draggable divider) wrapping a Dock (right-edge
        // info panel + fill image). Every consumer reads the arranged pane rects + the
        // single image placement below -- the fileListW/panelW/areaW/offsetX formula is
        // no longer copy-pasted per consumer and "happens to agree". The divider is the
        // Split's draw==hit node, so the resize grab region IS the drawn bar.
        // -----------------------------------------------------------------------

        private readonly record struct ViewerLayout(RectF32 FileList, RectF32 ImageArea, RectF32 InfoPanel);

        private readonly record struct ImagePlacement(float OffsetX, float OffsetY, float DrawW, float DrawH, float Scale);

        private ViewerLayout _layout;
        private ImmutableArray<Layout.ArrangedNode<float>> _layoutArranged;
        private ImagePlacement _placement;

        // SER transport bar geometry, computed in ComputeLayout (default/empty for a still image): the
        // whole strip and, within it, the scrub track rect that maps cursor-X <-> frame index.
        private RectF32 _transportRect;
        private RectF32 _scrubTrackRect;

        // Manual white-balance slider track rects (R, G, B), captured in RenderInfoPanel each frame; map a
        // cursor-X <-> WB multiplier in BeginWhiteBalanceDragAt / UpdateWhiteBalanceDrag. Default/empty when
        // the source is monochrome (no WB sliders drawn).
        private readonly RectF32[] _wbTrackRects = new RectF32[3];

        // White-balance slider range (canonical values live on AutoWhiteBalance so the slider extent and the
        // auto-WB clamp stay in lock-step). Log-mapped so neutral (1.0) sits at the track midpoint and an
        // equal gain/cut is symmetric (0.5x left edge <-> 2.0x right edge).
        private const float WbMin = AutoWhiteBalance.MinMultiplier;
        private const float WbMax = AutoWhiteBalance.MaxMultiplier;

        // Wavelet-sharpen layer slider track rects (6 a-trous scales, finest first), captured in
        // RenderWaveletControls each frame; map a cursor-X <-> per-layer gain. Only drawn for the live
        // stacked view. Linear gain in [0, WaveletGainMax]; neutral 1.0.
        private readonly RectF32[] _waveletTrackRects = new RectF32[6];
        private const float WaveletGainMax = 5f;

        /// <summary>Design-unit thickness of the file-list resize divider (the Split divider IS the grab bar).</summary>
        private const float BaseFileListDividerWidth = 6f;

        private void ComputeLayout(ViewerState state, string fontPath)
        {
            var below = new RectF32(0f, ToolbarHeight, Width, Height - ToolbarHeight - StatusBarHeight);

            Layout.Node content = state.ShowInfoPanel
                ? Layout.Builder.Dock(
                    Layout.Builder.Fill(key: "image"),
                    Layout.Builder.Right(Layout.Builder.Fill(key: "infoPanel"), BaseInfoPanelWidth))
                : Layout.Builder.Fill(key: "image");

            Layout.Node root = state.ShowFileList
                ? Layout.Builder.Split(
                    Layout.Builder.Fill(key: "fileList"),
                    content,
                    Layout.Axis.Horizontal,
                    firstExtent: state.FileListWidthBase,
                    dividerThickness: BaseFileListDividerWidth,
                    dividerHit: new ResizeHandleHit("FileList"),
                    dividerColor: state.IsResizingFileList ? ResizeHandleActiveColor : ResizeHandleIdleColor)
                : content;

            _layoutArranged = ArrangeLayout(root, below, fontPath, DpiScale);

            RectF32 fileList = default, image = default, infoPanel = default;
            foreach (var (node, b) in _layoutArranged)
            {
                if (node is Layout.Node.Leaf { Content: Layout.Content.Fill fill })
                {
                    var r = new RectF32(b.X, b.Y, b.Width, b.Height);
                    switch (fill.Key)
                    {
                        case "fileList": fileList = r; break;
                        case "image": image = r; break;
                        case "infoPanel": infoPanel = r; break;
                    }
                }
            }

            // Reserve the transport strip at the bottom of the image pane for a sequence, shrinking the
            // image area so the strip never overlaps the picture. The file list / info panel keep their
            // full height -- the transport belongs to the image column only.
            _transportRect = default;
            if (state.IsSequence)
            {
                var th = MathF.Min(TransportHeight, image.Height);
                var shrunk = new RectF32(image.X, image.Y, image.Width, image.Height - th);
                _transportRect = new RectF32(image.X, shrunk.Y + shrunk.Height, image.Width, th);
                image = shrunk;
            }

            _layout = new ViewerLayout(fileList, image, infoPanel);
        }

        private void ComputeImagePlacement(ViewerState state)
        {
            var area = _layout.ImageArea;
            if (ImageWidth <= 0 || ImageHeight <= 0)
            {
                _placement = new ImagePlacement(area.X, area.Y, 0f, 0f, state.Zoom);
                return;
            }

            var fitScale = MathF.Min(area.Width / ImageWidth, area.Height / ImageHeight);
            if (state.ZoomToFit)
            {
                state.Zoom = fitScale;
            }

            var scale = state.Zoom;
            var drawW = ImageWidth * scale;
            var drawH = ImageHeight * scale;
            var offsetX = area.X + (area.Width - drawW) / 2f + state.PanOffset.X;
            var offsetY = area.Y + (area.Height - drawH) / 2f + state.PanOffset.Y;
            _placement = new ImagePlacement(offsetX, offsetY, drawW, drawH, scale);
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
                var gridWcs = state.ShowGrid && document?.Wcs is { HasCDMatrix: true } w ? w : (WCS?)null;
                RenderImage(source, state, stretch, gridWcs);
            }

            // UI chrome (drawn on top of image)
            RenderToolbar(document, state);

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
            if (!Annotation.IsEmpty && document?.Wcs is { HasCDMatrix: true } annotationWcs)
            {
                RenderWcsAnnotation(state, annotationWcs);
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

            RenderStatusBar(document, state);

            // Dropdown overlays — rendered last so their clickables win z-order
            // (RegisterClickable resolves by paint order). RenderDropdownMenu is
            // a no-op when the state is closed.
            if (_fontPath is { } fontPath)
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
        // Toolbar
        // -----------------------------------------------------------------------

        private void RenderToolbar(AstroImageDocument? document, ViewerState state)
        {
            FillRect(0, 0, Width, ToolbarHeight, ViewerTheme.ToolbarBg);

            // Recompute button bounds every frame — labels can change width
            // (e.g. "Stars" -> "Stars: 5893") which shifts later buttons.
            _toolbarButtonBounds.Clear();

            if (_fontPath is null)
            {
                return;
            }

            var mouseX = state.MouseScreenPosition.X;
            var mouseY = state.MouseScreenPosition.Y;

            var x = PanelPadding;
            var btnH = ToolbarHeight - ButtonSpacing * 2;
            var btnY = ButtonSpacing;
            var textY = (ToolbarHeight - ToolbarFontSize) / 2f;
            var prevGroup = -1;

            for (var i = 0; i < ToolbarButtons.Length; i++)
            {
                var (label, action, group) = ToolbarButtons[i];

                if (prevGroup >= 0 && group != prevGroup)
                {
                    x += ButtonGroupSpacing;
                }
                prevGroup = group;

                var displayLabel = GetToolbarButtonLabel(label, action, document, state);
                var textWidth = MeasureText(displayLabel, ToolbarFontSize);
                var btnW = textWidth + ButtonPaddingH * 2;
                var enabled = IsToolbarButtonEnabled(action, document);
                var active = IsToolbarButtonActive(action, document, state);

                var hovered = enabled && !state.ToolbarDropdown.IsOpen && mouseX >= x && mouseX < x + btnW && mouseY >= btnY && mouseY < btnY + btnH;

                if (!enabled)
                {
                    FillRect(x, btnY, btnW, btnH, ToolbarButtonDisabledBg);
                }
                else if (active && hovered)
                {
                    // Active + hover = the brightest selection blue (matches ViewerTheme's selected role).
                    FillRect(x, btnY, btnW, btnH, ViewerTheme.Palette.Selection);
                }
                else if (active)
                {
                    FillRect(x, btnY, btnW, btnH, ToolbarButtonActiveBg);
                }
                else if (hovered)
                {
                    FillRect(x, btnY, btnW, btnH, ToolbarButtonHoverBg);
                }
                else
                {
                    FillRect(x, btnY, btnW, btnH, ToolbarButtonBg);
                }

                var textBrightness = enabled ? 0.9f : 0.45f;
                DrawText(displayLabel, x + ButtonPaddingH, textY, ToolbarFontSize,
                    RGBAColor32.FromFloat(textBrightness, textBrightness, textBrightness, 1f));

                if (enabled)
                {
                    RegisterClickable(x, btnY, btnW, btnH, new HitResult.ButtonHit(action.ToString()));
                    // Capture rect so left-click can anchor the dropdown beneath the
                    // button (see OpenToolbarDropdown). Only enabled buttons can be
                    // clicked, so we only need their bounds.
                    _toolbarButtonBounds[action] = new RectF32(x, btnY, btnW, btnH);
                }

                x += btnW + ButtonSpacing;
            }
        }

        // -----------------------------------------------------------------------
        // Toolbar dropdowns — single shared overlay (only one open at a time)
        // -----------------------------------------------------------------------

        /// <summary>Captured bounds of each enabled toolbar button this frame —
        /// used as the anchor when opening that button's dropdown.</summary>
        private readonly Dictionary<ToolbarAction, RectF32> _toolbarButtonBounds = new();

        /// <summary>Cycle order + dropdown order for the stretch-mode selector.
        /// Mirrors <see cref="ViewerActions.StretchLinkModes"/> 1:1 so the click
        /// handler can index back into the enum array.</summary>
        private static readonly ImmutableArray<string> StretchLinkModeLabels = BuildLabels(
            ViewerActions.StretchLinkModes, m => m.ToString());

        /// <summary>Channel-view selector — Composite/Red/Green/Blue. Only
        /// surfaced for 3+ channel images (gated by <see cref="IsToolbarButtonEnabled"/>).</summary>
        private static readonly ChannelView[] ChannelViewOrder =
            [ChannelView.Composite, ChannelView.Red, ChannelView.Green, ChannelView.Blue];

        private static readonly ImmutableArray<string> ChannelViewLabels = BuildLabels(
            ChannelViewOrder, v => v switch { ChannelView.Composite => "RGB", _ => v.ToString() });

        /// <summary>Debayer-algorithm selector — all algorithms always shown. The click handler
        /// indexes this array directly, so the order is independent of the enum's numeric values.
        /// MHC sits next to the other Bayer-to-RGB algorithms; for the GPU live (RawBayer) path it
        /// and VNG/AHD all resolve to the shader's MHC demosaic (see <see cref="GpuDebayerMode"/>).</summary>
        private static readonly DebayerAlgorithm[] DebayerAlgorithmOrder =
            [DebayerAlgorithm.None, DebayerAlgorithm.BilinearMono, DebayerAlgorithm.MHC, DebayerAlgorithm.VNG, DebayerAlgorithm.AHD];

        private static readonly ImmutableArray<string> DebayerLabels = BuildLabels(
            DebayerAlgorithmOrder, a => a.DisplayName);

        /// <summary>Stretch-parameter preset labels — 8 (Factor, ShadowsClipping) presets.</summary>
        private static readonly ImmutableArray<string> StretchParamsLabels = BuildLabels(
            StretchParameters.Presets, p => p.ToString());

        /// <summary>Curves-boost preset labels — 0/25/50/100/150 %.</summary>
        private static readonly ImmutableArray<string> CurvesBoostLabels = BuildLabels(
            ViewerState.CurvesBoostPresets, b => b > 0f ? $"{b:P0}" : "Off");

        /// <summary>HDR preset labels — "Off" + 4 (amount, knee) combos.</summary>
        private static readonly ImmutableArray<string> HdrLabels = BuildLabels(
            ViewerState.HdrPresets, p => p.Amount > 0f ? $"{p.Amount:F1} / {p.Knee:F2}" : "Off");

        /// <summary>Background-neutralization preset table — combines method × strength
        /// into one flat dropdown. <c>null</c> method = "Off" (disable). Mean has a
        /// strength variant to demonstrate the lerp plumbing; the other methods stay
        /// at full strength until a separate strength slider lands.</summary>
        private static readonly (string Label, BackgroundNeutralizationMethod? Method, float Strength)[] BackgroundNeutralizationPresets =
        [
            ("Off",          null,                                          0f),
            ("Mean",         BackgroundNeutralizationMethod.Mean,           1f),
            ("Mean (50%)",   BackgroundNeutralizationMethod.Mean,           0.5f),
            ("Green pivot",  BackgroundNeutralizationMethod.GreenPivot,     1f),
            ("Min pivot",    BackgroundNeutralizationMethod.MinPivot,       1f),
        ];

        private static readonly ImmutableArray<string> BackgroundNeutralizationLabels = BuildLabels(
            BackgroundNeutralizationPresets, p => p.Label);

        private static string ShortMethodLabel(BackgroundNeutralizationMethod m) => m switch
        {
            BackgroundNeutralizationMethod.GreenPivot => "Green",
            BackgroundNeutralizationMethod.MinPivot   => "Min",
            _                                         => "Mean",
        };

        private static ImmutableArray<string> BuildLabels<T>(System.Collections.Generic.IReadOnlyList<T> items, Func<T, string> selector)
        {
            var arr = new string[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                arr[i] = selector(items[i]);
            }
            return ImmutableArray.Create(arr);
        }

        /// <summary>
        /// Opens the appropriate dropdown overlay for <paramref name="action"/>
        /// anchored below its toolbar button. Returns <c>true</c> if a dropdown
        /// was opened (caller must not also dispatch the action's cycle).
        /// Right-click on the same buttons still falls through to
        /// <see cref="ViewerActions.HandleToolbarAction"/> for reverse-cycle.
        /// </summary>
        public bool OpenToolbarDropdown(ViewerState state, ToolbarAction action)
        {
            if (!_toolbarButtonBounds.TryGetValue(action, out var bounds))
            {
                return false;
            }

            switch (action)
            {
                case ToolbarAction.StretchLink:
                    OpenDropdown(state, bounds, StretchLinkModeLabels, (idx, _) =>
                    {
                        var modes = ViewerActions.StretchLinkModes;
                        if ((uint)idx < (uint)modes.Length)
                        {
                            state.StretchMode = modes[idx];
                            state.StatusMessage = $"Stretch: {state.StretchMode}";
                            state.NeedsRedraw = true;
                        }
                    }, Array.IndexOf(ViewerActions.StretchLinkModes, state.StretchMode));
                    return true;

                case ToolbarAction.Channel:
                    OpenDropdown(state, bounds, ChannelViewLabels, (idx, _) =>
                    {
                        if ((uint)idx < (uint)ChannelViewOrder.Length)
                        {
                            state.ChannelView = ChannelViewOrder[idx];
                            state.NeedsTextureUpdate = true;
                            state.StatusMessage = $"Channel: {state.ChannelView}";
                        }
                    }, Array.IndexOf(ChannelViewOrder, state.ChannelView));
                    return true;

                case ToolbarAction.Debayer:
                    OpenDropdown(state, bounds, DebayerLabels, (idx, _) =>
                    {
                        if ((uint)idx < (uint)DebayerAlgorithmOrder.Length)
                        {
                            state.DebayerAlgorithm = DebayerAlgorithmOrder[idx];
                            // RawBayer (SER / raw Bayer FITS) re-derives the GPU demosaic mode in
                            // UploadDocumentTextures, so the bilinear<->MHC switch is live; a CPU-debayered
                            // colour FITS is unaffected (it was demosaiced at load).
                            state.NeedsTextureUpdate = true;
                            state.StatusMessage = $"Debayer: {state.DebayerAlgorithm.DisplayName}";
                        }
                    }, Array.IndexOf(DebayerAlgorithmOrder, state.DebayerAlgorithm));
                    return true;

                case ToolbarAction.StretchParams:
                    OpenDropdown(state, bounds, StretchParamsLabels, (idx, _) =>
                    {
                        var presets = StretchParameters.Presets;
                        if ((uint)idx < (uint)presets.Length)
                        {
                            state.StretchPresetIndex = idx;
                            state.StretchParameters = presets[idx];
                            state.NeedsRedraw = true;
                            state.StatusMessage = $"Stretch: {state.StretchParameters}";
                        }
                    }, state.StretchPresetIndex);
                    return true;

                case ToolbarAction.CurvesBoost:
                    OpenDropdown(state, bounds, CurvesBoostLabels, (idx, _) =>
                    {
                        var presets = ViewerState.CurvesBoostPresets;
                        if ((uint)idx < (uint)presets.Length)
                        {
                            state.CurvesBoostIndex = idx;
                            state.CurvesBoost = presets[idx];
                            state.NeedsRedraw = true;
                            state.StatusMessage = state.CurvesBoost > 0f ? $"Curves Boost: {state.CurvesBoost:P0}" : "Curves Boost: Off";
                        }
                    }, state.CurvesBoostIndex);
                    return true;

                case ToolbarAction.Hdr:
                    OpenDropdown(state, bounds, HdrLabels, (idx, _) =>
                    {
                        var presets = ViewerState.HdrPresets;
                        if ((uint)idx < (uint)presets.Length)
                        {
                            state.HdrPresetIndex = idx;
                            state.HdrAmount = presets[idx].Amount;
                            state.HdrKnee = presets[idx].Knee;
                            state.NeedsRedraw = true;
                            state.StatusMessage = presets[idx].Amount > 0f
                                ? $"HDR: {presets[idx].Amount:F1} (knee {presets[idx].Knee:F2})"
                                : "HDR: Off";
                        }
                    }, state.HdrPresetIndex);
                    return true;

                case ToolbarAction.BackgroundNeutralize:
                    OpenDropdown(state, bounds, BackgroundNeutralizationLabels, (idx, _) =>
                    {
                        var presets = BackgroundNeutralizationPresets;
                        if ((uint)idx >= (uint)presets.Length)
                        {
                            return;
                        }
                        var (label, method, strength) = presets[idx];
                        state.BackgroundNeutralizationStrength = strength;
                        if (method is { } m)
                        {
                            state.BackgroundNeutralizationMethod = m;
                            // Compute (or hit per-method cache) and pin gain onto document.
                            // User picked a method explicitly, so the toolbar reflects that
                            // even if this image's gain happens to land near identity.
                            var gain = _document?.ComputeBackgroundNeutralization(m);
                            state.BackgroundNeutralizationEnabled = true;
                            state.StatusMessage = gain is { } g
                                ? $"NeutBg: {label}  R={g.R:F2} G={g.G:F2} B={g.B:F2}"
                                : $"NeutBg: {label} (no background data)";
                        }
                        else
                        {
                            // "Off" entry — drop the document gain so the uniform reverts to identity
                            if (_document is not null)
                            {
                                _document.BackgroundNeutralization = null;
                            }
                            state.BackgroundNeutralizationEnabled = false;
                            state.StatusMessage = "NeutBg: Off";
                        }
                        state.NeedsRedraw = true;
                    });
                    return true;

                default:
                    return false;
            }
        }

        private void OpenDropdown(ViewerState state, RectF32 bounds, ImmutableArray<string> labels, Action<int, string> onSelect, int selectedIndex = -1)
        {
            // Width = max(button width, widest label + horizontal padding).
            // RenderDropdownMenu draws each label with 0.5*fontSize padding per
            // side, so budget a full fontSize of slack to avoid edge clipping.
            var width = bounds.Width;
            var fontSize = ToolbarFontSize;
            foreach (var label in labels)
            {
                var labelWidth = MeasureText(label, fontSize) + fontSize;
                if (labelWidth > width)
                {
                    width = labelWidth;
                }
            }
            state.ToolbarDropdown.Open(
                bounds.X,
                bounds.Y + bounds.Height,
                width,
                labels,
                onSelect);
            // Mark the current selection so the menu shows the active item on open
            // (RenderDropdownMenu highlights HighlightIndex; Open resets it to -1).
            state.ToolbarDropdown.HighlightIndex = selectedIndex;
            state.NeedsRedraw = true;
        }

        private bool IsToolbarButtonEnabled(ToolbarAction action, AstroImageDocument? document) => action switch
        {
            // Gate on the active source's sensor type, not on AstroImageDocument -- a SER is a
            // SerPreviewSource (document == null) but is a raw RGGB Bayer source the GPU debayers,
            // so the demosaic selector must stay enabled for it too.
            ToolbarAction.Debayer => _source?.SensorType is SensorType.RGGB,
            ToolbarAction.Channel => document is not null && document.UnstretchedImage.ChannelCount > 1,
            ToolbarAction.CurvesBoost => document?.Stars is { Count: > 0 },
            ToolbarAction.Hdr => document is not null,
            ToolbarAction.StretchToggle => document is not null,
            ToolbarAction.StretchLink or ToolbarAction.StretchParams => document is not null,
            ToolbarAction.Grid => document?.Wcs is { HasCDMatrix: true },
            ToolbarAction.Overlays => document?.Wcs is { HasCDMatrix: true } && CelestialObjectDB?.IsValueCreated == true,
            ToolbarAction.Stars => document?.Stars is { Count: > 0 },
            ToolbarAction.ColorCalibrate => document?.Stars is { Count: >= 5 }
                && document.Stars.StarMask is not null
                && (document.UnstretchedImage.ChannelCount >= 3
                    || document.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB),
            ToolbarAction.BackgroundNeutralize => document?.PerChannelBackground is { Length: >= 3 }
                && (document.UnstretchedImage.ChannelCount >= 3
                    || document.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB),
            ToolbarAction.SpccCalibrate => document?.Stars is { Count: >= 3 }
                && document.IsPlateSolved
                && (document.UnstretchedImage.ChannelCount >= 3
                    || document.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB),
            ToolbarAction.PlateSolve => document is not null && !document.IsPlateSolved,
            ToolbarAction.ZoomFit or ToolbarAction.ZoomActual => document is not null,
            _ => true,
        };

        private bool IsToolbarButtonActive(ToolbarAction action, AstroImageDocument? document, ViewerState state)
        {
            return action switch
            {
                ToolbarAction.StretchToggle or ToolbarAction.StretchLink or ToolbarAction.StretchParams
                    => state.StretchMode is not StretchMode.None,
                // Highlight whenever a Bayer source is loaded and a demosaic is selected -- the GPU
                // applies state.DebayerAlgorithm live (re-derived in UploadDocumentTextures), so it's
                // never stale against an immutable document.DebayerAlgorithm. Works for SER + Bayer FITS.
                ToolbarAction.Debayer => _source?.SensorType is SensorType.RGGB
                    && state.DebayerAlgorithm is not DebayerAlgorithm.None,
                ToolbarAction.CurvesBoost => state.CurvesBoost > 0f,
                ToolbarAction.Hdr => state.HdrAmount > 0f,
                ToolbarAction.Grid => state.ShowGrid,
                ToolbarAction.Overlays => state.ShowOverlays,
                ToolbarAction.Stars => state.ShowStarOverlay,
                ToolbarAction.ColorCalibrate => state.ColorCalibrationEnabled,
                ToolbarAction.BackgroundNeutralize => state.BackgroundNeutralizationEnabled,
                ToolbarAction.SpccCalibrate => state.ColorCalibrationEnabled,
                ToolbarAction.ZoomFit => state.ZoomToFit,
                ToolbarAction.ZoomActual => !state.ZoomToFit && MathF.Abs(state.Zoom - 1f) < 0.001f,
                _ => false,
            };
        }

        private string GetToolbarButtonLabel(string baseLabel, ToolbarAction action, AstroImageDocument? document, ViewerState state)
        {
            return action switch
            {
                ToolbarAction.StretchToggle => "STF",
                ToolbarAction.StretchLink => state.StretchMode switch
                {
                    StretchMode.Linked => "Linked",
                    StretchMode.Luma => "Luma",
                    _ => "Unlinked"
                },
                ToolbarAction.StretchParams => $"{state.StretchParameters}",
                ToolbarAction.Channel => $"Channel: {(state.ChannelView is ChannelView.Composite ? "RGB" : state.ChannelView)}",
                ToolbarAction.Debayer => $"Debayer: {state.DebayerAlgorithm.DisplayName}",
                ToolbarAction.CurvesBoost => $"Boost: {state.CurvesBoost:P0}",
                ToolbarAction.Hdr => state.HdrAmount > 0f ? $"HDR: {state.HdrAmount:F1}" : "HDR",
                ToolbarAction.ZoomFit => "Fit",
                ToolbarAction.ZoomActual => "1:1",
                ToolbarAction.Grid => "Grid",
                ToolbarAction.Overlays when CelestialObjectDB is { IsValueCreated: false } => "Objects...",
                ToolbarAction.Overlays => "Objects",
                ToolbarAction.Stars when document?.Stars is null => "Stars...",
                ToolbarAction.Stars when document?.Stars is { Count: > 0 } s => $"Stars: {s.Count}",
                ToolbarAction.Stars => "Stars: 0",
                ToolbarAction.BackgroundNeutralize when state.BackgroundNeutralizationEnabled =>
                    state.BackgroundNeutralizationStrength >= 0.9999f
                        ? $"NeutBg: {ShortMethodLabel(state.BackgroundNeutralizationMethod)}"
                        : $"NeutBg: {ShortMethodLabel(state.BackgroundNeutralizationMethod)} {state.BackgroundNeutralizationStrength:P0}",
                ToolbarAction.SpccCalibrate when state.ColorCalibrationEnabled => $"SPCC: {document?.ColorCalibration?.R:F2}/{document?.ColorCalibration?.B:F2}",
                ToolbarAction.PlateSolve when state.IsPlateSolving => "Solving...",
                ToolbarAction.PlateSolve when document?.IsPlateSolved == true => "Solved",
                _ => baseLabel,
            };
        }

        /// <summary>
        /// Hit-tests the toolbar using actual rendered button widths for the current state.
        /// </summary>
        public ToolbarAction? HitTestToolbar(float screenX, float screenY, AstroImageDocument? document, ViewerState state)
        {
            if (screenY < ButtonSpacing || screenY >= ToolbarHeight - ButtonSpacing || _fontPath is null)
            {
                return null;
            }

            var x = PanelPadding;
            var prevGroup = -1;

            for (var i = 0; i < ToolbarButtons.Length; i++)
            {
                var (label, action, group) = ToolbarButtons[i];

                if (prevGroup >= 0 && group != prevGroup)
                {
                    x += ButtonGroupSpacing;
                }
                prevGroup = group;

                var displayLabel = GetToolbarButtonLabel(label, action, document, state);
                var textWidth = MeasureText(displayLabel, ToolbarFontSize);
                var btnW = textWidth + ButtonPaddingH * 2;

                if (screenX >= x && screenX < x + btnW)
                {
                    return IsToolbarButtonEnabled(action, document) ? action : null;
                }
                x += btnW + ButtonSpacing;
            }

            return null;
        }

        // -----------------------------------------------------------------------
        // File list sidebar
        // -----------------------------------------------------------------------

        private void RenderFileList(ViewerState state)
        {
            var listTop = ToolbarHeight;
            var listHeight = Height - ToolbarHeight - StatusBarHeight;

            FillRect(0, listTop, FileListWidth, listHeight, ViewerTheme.FileListBg);

            if (_fontPath is null)
            {
                return;
            }

            var y = (float)listTop + PanelPadding;
            DrawText("Files", PanelPadding, y, FontSize, ViewerTheme.Palette.HeaderText);
            y += FontSize + 4f;

            FillRect(PanelPadding, y, FileListWidth - PanelPadding * 2, 1, ViewerTheme.Palette.Separator);
            y += 3f;

            var itemHeight = FontSize + 4f;
            var visibleCount = (int)((listHeight - (y - listTop)) / itemHeight);
            var mouseX = state.MouseScreenPosition.X;
            var mouseY = state.MouseScreenPosition.Y;

            for (var i = 0; i < visibleCount && i + state.FileListScrollOffset < state.ImageFileNames.Count; i++)
            {
                var fileIndex = i + state.FileListScrollOffset;
                var fileName = state.ImageFileNames[fileIndex];
                var itemY = y + i * itemHeight;

                var isSelected = fileIndex == state.SelectedFileIndex;
                // Suppress hover highlight while a dropdown overlay is open: the pointer is captured
                // by the dropdown, so the list underneath must not react to it. Selection (the loaded
                // file) is NOT gated -- it should stay highlighted regardless.
                var isHovered = !state.ToolbarDropdown.IsOpen
                    && mouseX >= 0 && mouseX < FileListWidth
                    && mouseY >= itemY && mouseY < itemY + itemHeight;

                if (isSelected)
                {
                    FillRect(2, itemY, FileListWidth - 4, itemHeight, ViewerTheme.Palette.Selection);
                }
                else if (isHovered)
                {
                    FillRect(2, itemY, FileListWidth - 4, itemHeight, FileListHoverBg);
                }

                var maxChars = (int)((FileListWidth - PanelPadding * 2) / (FontSize * 0.6f));
                var displayName = fileName.Length > maxChars ? fileName[..(maxChars - 2)] + ".." : fileName;

                DrawText(displayName, PanelPadding, itemY + 2f, FontSize,
                    isSelected ? FileListItemTextSelected : FileListItemText);

                RegisterClickable(0, itemY, FileListWidth, itemHeight, new HitResult.ListItemHit("FileList", fileIndex));
            }

            if (state.ImageFileNames.Count > visibleCount)
            {
                var scrollFraction = (float)state.FileListScrollOffset / Math.Max(1, state.ImageFileNames.Count - visibleCount);
                var scrollBarH = Math.Max(20f, listHeight * visibleCount / state.ImageFileNames.Count);
                var scrollBarY = listTop + scrollFraction * (listHeight - scrollBarH);
                FillRect(FileListWidth - 4, scrollBarY, 3, scrollBarH, ScrollBarColor);
            }

            // The resize divider between the file list and the content area is now the Split's
            // draw==hit divider node, painted once in Render() from the single layout pass -- no
            // more hand-rolled FillRect handle + widened RegisterClickable straddling the boundary.
        }

        /// <summary>
        /// Hit-tests the file list sidebar and returns the file index, or -1.
        /// </summary>
        public int HitTestFileList(float screenX, float screenY, ViewerState state)
        {
            if (!state.ShowFileList || screenX < 0 || screenX >= FileListWidth)
            {
                return -1;
            }

            var listTop = ToolbarHeight;
            var headerOffset = PanelPadding + FontSize + 4f + 3f;
            var itemHeight = FontSize + 4f;
            var relY = screenY - listTop - headerOffset;

            if (relY < 0)
            {
                return -1;
            }

            var itemIndex = (int)(relY / itemHeight) + state.FileListScrollOffset;
            if (itemIndex >= 0 && itemIndex < state.ImageFileNames.Count)
            {
                return itemIndex;
            }

            return -1;
        }

        // -----------------------------------------------------------------------
        // WCS Grid labels
        // -----------------------------------------------------------------------

        /// <summary>
        /// Grid spacing options in arcseconds, from fine to coarse.
        /// The renderer picks the smallest spacing that gives at least ~3 grid lines.
        /// </summary>
        private static readonly double[] GridSpacingsArcsec =
        [
            1, 2, 5, 10, 15, 30,                           // sub-arcminute
            60, 120, 300, 600, 900, 1800,                   // arcminutes
            3600, 7200, 18000, 36000, 90000, 180000,        // degrees
        ];

        /// <summary>
        /// Renders RA/Dec labels at grid line intersections with image edges.
        /// The grid lines themselves are drawn by the GPU shader.
        /// </summary>
        private void RenderGridLabels(ViewerState state, WCS wcs)
        {
            if (_fontPath is null || ImageWidth <= 0 || ImageHeight <= 0)
            {
                return;
            }

            // All geometry from the single layout pass (arranged image-pane rect + image placement).
            var area = _layout.ImageArea;
            var scale = _placement.Scale;
            var imgOffsetX = _placement.OffsetX;
            var imgOffsetY = _placement.OffsetY;

            // Visible image pixel bounds (1-based FITS coordinates), clamped to the image-area pane.
            var visLeft = Math.Max(1.0, (area.X - imgOffsetX) / scale + 1);
            var visRight = Math.Min((double)ImageWidth, (area.X + area.Width - imgOffsetX) / scale + 1);
            var visTop = Math.Max(1.0, (area.Y - imgOffsetY) / scale + 1);
            var visBottom = Math.Min((double)ImageHeight, (area.Y + area.Height - imgOffsetY) / scale + 1);

            if (visLeft >= visRight || visTop >= visBottom)
            {
                return;
            }

            // Get sky coordinates at corners to determine RA/Dec range
            var corners = new (double RA, double Dec)?[]
            {
                wcs.PixelToSky(visLeft, visTop),
                wcs.PixelToSky(visRight, visTop),
                wcs.PixelToSky(visLeft, visBottom),
                wcs.PixelToSky(visRight, visBottom),
                wcs.PixelToSky((visLeft + visRight) / 2, visTop),
                wcs.PixelToSky((visLeft + visRight) / 2, visBottom),
                wcs.PixelToSky(visLeft, (visTop + visBottom) / 2),
                wcs.PixelToSky(visRight, (visTop + visBottom) / 2),
            };

            double minRA = double.MaxValue, maxRA = double.MinValue;
            double minDec = double.MaxValue, maxDec = double.MinValue;
            foreach (var c in corners)
            {
                if (c is not { } sky)
                {
                    continue;
                }
                minRA = Math.Min(minRA, sky.RA);
                maxRA = Math.Max(maxRA, sky.RA);
                minDec = Math.Min(minDec, sky.Dec);
                maxDec = Math.Max(maxDec, sky.Dec);
            }

            if (minRA > maxRA || minDec > maxDec)
            {
                return;
            }

            // Handle RA wraparound (if range spans 0h/24h)
            if (maxRA - minRA > 12.0)
            {
                double wrapMin = double.MaxValue, wrapMax = double.MinValue;
                foreach (var c in corners)
                {
                    if (c is not { } sky)
                    {
                        continue;
                    }
                    var ra = sky.RA < 12.0 ? sky.RA + 24.0 : sky.RA;
                    wrapMin = Math.Min(wrapMin, ra);
                    wrapMax = Math.Max(wrapMax, ra);
                }
                minRA = wrapMin;
                maxRA = wrapMax;
            }

            // Compute grid spacing in sky units
            var pixelScaleArcsec = wcs.PixelScaleArcsec;
            var viewImagePixels = MathF.Min(area.Width, area.Height) / scale;
            var viewArcsec = viewImagePixels * pixelScaleArcsec;
            var spacingArcsec = GridSpacingsArcsec[^1];
            foreach (var candidate in GridSpacingsArcsec)
            {
                if (candidate >= viewArcsec / 8.0)
                {
                    spacingArcsec = candidate;
                    break;
                }
            }

            var spacingDecDeg = spacingArcsec / 3600.0;
            var spacingRAhours = spacingArcsec / 3600.0 / 15.0;

            var labelSize = FontSize * 0.85f;
            var labelPad = 3f;

            var raOnHorizEdges = Math.Abs(wcs.CD1_1) > Math.Abs(wcs.CD1_2);

            var cornerMargin = labelSize * 4f;

            var numSamples = 300;

            var edges = new (double X0, double Y0, double X1, double Y1, bool IsHorizontal)[]
            {
                (visLeft, visTop, visRight, visTop, true),       // top edge
                (visLeft, visBottom, visRight, visBottom, true),  // bottom edge
                (visLeft, visTop, visLeft, visBottom, false),     // left edge
                (visRight, visTop, visRight, visBottom, false),   // right edge
            };

            foreach (var (x0, y0, x1, y1, isHoriz) in edges)
            {
                var showRA = isHoriz == raOnHorizEdges;
                var showDec = isHoriz != raOnHorizEdges;
                var isFirstEdge = isHoriz ? (y0 <= visTop + 1) : (x0 <= visLeft + 1);

                var edgeStartX = imgOffsetX + (float)(x0 - 1) * scale;
                var edgeStartY = imgOffsetY + (float)(y0 - 1) * scale;
                var edgeEndX = imgOffsetX + (float)(x1 - 1) * scale;
                var edgeEndY = imgOffsetY + (float)(y1 - 1) * scale;

                double prevRA = double.NaN, prevDec = double.NaN;
                float prevScreenX = 0, prevScreenY = 0;

                for (int i = 0; i <= numSamples; i++)
                {
                    var t = (double)i / numSamples;
                    var px = x0 + (x1 - x0) * t;
                    var py = y0 + (y1 - y0) * t;
                    var sky = wcs.PixelToSky(px, py);
                    if (sky is not { } s)
                    {
                        prevRA = double.NaN;
                        prevDec = double.NaN;
                        continue;
                    }

                    var screenX = imgOffsetX + (float)(px - 1) * scale;
                    var screenY = imgOffsetY + (float)(py - 1) * scale;

                    if (!double.IsNaN(prevRA))
                    {
                        // RA crossings (skip wraparound jumps)
                        if (showRA && Math.Abs(s.RA - prevRA) < 12.0)
                        {
                            var raLo = Math.Min(prevRA, s.RA);
                            var raHi = Math.Max(prevRA, s.RA);
                            var firstG = (int)Math.Ceiling(raLo / spacingRAhours);
                            var lastG = (int)Math.Floor(raHi / spacingRAhours);
                            for (var g = firstG; g <= lastG; g++)
                            {
                                var gridRA = g * spacingRAhours;
                                var frac = (gridRA - prevRA) / (s.RA - prevRA);
                                var lx = prevScreenX + (screenX - prevScreenX) * (float)frac;
                                var ly = prevScreenY + (screenY - prevScreenY) * (float)frac;

                                var distToStart = MathF.Abs(isHoriz ? lx - edgeStartX : ly - edgeStartY);
                                var distToEnd = MathF.Abs(isHoriz ? lx - edgeEndX : ly - edgeEndY);
                                if (distToStart < cornerMargin || distToEnd < cornerMargin)
                                {
                                    continue;
                                }

                                var normalizedRA = gridRA % 24.0;
                                if (normalizedRA < 0) normalizedRA += 24.0;
                                var raLabel = FormatRALabel(normalizedRA, spacingArcsec);
                                PlaceEdgeLabel(raLabel, lx, ly, labelSize, labelPad, isHoriz, isFirstEdge);
                            }
                        }

                        // Dec crossings
                        if (showDec)
                        {
                            var decLo = Math.Min(prevDec, s.Dec);
                            var decHi = Math.Max(prevDec, s.Dec);
                            var firstG = (int)Math.Ceiling(decLo / spacingDecDeg);
                            var lastG = (int)Math.Floor(decHi / spacingDecDeg);
                            for (var g = firstG; g <= lastG; g++)
                            {
                                var gridDec = g * spacingDecDeg;
                                var frac = (gridDec - prevDec) / (s.Dec - prevDec);
                                var lx = prevScreenX + (screenX - prevScreenX) * (float)frac;
                                var ly = prevScreenY + (screenY - prevScreenY) * (float)frac;

                                var distToStart = MathF.Abs(isHoriz ? lx - edgeStartX : ly - edgeStartY);
                                var distToEnd = MathF.Abs(isHoriz ? lx - edgeEndX : ly - edgeEndY);
                                if (distToStart < cornerMargin || distToEnd < cornerMargin)
                                {
                                    continue;
                                }

                                var decLabel = FormatDecLabel(gridDec, spacingArcsec);
                                PlaceEdgeLabel(decLabel, lx, ly, labelSize, labelPad, isHoriz, isFirstEdge);
                            }
                        }
                    }

                    prevRA = s.RA;
                    prevDec = s.Dec;
                    prevScreenX = screenX;
                    prevScreenY = screenY;
                }
            }
        }

        private void PlaceEdgeLabel(string label, float lx, float ly, float labelSize, float labelPad,
            bool isHoriz, bool isFirstEdge)
        {
            var lineOffset = labelPad + 2f;
            if (isHoriz)
            {
                var labelX = isFirstEdge ? lx + lineOffset : lx - MeasureText(label, labelSize) - lineOffset;
                var labelY = isFirstEdge ? ly + labelPad : ly - labelSize - labelPad;
                DrawText(label, labelX, labelY, labelSize, GridLabelColor);
            }
            else
            {
                var labelX = isFirstEdge ? lx + labelPad : lx - MeasureText(label, labelSize) - labelPad;
                var labelY = isFirstEdge ? ly + lineOffset : ly - labelSize - lineOffset;
                DrawText(label, labelX, labelY, labelSize, GridLabelColor);
            }
        }

        private static string FormatRALabel(double raHours, double spacingArcsec)
        {
            var h = (int)Math.Floor(raHours);
            var m = (raHours - h) * 60.0;
            var mi = (int)Math.Floor(m);
            var s = (m - mi) * 60.0;

            if (spacingArcsec >= 3600)
            {
                return $"{h}h";
            }
            if (spacingArcsec >= 60)
            {
                return $"{h}h{mi:D2}m";
            }
            return $"{h}h{mi:D2}m{s:00.0}s";
        }

        private static string FormatDecLabel(double decDeg, double spacingArcsec)
        {
            var sign = decDeg >= 0 ? "+" : "-";
            var abs = Math.Abs(decDeg);
            var d = (int)Math.Floor(abs);
            var m = (abs - d) * 60.0;
            var mi = (int)Math.Floor(m);
            var s = (m - mi) * 60.0;

            if (spacingArcsec >= 3600)
            {
                return $"{sign}{d}\u00b0";
            }
            if (spacingArcsec >= 60)
            {
                return $"{sign}{d}\u00b0{mi:D2}'";
            }
            return $"{sign}{d}\u00b0{mi:D2}'{s:00.0}\"";
        }

        // -----------------------------------------------------------------------
        // Star Overlay
        // -----------------------------------------------------------------------

        private void RenderStarOverlay(ViewerState state, StarList stars)
        {
            // Geometry from the single layout pass -- consistent with the rendered image by construction.
            var area = _layout.ImageArea;
            var offsetX = _placement.OffsetX;
            var offsetY = _placement.OffsetY;

            var clipLeft = area.X;
            var clipTop = area.Y;
            var clipRight = area.X + area.Width;
            var clipBottom = area.Y + area.Height;

            foreach (var star in stars)
            {
                var cx = offsetX + (star.XCentroid + 0.5f) * state.Zoom;
                var cy = offsetY + (star.YCentroid + 0.5f) * state.Zoom;
                var radius = MathF.Max(star.HFD * 0.5f * state.Zoom, 6f);

                if (cx + radius < clipLeft || cx - radius > clipRight ||
                    cy + radius < clipTop || cy - radius > clipBottom)
                {
                    continue;
                }

                var alpha = MathF.Min(1.0f, 0.3f + state.Zoom * 0.7f);
                DrawEllipseOverlay(cx, cy, radius, radius, 0f,
                    new RGBAColor32(0, (byte)(0.8f * 255), (byte)(0.2f * 255), (byte)(alpha * 255)), 1.5f);
            }
        }

        // -----------------------------------------------------------------------
        // Object Overlays
        // -----------------------------------------------------------------------

        private void RenderOverlays(ViewerState state, WCS wcs, ICelestialObjectDB db)
        {
            if (_fontPath is null || ImageWidth <= 0 || ImageHeight <= 0)
            {
                return;
            }

            // Image-area pane rect from the single layout pass.
            var area = _layout.ImageArea;

            var layout = new ViewportLayout(
                WindowWidth: Width,
                WindowHeight: Height,
                ImageWidth: ImageWidth,
                ImageHeight: ImageHeight,
                Zoom: state.Zoom,
                PanOffset: state.PanOffset,
                AreaLeft: area.X,
                AreaTop: area.Y,
                AreaWidth: area.Width,
                AreaHeight: area.Height,
                DpiScale: DpiScale
            );

            var items = OverlayEngine.ComputeOverlays(layout, wcs, db, MeasureText, BaseFontSize);
            if (items.Count == 0)
            {
                return;
            }

            var labelSize = FontSize * 0.85f;
            var labelPad = 4f;

            // Draw markers first (brightest-first order is preserved by the engine)
            foreach (var item in items)
            {
                var (r, g, b) = item.Color;
                var marker = item.Marker;
                switch (marker.Kind)
                {
                    case OverlayMarkerKind.Ellipse:
                        DrawEllipseOverlay(item.ScreenX, item.ScreenY,
                            marker.SemiMajorPx, marker.SemiMinorPx, marker.AngleRad,
                            FloatToColor(r, g, b, 1.0f), 1.5f);
                        break;
                    case OverlayMarkerKind.Cross:
                        DrawCrossOverlay(item.ScreenX, item.ScreenY, marker.ArmPx,
                            FloatToColor(r, g, b, 1.0f));
                        break;
                    case OverlayMarkerKind.Circle:
                        DrawEllipseOverlay(item.ScreenX, item.ScreenY,
                            marker.RadiusPx, marker.RadiusPx, 0f,
                            FloatToColor(r, g, b, 0.9f), 1.5f);
                        break;
                }
            }

            // Label placement + collision avoidance is shared with the sky map object
            // overlay (see OverlayEngine.PlaceLabels).
            var lineH = labelSize * 1.2f;
            OverlayEngine.PlaceLabels(items, labelSize, labelPad, MeasureText,
                (item, lx, ly) =>
                {
                    var (r, g, b) = item.Color;
                    DrawOverlayLabelLines(item.LabelLines, lx, ly, lineH, labelSize, r, g, b);
                });
        }

        /// <summary>
        /// Render the caller-supplied <see cref="WcsAnnotation"/> through the active
        /// WCS using the renderer's existing primitives. Generic — knows nothing
        /// about polar alignment, plate-solve verification, etc.; just iterates the
        /// annotation list, projects each item via <see cref="WcsAnnotationLayer"/>,
        /// dispatches to <see cref="DrawCrossOverlay"/> or
        /// <see cref="DrawEllipseOverlay"/>.
        /// </summary>
        private void RenderWcsAnnotation(ViewerState state, WCS wcs)
        {
            if (ImageWidth <= 0 || ImageHeight <= 0) return;

            // Image-area pane rect from the single layout pass.
            var area = _layout.ImageArea;

            var layout = new ViewportLayout(
                WindowWidth: Width,
                WindowHeight: Height,
                ImageWidth: ImageWidth,
                ImageHeight: ImageHeight,
                Zoom: state.Zoom,
                PanOffset: state.PanOffset,
                AreaLeft: area.X,
                AreaTop: area.Y,
                AreaWidth: area.Width,
                AreaHeight: area.Height,
                DpiScale: DpiScale);

            var labelSize = FontSize * 0.85f;
            var labelPad = 4f;

            // Rings drawn first so marker glyphs draw on top of them.
            if (!Annotation.Rings.IsDefaultOrEmpty)
            {
                foreach (var ring in Annotation.Rings)
                {
                    if (WcsAnnotationLayer.ProjectRing(ring, wcs, layout) is not { } placement) continue;
                    if (placement.RadiusScreenPx < 1f) continue;
                    DrawEllipseOverlay(placement.ScreenX, placement.ScreenY,
                        placement.RadiusScreenPx, placement.RadiusScreenPx, 0f,
                        ring.Color, thickness: 1.5f);
                    if (!string.IsNullOrEmpty(ring.Label))
                    {
                        DrawText(ring.Label,
                            placement.ScreenX + placement.RadiusScreenPx + labelPad,
                            placement.ScreenY - labelSize * 0.5f,
                            labelSize,
                            ring.Color);
                    }
                }
            }

            if (!Annotation.Markers.IsDefaultOrEmpty)
            {
                foreach (var marker in Annotation.Markers)
                {
                    if (WcsAnnotationLayer.ProjectMarker(marker, wcs, layout) is not { } placement) continue;

                    switch (marker.Glyph)
                    {
                        case SkyMarkerGlyph.Cross:
                            DrawCrossOverlay(placement.ScreenX, placement.ScreenY, marker.SizePx, marker.Color);
                            break;
                        case SkyMarkerGlyph.Dot:
                            DrawEllipseOverlay(placement.ScreenX, placement.ScreenY,
                                marker.SizePx, marker.SizePx, 0f, marker.Color, thickness: 0f);
                            break;
                        case SkyMarkerGlyph.Circle:
                            DrawEllipseOverlay(placement.ScreenX, placement.ScreenY,
                                marker.SizePx, marker.SizePx, 0f, marker.Color, thickness: 1.5f);
                            break;
                        case SkyMarkerGlyph.CircledCross:
                            DrawEllipseOverlay(placement.ScreenX, placement.ScreenY,
                                marker.SizePx, marker.SizePx, 0f, marker.Color, thickness: 1.5f);
                            DrawCrossOverlay(placement.ScreenX, placement.ScreenY, marker.SizePx * 0.6f, marker.Color);
                            break;
                    }

                    if (!string.IsNullOrEmpty(marker.Label))
                    {
                        DrawText(marker.Label,
                            placement.ScreenX + marker.SizePx + labelPad,
                            placement.ScreenY - labelSize * 0.5f,
                            labelSize,
                            marker.Color);
                    }
                }
            }

            if (!Annotation.Arrows.IsDefaultOrEmpty)
            {
                foreach (var arrow in Annotation.Arrows)
                {
                    if (WcsAnnotationLayer.ProjectArrow(arrow, wcs, layout) is not { } placement) continue;

                    var dx = placement.EndScreenX - placement.StartScreenX;
                    var dy = placement.EndScreenY - placement.StartScreenY;
                    var len = MathF.Sqrt(dx * dx + dy * dy);
                    // Skip degenerate arrows (start and end project to ~same
                    // pixel) -- a single dot would carry no direction info.
                    if (len < 1f) continue;

                    DrawLineOverlay(placement.StartScreenX, placement.StartScreenY,
                        placement.EndScreenX, placement.EndScreenY,
                        arrow.Color, arrow.ThicknessPx);

                    // HeadSizePx <= 0 -> bare line segment, no arrowhead.
                    // Used by the polar-align cross meridians (4 radial line
                    // segments from refracted pole to outer ring).
                    if (arrow.HeadSizePx > 0f)
                    {
                        // Two-segment arrowhead: angle off the shaft direction at
                        // the head endpoint. 30deg legs match SharpCap's look.
                        var headLen = arrow.HeadSizePx;
                        var ux = dx / len;
                        var uy = dy / len;
                        const float headAngle = 0.5236f; // 30 degrees in radians
                        var ca = MathF.Cos(headAngle);
                        var sa = MathF.Sin(headAngle);
                        // Two unit vectors rotated +/-headAngle from the *reverse*
                        // shaft direction; scale by head length to produce the
                        // two head-leg endpoints.
                        var leg1X = placement.EndScreenX - headLen * (ca * ux - sa * uy);
                        var leg1Y = placement.EndScreenY - headLen * (sa * ux + ca * uy);
                        var leg2X = placement.EndScreenX - headLen * (ca * ux + sa * uy);
                        var leg2Y = placement.EndScreenY - headLen * (-sa * ux + ca * uy);
                        DrawLineOverlay(placement.EndScreenX, placement.EndScreenY, leg1X, leg1Y, arrow.Color, arrow.ThicknessPx);
                        DrawLineOverlay(placement.EndScreenX, placement.EndScreenY, leg2X, leg2Y, arrow.Color, arrow.ThicknessPx);
                    }

                    if (!string.IsNullOrEmpty(arrow.Label))
                    {
                        DrawText(arrow.Label,
                            placement.EndScreenX + labelPad,
                            placement.EndScreenY - labelSize * 0.5f,
                            labelSize,
                            arrow.Color);
                    }
                }
            }
        }

        private void DrawOverlayLabelLines(IReadOnlyList<string> lines, float x, float y, float lineH, float fontSize, float r, float g, float b)
        {
            for (int li = 0; li < lines.Count; li++)
            {
                // First line full intensity; continuation lines dimmed. Dim by scaling
                // the RGB toward black (the original behaviour) rather than via alpha.
                var dim = li == 0 ? 1.0f : 0.7f;
                DrawText(lines[li], x, y + li * lineH, fontSize, RGBAColor32.FromFloat(r * dim, g * dim, b * dim, 1f));
            }
        }

        private static RGBAColor32 FloatToColor(float r, float g, float b, float a)
            => RGBAColor32.FromFloat(r, g, b, a);

        // -----------------------------------------------------------------------
        // Histogram overlay
        // -----------------------------------------------------------------------

        private (float Left, float Top, float Width, float Height) GetHistogramRect(ViewerState state)
        {
            var histW = BaseHistogramWidth * DpiScale;
            var histH = BaseHistogramHeight * DpiScale;
            var margin = BaseHistogramMargin * DpiScale;
            // Right edge of the image-area pane (abuts the info panel or the window edge) from the layout pass.
            var area = _layout.ImageArea;
            var rightEdge = area.Width > 0 ? area.X + area.Width : (float)Width;
            return (rightEdge - histW - margin, ToolbarHeight + margin, histW, histH);
        }

        private (float X, float Y, float W, float H) GetHistogramLogButtonRect(ViewerState state)
        {
            var (histLeft, histTop, histW, _) = GetHistogramRect(state);
            var btnW = MeasureText("LOG", ToolbarFontSize) + ButtonPaddingH;
            var btnH = ToolbarFontSize + 4f * DpiScale;
            var btnX = histLeft + histW - btnW - 2f * DpiScale;
            var btnY = histTop + 2f * DpiScale;
            return (btnX, btnY, btnW, btnH);
        }

        /// <summary>
        /// Returns true if the given screen position hits the histogram LOG button.
        /// </summary>
        public bool HitTestHistogramLog(float screenX, float screenY, ViewerState state)
        {
            if (!state.ShowHistogram || GetHistogramDisplay() is not { ChannelCount: > 0 })
            {
                return false;
            }
            var (bx, by, bw, bh) = GetHistogramLogButtonRect(state);
            return screenX >= bx && screenX < bx + bw && screenY >= by && screenY < by + bh;
        }

        private void RenderHistogram(IPreviewSource source, ViewerState state)
        {
            if (GetHistogramDisplay() is not { ChannelCount: > 0 } histogramDisplay)
            {
                return;
            }

            var stretch = source.ComputeStretchUniforms(
                state.StretchMode, state.StretchParameters,
                bgNeutralizationStrength: state.BackgroundNeutralizationStrength,
                manualWhiteBalance: state.ManualWhiteBalance);

            var (histLeft, histTop, histW, histH) = GetHistogramRect(state);

            // Semi-transparent background
            FillRect(histLeft, histTop, histW, histH, ViewerTheme.HistogramBg);

            RenderHistogramQuad(stretch, histogramDisplay, state,
                histLeft, histTop, histLeft + histW, histTop + histH, Width, Height);

            // Draw LOG button in upper-right corner of histogram
            if (_fontPath is not null)
            {
                var (bx, by, bw, bh) = GetHistogramLogButtonRect(state);
                var mouseX = state.MouseScreenPosition.X;
                var mouseY = state.MouseScreenPosition.Y;
                var hovered = !state.ToolbarDropdown.IsOpen && mouseX >= bx && mouseX < bx + bw && mouseY >= by && mouseY < by + bh;

                if (state.HistogramLogScale)
                {
                    FillRect(bx, by, bw, bh, hovered ? HistogramLogOnHoverBg : HistogramLogOnBg);
                }
                else
                {
                    FillRect(bx, by, bw, bh, hovered ? HistogramLogOffHoverBg : HistogramLogOffBg);
                }

                var textY = by + (bh - ToolbarFontSize) / 2f;
                DrawText("LOG", bx + ButtonPaddingH / 2f, textY, ToolbarFontSize, ViewerTheme.Palette.BodyText);

                RegisterClickable(bx, by, bw, bh, new HitResult.ButtonHit("HistogramLog"),
                    _ => { state.HistogramLogScale = !state.HistogramLogScale; });
            }
        }

        // -----------------------------------------------------------------------
        // Info panel
        // -----------------------------------------------------------------------

        private void RenderInfoPanel(IPreviewSource source, ViewerState state)
        {
            if (_fontPath is null)
            {
                return;
            }

            // Metadata/statistics/cursor/stars are still-image (document) concerns; a SER source has no
            // document, so those sections are skipped and the panel shows just the (shared) white-balance
            // controls + the controls help -- filling the panel strip that the layout reserves regardless.
            var document = source as AstroImageDocument;

            // Info-panel rect from the single layout pass (docked right by the Split's content Dock).
            var panel = _layout.InfoPanel;
            FillRect(panel.X, panel.Y, panel.Width, panel.Height, ViewerTheme.InfoPanelBg);

            var y = panel.Y + PanelPadding;
            var x = panel.X + PanelPadding;

            var maxTextWidth = panel.Width - PanelPadding * 2;

            if (document is not null)
            {
                DrawTextLine(ref y, x, "-- Metadata --", ViewerTheme.Palette.HeaderText);
                foreach (var line in InfoPanelData.GetMetadataLines(document))
                {
                    DrawWrappedTextLine(ref y, x, line, maxTextWidth, ViewerTheme.Palette.BodyText);
                }

                y += FontSize;

                DrawTextLine(ref y, x, "-- Statistics --", ViewerTheme.Palette.HeaderText);
                foreach (var line in InfoPanelData.GetStatisticsLines(document))
                {
                    DrawTextLine(ref y, x, line, ViewerTheme.Palette.BodyText);
                }
            }

            if (state.CursorPixelInfo is not null)
            {
                y += FontSize;
                DrawTextLine(ref y, x, "-- Cursor --", ViewerTheme.Palette.HeaderText);
                foreach (var line in InfoPanelData.GetCursorLines(state))
                {
                    DrawTextLine(ref y, x, line, ViewerTheme.Palette.BodyText);
                }
            }

            // Manual white-balance sliders -- only meaningful for a colour source (3 channels, or a raw
            // Bayer mosaic the GPU debayers into colour). Captures per-channel track rects for the drag.
            var isColour = source.ChannelCount >= 3 || source.SensorType is SensorType.RGGB;
            if (isColour)
            {
                y += FontSize;
                RenderWhiteBalanceControls(state, ref y, x, maxTextWidth);
            }
            else
            {
                _wbTrackRects[0] = _wbTrackRects[1] = _wbTrackRects[2] = default;
            }

            // Wavelet-sharpen layer sliders -- only for the live stacked view (they re-sharpen the stacked
            // master; they have no effect on a raw frame). Sit right under the white-balance sliders.
            if (state.ShowStacked)
            {
                y += FontSize;
                RenderWaveletControls(state, ref y, x, maxTextWidth);
            }
            else
            {
                for (var i = 0; i < _waveletTrackRects.Length; i++)
                {
                    _waveletTrackRects[i] = default;
                }
            }

            // Controls help at bottom of panel
            ReadOnlySpan<string> controlLabels =
            [
                "-- Controls --",
                "T: Cycle stretch",
                "S: Toggle stars",
                "+/-: Stretch factor",
                "C: Cycle channel",
                "D: Cycle debayer",
                "W: Color calibrate (SPCC)",
                "N: Toggle neut. background",
                "H: Cycle HDR",
                "G: Toggle grid",
                "V/Shift+V: Histogram/Log",
                "P: Plate solve",
                "Wheel/Ctrl+Wheel: Zoom",
                "Ctrl++/-: Zoom in/out",
                "F/Ctrl+0: Zoom to fit",
                "R/Ctrl+1: Zoom 1:1",
                "Ctrl+2..9: Zoom 1:N",
                "I: Toggle info panel",
                "L: Toggle file list",
                "F11: Fullscreen",
                "Esc: Quit",
            ];

            var lineHeight = FontSize + 2f;
            var availableLines = (int)((Height - StatusBarHeight - PanelPadding - y - lineHeight) / lineHeight);
            if (availableLines >= 2)
            {
                var clipped = availableLines < controlLabels.Length;
                var linesToDraw = clipped ? availableLines - 1 : controlLabels.Length;
                var totalLines = clipped ? linesToDraw + 1 : linesToDraw;
                y = Height - StatusBarHeight - lineHeight * totalLines - PanelPadding;
                for (var i = 0; i < linesToDraw; i++)
                {
                    var isHeader = i == 0;
                    DrawTextLine(ref y, x, controlLabels[i],
                        isHeader ? ViewerTheme.Palette.HeaderText : ViewerTheme.Palette.DimText);
                }
                if (clipped)
                {
                    DrawTextLine(ref y, x, "...", ViewerTheme.Palette.DimText);
                }
            }
        }

        // -----------------------------------------------------------------------
        // Manual white-balance sliders (info panel; shared across FITS / TIFF / SER)
        //
        // Three log-mapped sliders (R/G/B) over [WbMin, WbMax] with neutral 1.0 at the track midpoint,
        // plus a Reset. Drag is press + move + release (mirrors the transport scrub): a press begins a
        // drag on the hit channel, mouse-move maps cursor-X -> multiplier, release ends it. A WB change
        // only re-derives the stretch uniforms from cached stats (no pixel pass), so it sets NeedsRedraw,
        // never NeedsTextureUpdate.
        // -----------------------------------------------------------------------

        private void RenderWhiteBalanceControls(ViewerState state, ref float y, float x, float panelWidth)
        {
            DrawTextLine(ref y, x, "-- White Balance --", ViewerTheme.Palette.HeaderText);

            var wb = state.ManualWhiteBalance;
            ReadOnlySpan<(string Label, float Value, RGBAColor32 Fill)> rows =
            [
                ("R", wb.R, RGBAColor32.FromFloat(0.85f, 0.32f, 0.32f, 1f)),
                ("G", wb.G, RGBAColor32.FromFloat(0.34f, 0.74f, 0.38f, 1f)),
                ("B", wb.B, RGBAColor32.FromFloat(0.38f, 0.56f, 0.92f, 1f)),
            ];

            var gap = 6f * DpiScale;
            var rowH = FontSize + gap;
            var labelW = MeasureText("R", FontSize) + gap;
            var valueW = MeasureText("0.00", FontSize) + gap;
            var barH = MathF.Max(4f, 6f * DpiScale);
            var handleW = MathF.Max(4f, 6f * DpiScale);

            for (var ch = 0; ch < 3; ch++)
            {
                var (label, value, fill) = rows[ch];
                var rowY = y;
                DrawText(label, x, rowY, FontSize, ViewerTheme.Palette.BodyText);

                var trackX = x + labelW;
                var trackRight = x + panelWidth - valueW;
                var trackW = MathF.Max(0f, trackRight - trackX);
                if (trackW > 0f)
                {
                    var barY = rowY + (FontSize - barH) / 2f;
                    FillRect(trackX, barY, trackW, barH, TransportTrackBg);
                    var frac = WbValueToFrac(value);
                    FillRect(trackX, barY, trackW * frac, barH, fill);

                    // Handle marker; guard the clamp's upper bound for a sliver-thin track (see the
                    // transport scrub for the same trackW < handleW degenerate-clamp note).
                    var handleMax = MathF.Max(trackX, trackX + trackW - handleW);
                    var handleX = Math.Clamp(trackX + trackW * frac - handleW / 2f, trackX, handleMax);
                    FillRect(handleX, rowY, handleW, FontSize, TransportHandle);

                    // Generous full-row hit band; its X/Width drive the cursor-X -> multiplier mapping.
                    var hitY = rowY - gap / 2f;
                    var hitH = FontSize + gap;
                    _wbTrackRects[ch] = new RectF32(trackX, hitY, trackW, hitH);
                    RegisterClickable(trackX, hitY, trackW, hitH, new WhiteBalanceSliderHit(ch));
                }
                else
                {
                    _wbTrackRects[ch] = default;
                }

                DrawText(value.ToString("0.00"), trackRight, rowY, FontSize, ViewerTheme.Palette.DimText);
                y = rowY + rowH;
            }

            // Auto + Reset buttons row: both self-contained via OnClick (both mouse-down paths run
            // HitTestAndDispatch, and neither label is a ToolbarAction so each falls through to the
            // OnClick-already-ran path). Auto runs gray-world over the current frame and drops the result
            // into the sliders -- which then act as the fine-tune.
            var btnH = FontSize + gap;

            const string autoLabel = "Auto";
            var autoW = MeasureText(autoLabel, FontSize) + gap * 2f;
            FillRect(x, y, autoW, btnH, ToolbarButtonBg);
            DrawText(autoLabel, x + gap, y + gap / 2f, FontSize, ViewerTheme.Palette.BodyText);
            RegisterClickable(x, y, autoW, btnH, new HitResult.ButtonHit("AutoWhiteBalance"),
                _ =>
                {
                    if (_source is { } src && AutoWhiteBalance.GrayWorld(src) is { } auto)
                    {
                        state.ManualWhiteBalance = auto;
                        state.NeedsRedraw = true;
                    }
                });

            const string resetLabel = "Reset WB";
            var resetW = MeasureText(resetLabel, FontSize) + gap * 2f;
            var resetX = x + autoW + gap;
            FillRect(resetX, y, resetW, btnH, ToolbarButtonBg);
            DrawText(resetLabel, resetX + gap, y + gap / 2f, FontSize, ViewerTheme.Palette.BodyText);
            RegisterClickable(resetX, y, resetW, btnH, new HitResult.ButtonHit("ResetWhiteBalance"),
                _ => { state.ManualWhiteBalance = (1f, 1f, 1f); state.NeedsRedraw = true; });
            y += btnH + FontSize;
        }

        private static float WbValueToFrac(float value)
        {
            var clamped = Math.Clamp(value, WbMin, WbMax);
            return MathF.Log(clamped / WbMin) / MathF.Log(WbMax / WbMin);
        }

        private static float WbFracToValue(float frac)
        {
            var f = Math.Clamp(frac, 0f, 1f);
            return WbMin * MathF.Exp(f * MathF.Log(WbMax / WbMin));
        }

        /// <summary>
        /// Begins a manual white-balance drag (press on a WB slider track). Public so both mouse-down paths
        /// (FitsViewer Program + GUI viewer tab) dispatch identically, mirroring <see cref="BeginScrubAt"/>.
        /// </summary>
        public void BeginWhiteBalanceDragAt(int channel, float px)
        {
            if (_state is not { } || (uint)channel >= 3u)
            {
                return;
            }

            _state.WhiteBalanceDragChannel = channel;
            UpdateWhiteBalanceDrag(px);
        }

        // Maps a cursor X onto a WB multiplier for the active drag channel against its captured track rect.
        private void UpdateWhiteBalanceDrag(float px)
        {
            if (_state is not { } state)
            {
                return;
            }
            var ch = state.WhiteBalanceDragChannel;
            if ((uint)ch >= 3u || _wbTrackRects[ch].Width <= 0f)
            {
                return;
            }

            var track = _wbTrackRects[ch];
            var frac = Math.Clamp((px - track.X) / track.Width, 0f, 1f);
            var value = WbFracToValue(frac);
            var wb = state.ManualWhiteBalance;
            state.ManualWhiteBalance = ch switch
            {
                0 => (value, wb.G, wb.B),
                1 => (wb.R, value, wb.B),
                _ => (wb.R, wb.G, value),
            };
            state.NeedsRedraw = true;
        }

        // -----------------------------------------------------------------------
        // Wavelet-sharpen layer sliders (info panel; live stacked view only)
        //
        // The Registax / AstroSurface 6-layer convention: one slider per a-trous detail scale, finest first.
        // Linear gain in [0, WaveletGainMax], neutral 1.0. Dragging a layer turns sharpening on and re-pushes
        // the params; the controller re-sharpens the cached stacked master off-thread (no re-stack), so the
        // image follows within a frame or two. Same press + drag + release model as the WB sliders.
        // -----------------------------------------------------------------------

        private void RenderWaveletControls(ViewerState state, ref float y, float x, float panelWidth)
        {
            DrawTextLine(ref y, x, "-- Wavelet Sharpen --", ViewerTheme.Palette.HeaderText);

            var gap = 6f * DpiScale;
            var btnH = FontSize + gap;

            // On/Off toggle (active = blue) + Reset-to-default, both self-contained via OnClick.
            var toggleLabel = state.WaveletSharpenEnabled ? "Sharpen: On" : "Sharpen: Off";
            var toggleW = MeasureText(toggleLabel, FontSize) + gap * 2f;
            FillRect(x, y, toggleW, btnH, state.WaveletSharpenEnabled ? TransportTrackFill : ToolbarButtonBg);
            DrawText(toggleLabel, x + gap, y + gap / 2f, FontSize, ViewerTheme.Palette.BodyText);
            RegisterClickable(x, y, toggleW, btnH, new HitResult.ButtonHit("WaveletToggle"),
                _ => { state.WaveletSharpenEnabled = !state.WaveletSharpenEnabled; state.WaveletDirty = true; state.NeedsRedraw = true; });

            const string resetLabel = "Reset";
            var resetW = MeasureText(resetLabel, FontSize) + gap * 2f;
            var resetX = x + toggleW + gap;
            FillRect(resetX, y, resetW, btnH, ToolbarButtonBg);
            DrawText(resetLabel, resetX + gap, y + gap / 2f, FontSize, ViewerTheme.Palette.BodyText);
            RegisterClickable(resetX, y, resetW, btnH, new HitResult.ButtonHit("WaveletReset"),
                _ => { state.WaveletGains = WaveletSharpenOptions.PlanetaryDefault.Gains; state.WaveletDirty = true; state.NeedsRedraw = true; });
            y += btnH + gap;

            var rowH = FontSize + gap;
            var labelW = MeasureText("6", FontSize) + gap;
            var valueW = MeasureText("0.0", FontSize) + gap;
            var barH = MathF.Max(4f, 6f * DpiScale);
            var handleW = MathF.Max(4f, 6f * DpiScale);
            // Brighter track fill when active; dim when sharpening is off (the sliders still work -- a drag
            // re-enables -- but read as inactive).
            var fill = state.WaveletSharpenEnabled
                ? RGBAColor32.FromFloat(0.45f, 0.72f, 0.78f, 1f)
                : RGBAColor32.FromFloat(0.40f, 0.45f, 0.48f, 1f);

            var gains = state.WaveletGains;
            for (var b = 0; b < _waveletTrackRects.Length; b++)
            {
                if (b >= gains.Length)
                {
                    _waveletTrackRects[b] = default;
                    continue;
                }

                var rowY = y;
                DrawText((b + 1).ToString(), x, rowY, FontSize, ViewerTheme.Palette.BodyText);

                var trackX = x + labelW;
                var trackRight = x + panelWidth - valueW;
                var trackW = MathF.Max(0f, trackRight - trackX);
                if (trackW > 0f)
                {
                    var barY = rowY + (FontSize - barH) / 2f;
                    FillRect(trackX, barY, trackW, barH, TransportTrackBg);
                    var frac = Math.Clamp(gains[b] / WaveletGainMax, 0f, 1f);
                    FillRect(trackX, barY, trackW * frac, barH, fill);

                    var handleMax = MathF.Max(trackX, trackX + trackW - handleW);
                    var handleX = Math.Clamp(trackX + trackW * frac - handleW / 2f, trackX, handleMax);
                    FillRect(handleX, rowY, handleW, FontSize, TransportHandle);

                    var hitY = rowY - gap / 2f;
                    var hitH = FontSize + gap;
                    _waveletTrackRects[b] = new RectF32(trackX, hitY, trackW, hitH);
                    RegisterClickable(trackX, hitY, trackW, hitH, new WaveletSliderHit(b));
                }
                else
                {
                    _waveletTrackRects[b] = default;
                }

                DrawText(gains[b].ToString("0.0"), trackRight, rowY, FontSize, ViewerTheme.Palette.DimText);
                y = rowY + rowH;
            }
        }

        /// <summary>
        /// Begins a wavelet-layer slider drag (press on a layer track). Public so both mouse-down paths
        /// (FitsViewer Program + GUI viewer tab) dispatch identically, mirroring <see cref="BeginWhiteBalanceDragAt"/>.
        /// Touching a layer turns sharpening on.
        /// </summary>
        public void BeginWaveletDragAt(int band, float px)
        {
            if (_state is not { } state || (uint)band >= (uint)_waveletTrackRects.Length)
            {
                return;
            }

            state.WaveletDragBand = band;
            state.WaveletSharpenEnabled = true;
            UpdateWaveletDrag(px);
        }

        // Maps a cursor X onto a per-layer gain for the active drag band against its captured track rect.
        private void UpdateWaveletDrag(float px)
        {
            if (_state is not { } state)
            {
                return;
            }
            var b = state.WaveletDragBand;
            if ((uint)b >= (uint)_waveletTrackRects.Length || _waveletTrackRects[b].Width <= 0f || b >= state.WaveletGains.Length)
            {
                return;
            }

            var track = _waveletTrackRects[b];
            var frac = Math.Clamp((px - track.X) / track.Width, 0f, 1f);
            state.WaveletGains = state.WaveletGains.SetItem(b, frac * WaveletGainMax);
            state.WaveletDirty = true;
            state.NeedsRedraw = true;
        }

        // -----------------------------------------------------------------------
        // Status bar
        // -----------------------------------------------------------------------

        private void RenderStatusBar(AstroImageDocument? document, ViewerState state)
        {
            if (_fontPath is null)
            {
                return;
            }

            var barY = Height - StatusBarHeight;

            var statusParts = new List<string>();

            if (document?.Wcs is { HasCDMatrix: true } wcs)
            {
                var scale = wcs.PixelScaleArcsec;
                var label = wcs.IsApproximate ? "approx" : "solved";
                var ra = CoordinateUtils.HoursToHMS(wcs.CenterRA);
                var dec = CoordinateUtils.DegreesToDMS(wcs.CenterDec);
                statusParts.Add($"WCS: {label} ({scale:F2}\"/px)  RA {ra}  Dec {dec}");
            }

            if (document is not null)
            {
                var zoomPct = state.Zoom * 100f;
                statusParts.Add($"Zoom: {zoomPct:F0}%");
            }

            if (document?.Stars is { Count: > 0 } detectedStars)
            {
                statusParts.Add($"Stars: {detectedStars.Count}  HFR: {document.AverageHFR:F2}  FWHM: {document.AverageFWHM:F2}");
            }

            if (state.StatusMessage is { } msg)
            {
                statusParts.Add(msg);
            }

            var statusText = string.Join("  |  ", statusParts);
            RenderTextBar(statusText.AsSpan(), _fontPath!, 0, barY, Width, StatusBarHeight,
                FontSize, ViewerTheme.StatusBarBg, ViewerTheme.Palette.BodyText,
                horizontalPadding: PanelPadding, alignX: TextAlign.Near, alignY: TextAlign.Near);
        }

        // -----------------------------------------------------------------------
        // SER transport bar (play/pause, scrub, frame + timestamp + fps readout)
        //
        // Drawn into the reserved _transportRect (the image pane was shrunk in ComputeLayout so the strip
        // never overlaps the picture). Reads only ViewerState + the cached _source accessors -- timestamp
        // lookups hit the source's managed cache, never the lazy file-tail trailer, so nothing here does
        // disk I/O. The scrub track rect is captured for the press/drag -> frame mapping in ScrubAt.
        // -----------------------------------------------------------------------

        private void RenderTransportBar(ViewerState state)
        {
            var r = _transportRect;
            if (r.Width <= 0 || r.Height <= 0 || _fontPath is null)
            {
                _scrubTrackRect = default;
                return;
            }

            FillRect(r.X, r.Y, r.Width, r.Height, TransportBg);

            var pad = PanelPadding;
            var fs = ToolbarFontSize;
            var contentH = r.Height - pad * 2;
            if (contentH <= 0f)
            {
                // Window minimized to a sliver -- nothing usable to draw; bail before any size math.
                _scrubTrackRect = default;
                return;
            }
            var textY = r.Y + (r.Height - fs) / 2f;

            // Play/pause button (ASCII glyphs to stay font/atlas-safe): show the action's target -- ">"
            // when paused (click to play), "||" when playing (click to pause). Self-contained via OnClick,
            // so both mouse-down paths (FitsViewer Program + GUI tab) toggle it without bespoke handling.
            var btnX = r.X + pad;
            var btnY = r.Y + pad;
            var btnSize = contentH;
            var ppLabel = state.IsPlaying ? "||" : ">";
            FillRect(btnX, btnY, btnSize, btnSize, ToolbarButtonBg);
            var ppW = MeasureText(ppLabel, fs);
            DrawText(ppLabel, btnX + (btnSize - ppW) / 2f, textY, fs, RGBAColor32.FromFloat(0.9f, 0.9f, 0.9f, 1f));
            RegisterClickable(btnX, btnY, btnSize, btnSize, new HitResult.ButtonHit("PlayPause"),
                _ => { state.IsPlaying = !state.IsPlaying; state.NeedsRedraw = true; });

            // RAW / STACK toggle: switch between the raw frame and the live rolling-window lucky-imaging
            // stack (which follows the playhead). Active (blue) when stacking; "STACK..." while the first
            // master is still computing (the displayed source is still the raw one until then).
            var stacking = state.ShowStacked;
            var stackLive = _source is LiveStackPreviewSource;
            var stackLabel = !stacking ? "RAW" : (stackLive ? "STACK" : "STACK...");
            var stackLabelW = MeasureText(stackLabel, fs);
            var stackBtnX = btnX + btnSize + pad;
            var stackBtnW = stackLabelW + pad * 2;
            FillRect(stackBtnX, btnY, stackBtnW, btnSize, stacking ? TransportTrackFill : ToolbarButtonBg);
            DrawText(stackLabel, stackBtnX + (stackBtnW - stackLabelW) / 2f, textY, fs, RGBAColor32.FromFloat(0.92f, 0.92f, 0.95f, 1f));
            RegisterClickable(stackBtnX, btnY, stackBtnW, btnSize, new HitResult.ButtonHit("StackToggle"),
                _ => { state.ShowStacked = !state.ShowStacked; state.WaveletDirty = true; state.NeedsTextureUpdate = true; state.NeedsRedraw = true; });

            // Right-aligned readout: frame n/total, capture timestamp (if present), playback fps.
            var idx = state.FrameIndex;
            var total = state.FrameCount;
            var timestamp = string.Empty;
            if (_source is { HasTimestamps: true } src)
            {
                var ts = src.TimestampOf(idx);
                if (ts != DateTimeOffset.MinValue)
                {
                    timestamp = ts.ToString("HH:mm:ss.fff") + " UT   ";
                }
            }

            // Show the file's nominal capture rate (often hundreds of fps for planetary lucky-imaging);
            // the actual display advance is still capped by PlaybackFps. Fall back to PlaybackFps when the
            // source has no timestamps to derive a nominal rate.
            var fps = state.SourceFps ?? state.PlaybackFps;
            var readout = $"{idx + 1}/{total}   {timestamp}{fps:F0} fps";
            var readoutW = MeasureText(readout, fs);
            var readoutX = r.X + r.Width - pad - readoutW;
            DrawText(readout, readoutX, textY, fs, RGBAColor32.FromFloat(0.85f, 0.85f, 0.85f, 1f));

            // Scrub track fills the gap between the buttons and the readout.
            var trackX = stackBtnX + stackBtnW + pad * 2;
            var trackRight = readoutX - pad * 2;
            var trackW = MathF.Max(0f, trackRight - trackX);
            if (trackW <= 0f)
            {
                _scrubTrackRect = default;
                return;
            }

            var barH = MathF.Max(4f, 6f * DpiScale);
            var barY2 = r.Y + (r.Height - barH) / 2f;
            FillRect(trackX, barY2, trackW, barH, TransportTrackBg);

            var frac = total > 1 ? (float)idx / (total - 1) : 0f;
            FillRect(trackX, barY2, trackW * frac, barH, TransportTrackFill);

            // Handle marker at the current position. Guard the clamp's upper bound: in a slim track
            // (trackW < handleW) trackX + trackW - handleW < trackX, which would make Math.Clamp's max <
            // min and throw (the minimize-to-sliver crash).
            var handleW = MathF.Max(4f, 6f * DpiScale);
            var handleMax = MathF.Max(trackX, trackX + trackW - handleW);
            var handleX = Math.Clamp(trackX + trackW * frac - handleW / 2f, trackX, handleMax);
            FillRect(handleX, btnY, handleW, contentH, TransportHandle);

            // The press/drag hit region is the full-height track band; _scrubTrackRect's X/Width drive the
            // px -> frame mapping in ScrubAt (Y/Height are only the clickable extent).
            _scrubTrackRect = new RectF32(trackX, btnY, trackW, contentH);
            RegisterClickable(trackX, btnY, trackW, contentH, new TransportScrubHit());
        }

        /// <summary>
        /// Begins a transport scrub (press on the scrub track): pauses playback and seeks to the press X.
        /// Shared by the FitsViewer mouse-down path and the GUI viewer-tab path so both behave identically.
        /// </summary>
        public void BeginScrubAt(float px)
        {
            if (_state is not { } state)
            {
                return;
            }

            state.IsScrubbing = true;
            state.IsPlaying = false; // pause while scrubbing; resume is an explicit play
            ScrubAt(px);
        }

        // Maps a cursor X onto a frame index against the captured scrub track and requests that frame.
        // The SequencePlayer decodes it off the render thread, so dragging never blocks the UI.
        private void ScrubAt(float px)
        {
            if (_state is not { } state || _scrubTrackRect.Width <= 0f || state.FrameCount <= 1)
            {
                return;
            }

            var frac = Math.Clamp((px - _scrubTrackRect.X) / _scrubTrackRect.Width, 0f, 1f);
            state.RequestedFrame = (int)MathF.Round(frac * (state.FrameCount - 1));
            state.NeedsRedraw = true;
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

        // -----------------------------------------------------------------------
        // Input handling
        // -----------------------------------------------------------------------

        public override bool HandleInput(InputEvent evt) => evt switch
        {
            InputEvent.KeyDown(var key, var modifiers) => HandleViewerKey(key, modifiers),
            InputEvent.MouseDown(var px, var py, _, _, _) => HandleViewerMouseDown(px, py),
            InputEvent.MouseMove(var px, var py) => HandleViewerMouseMove(px, py),
            InputEvent.MouseUp(_, _, _) => HandleViewerMouseUp(),
            InputEvent.Scroll(var delta, var mx, var my, _) => HandleViewerScroll(delta, mx, my),
            _ => false
        };

        private bool HandleViewerKey(InputKey key, InputModifier modifiers)
        {
            if (_state is not { } state)
            {
                return false;
            }

            // Dropdowns get first crack at keyboard so Escape/Enter/Arrows route
            // to the open overlay before falling through to global shortcuts
            // (e.g. Escape would otherwise quit via RequestExitSignal).
            if (state.ToolbarDropdown.HandleKeyDown(key))
            {
                state.NeedsRedraw = true;
                return true;
            }

            var ctrl = (modifiers & InputModifier.Ctrl) != 0;
            var shift = (modifiers & InputModifier.Shift) != 0;

            // SER transport keys take priority while a sequence is loaded -- they deliberately claim
            // Space / arrows / Home / End / Up / Down (Up/Down would otherwise step the file list) for
            // playback. Seeks route through state.RequestedFrame so decode stays off the render thread.
            if (state.IsSequence && !ctrl && HandleTransportKey(key, state))
            {
                state.NeedsRedraw = true;
                return true;
            }

            if (ctrl)
            {
                switch (key)
                {
                    case InputKey.Plus:
                        ViewerActions.ZoomIn(state);
                        return true;
                    case InputKey.Minus:
                        ViewerActions.ZoomOut(state);
                        return true;
                    case InputKey.D0:
                        ViewerActions.ZoomToFit(state);
                        return true;
                    case InputKey.D1:
                        ViewerActions.ZoomToActual(state);
                        return true;
                    case >= InputKey.D2 and <= InputKey.D9:
                        ViewerActions.ZoomTo(state, 1f / (key - InputKey.D0));
                        return true;
                }
            }

            switch (key)
            {
                case InputKey.Escape:
                    PostSignal(new RequestExitSignal());
                    return true;
                case InputKey.F11:
                    PostSignal(new ToggleFullscreenSignal());
                    return true;
                case InputKey.T:
                    ViewerActions.ToggleStretch(state);
                    return true;
                case InputKey.S:
                    state.ShowStarOverlay = !state.ShowStarOverlay;
                    return true;
                case InputKey.C:
                    if (_document is not null)
                    {
                        ViewerActions.CycleChannelView(state, _document.UnstretchedImage.ChannelCount);
                    }
                    return true;
                case InputKey.D:
                    ViewerActions.CycleDebayerAlgorithm(state);
                    return true;
                case InputKey.I:
                    state.ShowInfoPanel = !state.ShowInfoPanel;
                    return true;
                case InputKey.K:
                    // Toggle the live rolling-window stack vs the raw frame (sequence-only). The controller
                    // keeps showing the raw frame until the first master is built.
                    if (state.IsSequence)
                    {
                        state.ShowStacked = !state.ShowStacked;
                        state.WaveletDirty = true; // push the current sharpen state when (re)entering stacked
                        state.NeedsTextureUpdate = true;
                        state.NeedsRedraw = true;
                    }
                    return true;
                case InputKey.L:
                    state.ShowFileList = !state.ShowFileList;
                    return true;
                case InputKey.Plus:
                    ViewerActions.CycleStretchPreset(state);
                    return true;
                case InputKey.Minus:
                    ViewerActions.CycleStretchPreset(state, reverse: true);
                    return true;
                case InputKey.B:
                    if (shift)
                    {
                        ViewerActions.CycleCurvesMode(state);
                    }
                    else
                    {
                        ViewerActions.CycleCurvesBoost(state);
                    }
                    return true;
                case InputKey.G:
                    state.ShowGrid = !state.ShowGrid;
                    return true;
                case InputKey.O:
                    state.ShowOverlays = !state.ShowOverlays;
                    state.NeedsRedraw = true;
                    return true;
                case InputKey.H:
                    ViewerActions.CycleHdr(state);
                    return true;
                case InputKey.V:
                    if (shift)
                    {
                        state.HistogramLogScale = !state.HistogramLogScale;
                    }
                    else
                    {
                        state.ShowHistogram = !state.ShowHistogram;
                    }
                    return true;
                case InputKey.P:
                    PostSignal(new PlateSolveSignal());
                    return true;
                case InputKey.F:
                    ViewerActions.ZoomToFit(state);
                    return true;
                case InputKey.N:
                    TryToggleBackgroundNeutralization(state);
                    return true;
                case InputKey.W:
                    TryStartColorCalibration(state);
                    return true;
                case InputKey.R:
                    ViewerActions.ZoomToActual(state);
                    return true;
                case InputKey.Up:
                    if (state.SelectedFileIndex > 0)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex - 1);
                    }
                    return true;
                case InputKey.Down:
                    if (state.SelectedFileIndex < state.ImageFileNames.Count - 1)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex + 1);
                    }
                    return true;
                default:
                    return false;
            }
        }

        // SER transport keys (sequence-only): play/pause, step, jump to ends, speed. Step/Home/End pause
        // and request a frame; the SequencePlayer decodes it off the render thread next tick.
        private bool HandleTransportKey(InputKey key, ViewerState state)
        {
            switch (key)
            {
                case InputKey.Space:
                case InputKey.Tab:
                    state.IsPlaying = !state.IsPlaying;
                    return true;
                case InputKey.Left:
                    state.IsPlaying = false;
                    state.RequestedFrame = Math.Max(0, state.FrameIndex - 1);
                    return true;
                case InputKey.Right:
                    state.IsPlaying = false;
                    state.RequestedFrame = Math.Min(state.FrameCount - 1, state.FrameIndex + 1);
                    return true;
                case InputKey.Home:
                    state.IsPlaying = false;
                    state.RequestedFrame = 0;
                    return true;
                case InputKey.End:
                    state.IsPlaying = false;
                    state.RequestedFrame = state.FrameCount - 1;
                    return true;
                case InputKey.Up:
                    ViewerActions.CyclePlaybackSpeed(state, faster: true);
                    return true;
                case InputKey.Down:
                    ViewerActions.CyclePlaybackSpeed(state, faster: false);
                    return true;
                default:
                    return false;
            }
        }

        private void TryStartColorCalibration(ViewerState state)
        {
            if (_document?.Stars is { Count: >= 5 }
                && _document.ColorCalibration is null
                && (_document.UnstretchedImage.ChannelCount >= 3
                    || _document.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB)
                && _document.TryBeginColorCalibration())
            {
                state.StatusMessage = "Calibrating color...";
                state.NeedsRedraw = true;
                var docForTask = _document;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var db = CelestialObjectDB is { IsValueCreated: true } lazy
                            ? await lazy.WithCancellation(CancellationToken.None)
                            : null!;

                        // Try SPCC first, fall back to sky-background method.
                        // Capture-by-local (docForTask) so the task always
                        // clears the in-flight flag on the doc it started for,
                        // even if the user has navigated away in the meantime.
                        var (matched, diag) = await docForTask.ComputeSpccColorCalibrationAsync(db);
                        if (matched <= 0)
                            (matched, diag) = await docForTask.ComputeColorCalibrationAsync(db);
                        if (docForTask.ColorCalibration is { } wb)
                        {
                            state.ColorCalibrationEnabled = true;
                            if (state.StretchMode is StretchMode.Unlinked)
                            {
                                state.StretchMode = StretchMode.Linked;
                            }
                            System.Console.Error.WriteLine($"[ColorCal] {diag}");
                            state.StatusMessage = matched > 0
                                ? $"WB ({matched}★): R={wb.Item1:F3} G=1.000 B={wb.Item3:F3}"
                                : null;
                        }
                        else
                        {
                            System.Console.Error.WriteLine($"[ColorCal] FAIL: {diag}");
                            state.StatusMessage = $"Calibration failed: {diag}";
                        }
                    }
                    finally
                    {
                        docForTask.EndColorCalibration();
                        state.NeedsRedraw = true;
                    }
                });
            }
        }

        private void TryToggleBackgroundNeutralization(ViewerState state)
        {
            if (state.BackgroundNeutralizationEnabled)
            {
                _document!.BackgroundNeutralization = null;
                state.BackgroundNeutralizationEnabled = false;
                state.NeedsRedraw = true;
                return;
            }

            var gains = _document?.ComputeBackgroundNeutralization(state.BackgroundNeutralizationMethod);
            if (gains is { } g)
            {
                state.BackgroundNeutralizationEnabled = true;
                state.NeedsRedraw = true;
                System.Console.Error.WriteLine($"[BgNeut/{state.BackgroundNeutralizationMethod}] R={g.R:F3} G={g.G:F3} B={g.B:F3}");
            }
        }

        // -----------------------------------------------------------------------
        // Mouse handling
        // -----------------------------------------------------------------------

        /// <summary>
        /// Handles mouse down: hit-tests toolbar/file list, then starts panning.
        /// Returns <c>true</c> if the event was consumed by hit-test, <c>false</c>
        /// if panning was started (caller may need to handle toolbar actions via
        /// <see cref="ViewerActions.HandleToolbarAction"/>).
        /// </summary>
        private bool HandleViewerMouseDown(float px, float py)
        {
            if (_state is not { } state)
            {
                return false;
            }

            state.MouseScreenPosition = (px, py);

            // Unified hit test — OnClick handlers fire for self-contained actions (e.g. HistogramLog)
            var hit = HitTestAndDispatch(px, py);

            if (hit is HitResult.ButtonHit { Action: var action } && Enum.TryParse<ToolbarAction>(action, out var toolbarAction))
            {
                ViewerActions.HandleToolbarAction(state, _document, toolbarAction);
                if (toolbarAction is ToolbarAction.ColorCalibrate or ToolbarAction.SpccCalibrate)
                {
                    TryStartColorCalibration(state);
                }
                else if (toolbarAction is ToolbarAction.BackgroundNeutralize)
                {
                    TryToggleBackgroundNeutralization(state);
                }
                return true;
            }

            if (hit is HitResult.ListItemHit { ListId: "FileList", Index: var fileIndex })
            {
                ViewerActions.SelectFile(state, fileIndex);
                return true;
            }

            if (hit is ResizeHandleHit { Id: "FileList" })
            {
                state.IsResizingFileList = true;
                state.NeedsRedraw = true;
                return true;
            }

            if (hit is TransportScrubHit)
            {
                BeginScrubAt(px);
                return true;
            }

            if (hit is WhiteBalanceSliderHit { Channel: var wbChannel })
            {
                BeginWhiteBalanceDragAt(wbChannel, px);
                return true;
            }

            if (hit is WaveletSliderHit { Band: var wlBand })
            {
                BeginWaveletDragAt(wlBand, px);
                return true;
            }

            if (hit is not null)
            {
                return true; // OnClick already handled it (e.g. HistogramLog, PlayPause)
            }

            // No hit — start panning
            ViewerActions.BeginPan(state, px, py);
            return false;
        }

        private bool HandleViewerMouseMove(float px, float py)
        {
            if (_state is not { } state)
            {
                return false;
            }

            state.MouseScreenPosition = (px, py);

            // Transport scrub drag: continuously seek to the dragged frame (decoded off the render thread).
            if (state.IsScrubbing)
            {
                ScrubAt(px);
                return true;
            }

            // White-balance slider drag: continuously re-derive the WB multiplier from cursor-X.
            if (state.WhiteBalanceDragChannel >= 0)
            {
                UpdateWhiteBalanceDrag(px);
                return true;
            }

            // Wavelet-layer slider drag: continuously re-derive the per-layer gain from cursor-X.
            if (state.WaveletDragBand >= 0)
            {
                UpdateWaveletDrag(px);
                return true;
            }

            // File-list resize drag: width tracks the cursor's X position in
            // DPI-independent units. Clamped by FileListWidthBase's setter.
            if (state.IsResizingFileList)
            {
                state.FileListWidthBase = px / DpiScale;
                state.NeedsRedraw = true;
                return true;
            }

            // Panning always needs a redraw (image position changes)
            if (state.IsPanning)
            {
                ViewerActions.UpdatePan(state, px, py);
                return true;
            }

            // Only redraw when cursor moves to a different image pixel
            var prevPos = state.CursorImagePosition;
            // Image-area pane rect (origin + size) from the single layout pass.
            var area = _layout.ImageArea;
            ViewerActions.UpdateCursorFromScreenPosition(_document, state, px, py, area.X, area.Y, area.Width, area.Height);
            return state.CursorImagePosition != prevPos;
        }

        private bool HandleViewerMouseUp()
        {
            if (_state is { } state)
            {
                if (state.IsScrubbing)
                {
                    state.IsScrubbing = false;
                    state.NeedsRedraw = true;
                }
                if (state.WhiteBalanceDragChannel >= 0)
                {
                    state.WhiteBalanceDragChannel = -1;
                    state.NeedsRedraw = true;
                }
                if (state.WaveletDragBand >= 0)
                {
                    state.WaveletDragBand = -1;
                    state.NeedsRedraw = true;
                }
                if (state.IsResizingFileList)
                {
                    state.IsResizingFileList = false;
                    state.NeedsRedraw = true;
                }
                ViewerActions.EndPan(state);
                return true;
            }
            return false;
        }

        private bool HandleViewerScroll(float scrollY, float mouseX, float mouseY)
        {
            if (_state is not { } state)
            {
                return false;
            }

            // Scroll file list when hovering over it
            if (state.ShowFileList && mouseX >= 0 && mouseX < ScaledFileListWidth && mouseY > ScaledToolbarHeight)
            {
                ViewerActions.ScrollFileList(state, -(int)scrollY * 3);
                return true;
            }

            // Zoom: inside the image viewport (image-area pane rect from the single layout pass)
            var area = _layout.ImageArea;
            var inImageViewport = mouseX >= area.X && mouseX < area.X + area.Width
                               && mouseY >= area.Y && mouseY < area.Y + area.Height;

            if (inImageViewport)
            {
                var zoomFactor = scrollY > 0 ? 1.15f : 1f / 1.15f;
                var oldZoom = state.Zoom;
                var newZoom = MathF.Max(0.01f, oldZoom * zoomFactor);

                var cx = mouseX - area.X - area.Width / 2f - state.PanOffset.X;
                var cy = mouseY - area.Y - area.Height / 2f - state.PanOffset.Y;

                state.PanOffset = (
                    state.PanOffset.X - cx * (newZoom / oldZoom - 1f),
                    state.PanOffset.Y - cy * (newZoom / oldZoom - 1f)
                );

                state.ZoomToFit = false;
                state.Zoom = newZoom;
                return true;
            }

            return false;
        }
    }
}
