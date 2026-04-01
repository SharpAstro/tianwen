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

        /// <summary>DPI scale factor. Set from framebuffer size / window size ratio.</summary>
        public float DpiScale { get; set; } = 1f;

        /// <summary>
        /// Lazy-initialized celestial object database used for object overlays.
        /// </summary>
        public DotNext.Threading.AsyncLazy<ICelestialObjectDB>? CelestialObjectDB { get; set; }

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

        // Histogram constants
        private const float BaseHistogramWidth = 256f;
        private const float BaseHistogramHeight = 128f;
        private const float BaseHistogramMargin = 8f;

        // Scaled accessors
        private float InfoPanelWidth => BaseInfoPanelWidth * DpiScale;
        private float StatusBarHeight => BaseStatusBarHeight * DpiScale;
        private float ToolbarHeight => BaseToolbarHeight * DpiScale;
        private float FileListWidth => BaseFileListWidth * DpiScale;
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
        ];

        // -----------------------------------------------------------------------
        // Abstract methods — GPU-specific rendering
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders the image quad with stretch uniforms, optional WCS grid, and viewport placement.
        /// </summary>
        protected abstract void RenderImageQuad(AstroImageDocument? doc, ViewerState state,
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
        /// Called when the viewport is resized.
        /// </summary>
        protected abstract void OnResize(uint width, uint height);

        /// <summary>
        /// Uploads image texture data for the given channel.
        /// </summary>
        public abstract void UploadImageTexture(ReadOnlySpan<float> data, int channel,
            int imageWidth, int imageHeight);

        /// <summary>
        /// Uploads histogram data from a document. Called once per image load.
        /// </summary>
        public abstract void UploadHistogramData(AstroImageDocument document);

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
        /// </summary>
        public (float Width, float Height) GetImageAreaSize(ViewerState state)
        {
            var fileListW = state.ShowFileList ? FileListWidth : 0;
            var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
            var areaW = (float)(Width - fileListW - panelW);
            var areaH = (float)(Height - ToolbarHeight - StatusBarHeight);
            return (areaW, areaH);
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
        public void UploadDocumentTextures(AstroImageDocument document, ViewerState state)
        {
            state.NeedsTextureUpdate = false;
            state.StatusMessage = "Preparing display...";

            var image = document.UnstretchedImage;
            var pixelWidth = image.Width;
            var pixelHeight = image.Height;

            // Raw Bayer: upload single channel, GPU shader does bilinear debayer
            if (image.ImageMeta.SensorType is TianWen.Lib.Imaging.SensorType.RGGB && image.ChannelCount == 1
                && state.ChannelView is ChannelView.Composite)
            {
                ChannelTextureCount = 3; // shader produces RGB
                ImageSourceMode = 2; // RawBayer
                BayerOffsetX = image.ImageMeta.BayerOffsetX;
                BayerOffsetY = image.ImageMeta.BayerOffsetY;
                UploadChannelTexture(image.GetChannelSpan(0), 0, pixelWidth, pixelHeight);
            }
            else if (state.ChannelView is ChannelView.Composite && image.ChannelCount >= 3)
            {
                ChannelTextureCount = 3;
                ImageSourceMode = 0; // ProcessedChannels

                for (var i = 0; i < 3; i++)
                {
                    UploadChannelTexture(image.GetChannelSpan(i), i, pixelWidth, pixelHeight);
                }
            }
            else
            {
                ChannelTextureCount = 1;
                ImageSourceMode = image.ChannelCount == 1 ? 1 : 0; // RawMono or ProcessedChannels

                var channelIndex = state.ChannelView switch
                {
                    ChannelView.Composite or ChannelView.Channel0 or ChannelView.Red => 0,
                    ChannelView.Channel1 or ChannelView.Green => Math.Min(1, image.ChannelCount - 1),
                    ChannelView.Channel2 or ChannelView.Blue => Math.Min(2, image.ChannelCount - 1),
                    var cv => throw new InvalidOperationException($"Invalid channel view {cv}")
                };

                UploadChannelTexture(image.GetChannelSpan(channelIndex), 0, pixelWidth, pixelHeight);
            }

            UploadHistogramData(document);
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

        public void Render(AstroImageDocument? document, ViewerState state)
        {
            _state = state;
            _document = document;
            BeginFrame();

            // Draw image FIRST so UI chrome paints on top of it
            if (ImageWidth > 0 && ImageHeight > 0)
            {
                var stretch = document?.ComputeStretchUniforms(state.StretchMode, state.StretchParameters)
                    ?? new StretchUniforms(StretchMode.None, 1f, default, default, default, default, default);
                var gridWcs = state.ShowGrid && document?.Wcs is { HasCDMatrix: true } w ? w : (WCS?)null;
                RenderImage(document, state, stretch, gridWcs);
            }

            // UI chrome (drawn on top of image)
            RenderToolbar(document, state);

            if (state.ShowFileList)
            {
                RenderFileList(state);
            }

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

            if (state.ShowHistogram && document is not null)
            {
                RenderHistogram(document, state);
            }

            if (state.ShowInfoPanel && document is not null)
            {
                RenderInfoPanel(document, state);
            }

            RenderStatusBar(document, state);
        }

        // -----------------------------------------------------------------------
        // Image rendering — computes placement, delegates to abstract
        // -----------------------------------------------------------------------

        private void RenderImage(AstroImageDocument? document, ViewerState state, StretchUniforms stretch, WCS? gridWcs)
        {
            var fileListW = state.ShowFileList ? FileListWidth : 0;
            var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
            var areaW = (float)(Width - fileListW - panelW);
            var areaH = (float)(Height - ToolbarHeight - StatusBarHeight);

            var fitScale = MathF.Min(areaW / ImageWidth, areaH / ImageHeight);
            if (state.ZoomToFit)
            {
                state.Zoom = fitScale;
            }

            var scale = state.Zoom;
            var drawW = ImageWidth * scale;
            var drawH = ImageHeight * scale;
            var offsetX = fileListW + (areaW - drawW) / 2f + state.PanOffset.X;
            var offsetY = ToolbarHeight + (areaH - drawH) / 2f + state.PanOffset.Y;

            RenderImageQuad(document, state, stretch, gridWcs,
                offsetX, offsetY, offsetX + drawW, offsetY + drawH, Width, Height);
        }

        // -----------------------------------------------------------------------
        // Toolbar
        // -----------------------------------------------------------------------

        private void RenderToolbar(AstroImageDocument? document, ViewerState state)
        {
            FillRect(0, 0, Width, ToolbarHeight, 0.18f, 0.18f, 0.20f, 1f);

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

                var hovered = enabled && mouseX >= x && mouseX < x + btnW && mouseY >= btnY && mouseY < btnY + btnH;

                if (!enabled)
                {
                    FillRect(x, btnY, btnW, btnH, 0.20f, 0.20f, 0.22f, 1f);
                }
                else if (active && hovered)
                {
                    FillRect(x, btnY, btnW, btnH, 0.25f, 0.35f, 0.55f, 1f);
                }
                else if (active)
                {
                    FillRect(x, btnY, btnW, btnH, 0.20f, 0.30f, 0.50f, 1f);
                }
                else if (hovered)
                {
                    FillRect(x, btnY, btnW, btnH, 0.35f, 0.35f, 0.40f, 1f);
                }
                else
                {
                    FillRect(x, btnY, btnW, btnH, 0.25f, 0.25f, 0.28f, 1f);
                }

                var textBrightness = enabled ? 0.9f : 0.45f;
                DrawText(displayLabel, x + ButtonPaddingH, textY, ToolbarFontSize, textBrightness, textBrightness, textBrightness);

                if (enabled)
                {
                    RegisterClickable(x, btnY, btnW, btnH, new HitResult.ButtonHit(action.ToString()));
                }

                x += btnW + ButtonSpacing;
            }
        }

        private bool IsToolbarButtonEnabled(ToolbarAction action, AstroImageDocument? document) => action switch
        {
            ToolbarAction.Debayer => document?.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB,
            ToolbarAction.Channel => document is not null && document.UnstretchedImage.ChannelCount > 1,
            ToolbarAction.CurvesBoost => document?.Stars is { Count: > 0 },
            ToolbarAction.Hdr => document is not null,
            ToolbarAction.StretchToggle => document is not null,
            ToolbarAction.StretchLink or ToolbarAction.StretchParams => document is not null,
            ToolbarAction.Grid => document?.Wcs is { HasCDMatrix: true },
            ToolbarAction.Overlays => document?.Wcs is { HasCDMatrix: true } && CelestialObjectDB?.IsValueCreated == true,
            ToolbarAction.Stars => document?.Stars is { Count: > 0 },
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
                ToolbarAction.Debayer => document?.DebayerAlgorithm == state.DebayerAlgorithm
                    && state.DebayerAlgorithm is not DebayerAlgorithm.None,
                ToolbarAction.CurvesBoost => state.CurvesBoost > 0f,
                ToolbarAction.Hdr => state.HdrAmount > 0f,
                ToolbarAction.Grid => state.ShowGrid,
                ToolbarAction.Overlays => state.ShowOverlays,
                ToolbarAction.Stars => state.ShowStarOverlay,
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

            FillRect(0, listTop, FileListWidth, listHeight, 0.13f, 0.13f, 0.15f, 0.95f);

            if (_fontPath is null)
            {
                return;
            }

            var y = (float)listTop + PanelPadding;
            DrawText("Files", PanelPadding, y, FontSize, 0.6f, 0.8f, 1f);
            y += FontSize + 4f;

            FillRect(PanelPadding, y, FileListWidth - PanelPadding * 2, 1, 0.3f, 0.3f, 0.35f, 1f);
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
                var isHovered = mouseX >= 0 && mouseX < FileListWidth
                    && mouseY >= itemY && mouseY < itemY + itemHeight;

                if (isSelected)
                {
                    FillRect(2, itemY, FileListWidth - 4, itemHeight, 0.25f, 0.35f, 0.55f, 1f);
                }
                else if (isHovered)
                {
                    FillRect(2, itemY, FileListWidth - 4, itemHeight, 0.22f, 0.22f, 0.28f, 1f);
                }

                var maxChars = (int)((FileListWidth - PanelPadding * 2) / (FontSize * 0.6f));
                var displayName = fileName.Length > maxChars ? fileName[..(maxChars - 2)] + ".." : fileName;

                var textColor = isSelected ? (R: 1f, G: 1f, B: 1f) : (R: 0.8f, G: 0.8f, B: 0.8f);
                DrawText(displayName, PanelPadding, itemY + 2f, FontSize, textColor.R, textColor.G, textColor.B);

                RegisterClickable(0, itemY, FileListWidth, itemHeight, new HitResult.ListItemHit("FileList", fileIndex));
            }

            if (state.ImageFileNames.Count > visibleCount)
            {
                var scrollFraction = (float)state.FileListScrollOffset / Math.Max(1, state.ImageFileNames.Count - visibleCount);
                var scrollBarH = Math.Max(20f, listHeight * visibleCount / state.ImageFileNames.Count);
                var scrollBarY = listTop + scrollFraction * (listHeight - scrollBarH);
                FillRect(FileListWidth - 4, scrollBarY, 3, scrollBarH, 0.4f, 0.4f, 0.45f, 0.8f);
            }
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

            var fileListW = state.ShowFileList ? FileListWidth : 0;
            var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
            var areaW = (float)(Width - fileListW - panelW);
            var areaH = (float)(Height - ToolbarHeight - StatusBarHeight);
            var scale = state.Zoom;
            var drawW = ImageWidth * scale;
            var drawH = ImageHeight * scale;
            var imgOffsetX = fileListW + (areaW - drawW) / 2f + state.PanOffset.X;
            var imgOffsetY = ToolbarHeight + (areaH - drawH) / 2f + state.PanOffset.Y;

            // Visible image pixel bounds (1-based FITS coordinates)
            var visLeft = Math.Max(1.0, (fileListW - imgOffsetX) / scale + 1);
            var visRight = Math.Min((double)ImageWidth, (fileListW + areaW - imgOffsetX) / scale + 1);
            var visTop = Math.Max(1.0, (ToolbarHeight - imgOffsetY) / scale + 1);
            var visBottom = Math.Min((double)ImageHeight, (ToolbarHeight + areaH - imgOffsetY) / scale + 1);

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
            var viewImagePixels = MathF.Min(areaW, areaH) / scale;
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
                DrawText(label, labelX, labelY, labelSize, 0.0f, 0.85f, 0.0f);
            }
            else
            {
                var labelX = isFirstEdge ? lx + labelPad : lx - MeasureText(label, labelSize) - labelPad;
                var labelY = isFirstEdge ? ly + lineOffset : ly - labelSize - lineOffset;
                DrawText(label, labelX, labelY, labelSize, 0.0f, 0.85f, 0.0f);
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
            var fileListW = state.ShowFileList ? FileListWidth : 0;
            var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
            var areaW = (float)(Width - fileListW - panelW);
            var areaH = (float)(Height - ToolbarHeight - StatusBarHeight);

            var drawW = ImageWidth * state.Zoom;
            var drawH = ImageHeight * state.Zoom;
            var offsetX = fileListW + (areaW - drawW) / 2f + state.PanOffset.X;
            var offsetY = ToolbarHeight + (areaH - drawH) / 2f + state.PanOffset.Y;

            var clipLeft = fileListW;
            var clipTop = (float)ToolbarHeight;
            var clipRight = fileListW + areaW;
            var clipBottom = ToolbarHeight + areaH;

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

            var fileListW = state.ShowFileList ? FileListWidth : 0;
            var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
            var areaW = (float)(Width - fileListW - panelW);
            var areaH = (float)(Height - ToolbarHeight - StatusBarHeight);

            var layout = new ViewportLayout(
                WindowWidth: Width,
                WindowHeight: Height,
                ImageWidth: ImageWidth,
                ImageHeight: ImageHeight,
                Zoom: state.Zoom,
                PanOffset: state.PanOffset,
                AreaLeft: fileListW,
                AreaTop: ToolbarHeight,
                AreaWidth: areaW,
                AreaHeight: areaH,
                DpiScale: DpiScale
            );

            var items = OverlayEngine.ComputeOverlays(layout, wcs, db, MeasureText, BaseFontSize);
            if (items.Count == 0)
            {
                return;
            }

            var labelSize = FontSize * 0.85f;
            var labelPad = 4f;
            var placedLabels = new List<(float X, float Y, float W, float H)>();
            var labelCount = 0;

            foreach (var item in items)
            {
                var (r, g, b) = item.Color;
                var cx = item.ScreenX;
                var cy = item.ScreenY;

                // Draw marker
                switch (item.Marker)
                {
                    case OverlayMarker.Ellipse ellipse:
                        DrawEllipseOverlay(cx, cy, ellipse.SemiMajorPx, ellipse.SemiMinorPx, ellipse.AngleRad,
                            FloatToColor(r, g, b, 1.0f), 1.5f);
                        break;
                    case OverlayMarker.Cross cross:
                        DrawCrossOverlay(cx, cy, cross.ArmPx,
                            FloatToColor(r, g, b, 1.0f));
                        break;
                    case OverlayMarker.Circle circle:
                        DrawEllipseOverlay(cx, cy, circle.RadiusPx, circle.RadiusPx, 0f,
                            FloatToColor(r, g, b, 0.9f), 1.5f);
                        break;
                }

                // Place labels with collision avoidance
                if (labelCount < OverlayEngine.MaxOverlayLabels && item.LabelLines.Count > 0)
                {
                    var maxLineW = 0f;
                    foreach (var line in item.LabelLines)
                    {
                        var w = MeasureText(line, labelSize);
                        if (w > maxLineW) maxLineW = w;
                    }
                    var lineH = labelSize * 1.2f;
                    var totalH = lineH * item.LabelLines.Count;

                    (float X, float Y)[] positions =
                    [
                        (cx + labelPad + 6f, cy - totalH / 2f),
                        (cx - maxLineW - labelPad - 6f, cy - totalH / 2f),
                        (cx - maxLineW / 2f, cy - totalH - labelPad - 6f),
                        (cx - maxLineW / 2f, cy + labelPad + 6f),
                    ];

                    var placed = false;
                    foreach (var (lx, ly) in positions)
                    {
                        var overlaps = false;
                        foreach (var (px, py, pw, ph) in placedLabels)
                        {
                            if (lx < px + pw && lx + maxLineW > px && ly < py + ph && ly + totalH > py)
                            {
                                overlaps = true;
                                break;
                            }
                        }

                        if (!overlaps)
                        {
                            DrawOverlayLabelLines(item.LabelLines, lx, ly, lineH, labelSize, r, g, b);
                            placedLabels.Add((lx, ly, maxLineW, totalH));
                            placed = true;
                            labelCount++;
                            break;
                        }
                    }

                    if (!placed && item.ForcePlaceLabel)
                    {
                        var (fx, fy) = positions[0];
                        DrawOverlayLabelLines(item.LabelLines, fx, fy, lineH, labelSize, r, g, b);
                        placedLabels.Add((fx, fy, maxLineW, totalH));
                        labelCount++;
                    }
                }
            }
        }

        private void DrawOverlayLabelLines(IReadOnlyList<string> lines, float x, float y, float lineH, float fontSize, float r, float g, float b)
        {
            for (int li = 0; li < lines.Count; li++)
            {
                var alpha = li == 0 ? 1.0f : 0.7f;
                DrawText(lines[li], x, y + li * lineH, fontSize, r * alpha, g * alpha, b * alpha);
            }
        }

        private static RGBAColor32 FloatToColor(float r, float g, float b, float a)
            => new RGBAColor32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), (byte)(a * 255));

        // -----------------------------------------------------------------------
        // Histogram overlay
        // -----------------------------------------------------------------------

        private (float Left, float Top, float Width, float Height) GetHistogramRect(ViewerState state)
        {
            var histW = BaseHistogramWidth * DpiScale;
            var histH = BaseHistogramHeight * DpiScale;
            var margin = BaseHistogramMargin * DpiScale;
            var rightEdge = state.ShowInfoPanel ? Width - InfoPanelWidth : (float)Width;
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

        private void RenderHistogram(AstroImageDocument document, ViewerState state)
        {
            if (GetHistogramDisplay() is not { ChannelCount: > 0 } histogramDisplay)
            {
                return;
            }

            var stretch = document.ComputeStretchUniforms(state.StretchMode, state.StretchParameters);

            var (histLeft, histTop, histW, histH) = GetHistogramRect(state);

            // Semi-transparent background
            FillRect(histLeft, histTop, histW, histH, 0f, 0f, 0f, 0.6f);

            RenderHistogramQuad(stretch, histogramDisplay, state,
                histLeft, histTop, histLeft + histW, histTop + histH, Width, Height);

            // Draw LOG button in upper-right corner of histogram
            if (_fontPath is not null)
            {
                var (bx, by, bw, bh) = GetHistogramLogButtonRect(state);
                var mouseX = state.MouseScreenPosition.X;
                var mouseY = state.MouseScreenPosition.Y;
                var hovered = mouseX >= bx && mouseX < bx + bw && mouseY >= by && mouseY < by + bh;

                if (state.HistogramLogScale)
                {
                    FillRect(bx, by, bw, bh, hovered ? 0.25f : 0.20f, hovered ? 0.35f : 0.30f, hovered ? 0.55f : 0.50f, 0.9f);
                }
                else
                {
                    FillRect(bx, by, bw, bh, hovered ? 0.35f : 0.25f, hovered ? 0.35f : 0.25f, hovered ? 0.40f : 0.28f, 0.9f);
                }

                var textY = by + (bh - ToolbarFontSize) / 2f;
                DrawText("LOG", bx + ButtonPaddingH / 2f, textY, ToolbarFontSize, 0.9f, 0.9f, 0.9f);

                RegisterClickable(bx, by, bw, bh, new HitResult.ButtonHit("HistogramLog"),
                    _ => { state.HistogramLogScale = !state.HistogramLogScale; });
            }
        }

        // -----------------------------------------------------------------------
        // Info panel
        // -----------------------------------------------------------------------

        private void RenderInfoPanel(AstroImageDocument document, ViewerState state)
        {
            if (_fontPath is null)
            {
                return;
            }

            var panelLeft = Width - InfoPanelWidth;
            FillRect(panelLeft, ToolbarHeight, InfoPanelWidth, Height - ToolbarHeight - StatusBarHeight, 0.15f, 0.15f, 0.15f, 0.85f);

            var y = (float)ToolbarHeight + PanelPadding;
            var x = panelLeft + PanelPadding;

            var maxTextWidth = InfoPanelWidth - PanelPadding * 2;

            DrawTextLine(ref y, x, "-- Metadata --", 0.6f, 0.8f, 1f);
            foreach (var line in InfoPanelData.GetMetadataLines(document))
            {
                DrawWrappedTextLine(ref y, x, line, maxTextWidth, 0.9f, 0.9f, 0.9f);
            }

            y += FontSize;

            DrawTextLine(ref y, x, "-- Statistics --", 0.6f, 0.8f, 1f);
            foreach (var line in InfoPanelData.GetStatisticsLines(document))
            {
                DrawTextLine(ref y, x, line, 0.9f, 0.9f, 0.9f);
            }

            if (state.CursorPixelInfo is not null)
            {
                y += FontSize;
                DrawTextLine(ref y, x, "-- Cursor --", 0.6f, 0.8f, 1f);
                foreach (var line in InfoPanelData.GetCursorLines(state))
                {
                    DrawTextLine(ref y, x, line, 0.9f, 0.9f, 0.9f);
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
                        isHeader ? 0.6f : 0.7f,
                        isHeader ? 0.8f : 0.7f,
                        isHeader ? 1f : 0.7f);
                }
                if (clipped)
                {
                    DrawTextLine(ref y, x, "...", 0.5f, 0.5f, 0.5f);
                }
            }
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
            FillRect(0, barY, Width, StatusBarHeight, 0.2f, 0.2f, 0.2f, 0.95f);

            var x = PanelPadding;
            var y = barY + 4f;

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
            DrawText(statusText, x, y, FontSize, 0.8f, 0.8f, 0.8f);
        }

        // -----------------------------------------------------------------------
        // Text helpers
        // -----------------------------------------------------------------------

        private void DrawTextLine(ref float y, float x, string text, float r, float g, float b)
        {
            DrawText(text, x, y, FontSize, r, g, b);
            y += FontSize + 2f;
        }

        private void DrawWrappedTextLine(ref float y, float x, string text, float maxWidth, float r, float g, float b)
        {
            var textWidth = MeasureText(text, FontSize);
            if (textWidth <= maxWidth)
            {
                DrawTextLine(ref y, x, text, r, g, b);
                return;
            }

            var colonIdx = text.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx < 0)
            {
                DrawTextLine(ref y, x, text, r, g, b);
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
                    DrawTextLine(ref y, x, lineText, r, g, b);
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

                DrawTextLine(ref y, x, prefix + remaining[..fit], r, g, b);
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
        /// Draws text at the given screen position using float color components.
        /// </summary>
        protected void DrawText(string text, float screenX, float screenY, float fontSize, float r, float g, float b)
        {
            if (_fontPath is null)
            {
                return;
            }

            var color = new RGBAColor32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255);
            var lh = (int)(fontSize * 1.3f);
            var rect = new RectInt(
                new PointInt((int)(screenX + Width), (int)screenY + lh),
                new PointInt((int)screenX, (int)screenY));
            Renderer.DrawText(text.AsSpan(), _fontPath, fontSize, color, rect, TextAlign.Near, TextAlign.Near);
        }

        /// <summary>
        /// Fills a rectangle using float color components.
        /// </summary>
        protected void FillRect(float x, float y, float w, float h, float r, float g, float b, float a)
        {
            var ix = (int)x;
            var iy = (int)y;
            var iw = (int)w;
            var ih = (int)h;
            Renderer.FillRectangle(
                new RectInt(new PointInt(ix + iw, iy + ih), new PointInt(ix, iy)),
                new RGBAColor32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), (byte)(a * 255)));
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

            var ctrl = (modifiers & InputModifier.Ctrl) != 0;
            var shift = (modifiers & InputModifier.Shift) != 0;

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
                    ViewerActions.CycleCurvesBoost(state);
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
                return true;
            }

            if (hit is HitResult.ListItemHit { ListId: "FileList", Index: var fileIndex })
            {
                ViewerActions.SelectFile(state, fileIndex);
                return true;
            }

            if (hit is not null)
            {
                return true; // OnClick already handled it (e.g. HistogramLog)
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

            // Panning always needs a redraw (image position changes)
            if (state.IsPanning)
            {
                ViewerActions.UpdatePan(state, px, py);
                return true;
            }

            // Only redraw when cursor moves to a different image pixel
            var prevPos = state.CursorImagePosition;
            var fileListW = state.ShowFileList ? ScaledFileListWidth : 0;
            var toolbarH = ScaledToolbarHeight;
            var (areaW, areaH) = GetImageAreaSize(state);
            ViewerActions.UpdateCursorFromScreenPosition(_document, state, px, py, fileListW, toolbarH, areaW, areaH);
            return state.CursorImagePosition != prevPos;
        }

        private bool HandleViewerMouseUp()
        {
            if (_state is { } state)
            {
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

            // Zoom: inside the image viewport
            var fileListW = state.ShowFileList ? ScaledFileListWidth : 0;
            var toolbarH = ScaledToolbarHeight;
            var (areaW, areaH) = GetImageAreaSize(state);
            var inImageViewport = mouseX >= fileListW && mouseX < fileListW + areaW
                               && mouseY >= toolbarH && mouseY < toolbarH + areaH;

            if (inImageViewport)
            {
                var zoomFactor = scrollY > 0 ? 1.15f : 1f / 1.15f;
                var oldZoom = state.Zoom;
                var newZoom = MathF.Max(0.01f, oldZoom * zoomFactor);

                var cx = mouseX - fileListW - areaW / 2f - state.PanOffset.X;
                var cy = mouseY - toolbarH - areaH / 2f - state.PanOffset.Y;

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
