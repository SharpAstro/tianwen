using System;
using System.Threading.Tasks;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;
using Vortice.Vulkan;

namespace TianWen.UI.Shared;

/// <summary>
/// Vulkan renderer for the FITS viewer. Renders the image via <see cref="VkFitsImagePipeline"/>
/// and overlays UI panels using <see cref="VkRenderer"/>.
/// </summary>
public sealed class VkImageRenderer : PixelWidgetBase<VulkanContext>, IDisposable
{
    private readonly VkRenderer _renderer;
    private readonly VkFitsImagePipeline _fitsPipeline;

    private HistogramDisplay? _histogramDisplay;
    private StretchMode? _histogramLastStretchMode;
    private float _histogramLastNormFactor;

    private uint _width;
    private uint _height;
    private int _imageWidth;
    private int _imageHeight;

    private string? _fontPath;

    public uint Width => _width;
    public uint Height => _height;

    public int ChannelTextureCount { get; set; } = 0;

    /// <summary>Reference to the viewer state from the last Render call.</summary>
    private ViewerState? _state;

    /// <summary>Reference to the document from the last Render call.</summary>
    private AstroImageDocument? _document;

    /// <summary>Callback for plate solving (needs DI). Set by the host.</summary>
    public Func<Task>? OnPlateSolve { get; set; }

    /// <summary>Callback for app exit. Set by the host.</summary>
    public Action? OnExit { get; set; }

    /// <summary>Callback for fullscreen toggle. Set by the host.</summary>
    public Action? OnToggleFullscreen { get; set; }

    /// <summary>
    /// DPI scale factor. Set from framebuffer size / window size ratio.
    /// </summary>
    public float DpiScale { get; set; } = 1f;

    // Base layout constants (at 1x scale)
    private const float BaseInfoPanelWidth = 300f;
    private const float BaseStatusBarHeight = 24f;
    private const float BaseToolbarHeight = 40f;
    private const float BaseFileListWidth = 300f;
    private const float BaseFontSize = 18f;
    private const float BaseToolbarFontSize = 18f;
    private const float BasePanelPadding = 6f;
    private const float BaseButtonPaddingH = 12f;
    private const float BaseButtonSpacing = 4f;
    private const float BaseButtonGroupSpacing = 14f;

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

    // Toolbar button definitions (label, action, group)
    // Groups are separated by extra spacing.
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

    /// <summary>Scaled toolbar height in pixels.</summary>
    public float ScaledToolbarHeight => ToolbarHeight;

    /// <summary>Scaled status bar height in pixels.</summary>
    public float ScaledStatusBarHeight => StatusBarHeight;

    /// <summary>Scaled file list width in pixels.</summary>
    public float ScaledFileListWidth => FileListWidth;

    /// <summary>Scaled info panel width in pixels.</summary>
    public float ScaledInfoPanelWidth => InfoPanelWidth;

    /// <summary>
    /// Returns the image area dimensions (excluding toolbar, sidebar, info panel, status bar).
    /// </summary>
    public (float Width, float Height) GetImageAreaSize(ViewerState state)
    {
        var fileListW = state.ShowFileList ? FileListWidth : 0;
        var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
        var areaW = (float)(_width - fileListW - panelW);
        var areaH = (float)(_height - ToolbarHeight - StatusBarHeight);
        return (areaW, areaH);
    }

    /// <summary>
    /// Lazy-initialized celestial object database used for object overlays.
    /// </summary>
    public DotNext.Threading.AsyncLazy<ICelestialObjectDB>? CelestialObjectDB { get; set; }

    public VkImageRenderer(VkRenderer renderer, uint width, uint height) : base(renderer)
    {
        _renderer = renderer;
        _width = width;
        _height = height;
        _fitsPipeline = new VkFitsImagePipeline(renderer.Surface);
        ResolveFontPath();
    }

    public void Resize(uint width, uint height)
    {
        _width = width;
        _height = height;
        _renderer.Resize(width, height);
    }

    /// <summary>
    /// Uploads per-channel R32f textures. Channels are stored as flat float arrays (height * width).
    /// </summary>
    public void UploadChannelTexture(ReadOnlySpan<float> data, int channel, int imageWidth, int imageHeight)
    {
        _imageWidth = imageWidth;
        _imageHeight = imageHeight;
        _fitsPipeline.UploadChannelTexture(data, channel, imageWidth, imageHeight);
    }

    /// <summary>
    /// Uploads per-channel histogram data as 1D R32F textures for the histogram shader.
    /// Call once when a new document is loaded.
    /// </summary>
    public void UploadHistogramData(AstroImageDocument document)
    {
        _histogramDisplay = new HistogramDisplay(document.ChannelStatistics);
        _histogramLastStretchMode = null; // force re-upload on next render
    }

    /// <summary>
    /// Recomputes display bins via <see cref="HistogramDisplay"/> and uploads to Vulkan textures.
    /// </summary>
    private void UpdateHistogramTextures(StretchUniforms stretch)
    {
        if (_histogramDisplay is null) return;

        _histogramLastStretchMode = stretch.Mode;
        _histogramLastNormFactor = stretch.NormFactor;

        _histogramDisplay.Recompute(
            stretch.Mode, stretch.NormFactor,
            stretch.Pedestal, stretch.Shadows, stretch.Midtones, stretch.Rescale);

        for (var c = 0; c < _histogramDisplay.ChannelCount; c++)
        {
            _fitsPipeline.UploadHistogramTexture(_histogramDisplay.GetDisplayBins(c), c);
        }
    }

    public void Render(AstroImageDocument? document, ViewerState state)
    {
        _state = state;
        _document = document;
        BeginFrame();

        // Draw image FIRST so UI chrome paints on top of it
        if (_imageWidth > 0 && _imageHeight > 0)
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

    // --- Toolbar ---

    private void RenderToolbar(AstroImageDocument? document, ViewerState state)
    {
        // Background
        FillRect(0, 0, _width, ToolbarHeight, 0.18f, 0.18f, 0.20f, 1f);

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

            // Extra spacing between groups
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

            // Hover detection (only for enabled buttons)
            var hovered = enabled && mouseX >= x && mouseX < x + btnW && mouseY >= btnY && mouseY < btnY + btnH;

            // Button background
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

            // Button text (dimmed when disabled)
            var textBrightness = enabled ? 0.9f : 0.45f;
            DrawText(displayLabel, x + ButtonPaddingH, textY, ToolbarFontSize, textBrightness, textBrightness, textBrightness);

            // Register clickable region for this button
            if (enabled)
            {
                var capturedAction = action;
                RegisterClickable(x, btnY, btnW, btnH, new HitResult.ButtonHit(action.ToString()));
            }

            x += btnW + ButtonSpacing;
        }
    }

    private bool IsToolbarButtonEnabled(ToolbarAction action, AstroImageDocument? document) => action switch
    {
        // Debayer only makes sense for Bayer sensors (e.g. RGGB)
        ToolbarAction.Debayer => document?.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB,
        // Channel cycling only useful when there are multiple channels (after debayer or native color)
        ToolbarAction.Channel => document is not null && document.UnstretchedImage.ChannelCount > 1,
        ToolbarAction.CurvesBoost => document?.Stars is { Count: > 0 },
        ToolbarAction.Hdr => document is not null,
        // Stretch buttons need a loaded document; link/params only when stretch is active
        ToolbarAction.StretchToggle => document is not null,
        ToolbarAction.StretchLink or ToolbarAction.StretchParams => document is not null,
        // Grid needs a WCS with CD matrix (solved or approximate)
        ToolbarAction.Grid => document?.Wcs is { HasCDMatrix: true },
        // Overlays also need the DB to be initialized
        ToolbarAction.Overlays => document?.Wcs is { HasCDMatrix: true } && CelestialObjectDB?.IsValueCreated == true,
        // Stars overlay needs detected stars
        ToolbarAction.Stars => document?.Stars is { Count: > 0 },
        // Plate solve needs a loaded, unsolved document
        ToolbarAction.PlateSolve => document is not null && !document.IsPlateSolved,
        // Zoom needs a loaded document
        ToolbarAction.ZoomFit or ToolbarAction.ZoomActual
            => document is not null,
        // Open is always enabled
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

    // --- File list sidebar ---

    private void RenderFileList(ViewerState state)
    {
        var listTop = ToolbarHeight;
        var listHeight = _height - ToolbarHeight - StatusBarHeight;

        // Background
        FillRect(0, listTop, FileListWidth, listHeight, 0.13f, 0.13f, 0.15f, 0.95f);

        if (_fontPath is null)
        {
            return;
        }

        // Header
        var y = (float)listTop + PanelPadding;
        DrawText("Files", PanelPadding, y, FontSize, 0.6f, 0.8f, 1f);
        y += FontSize + 4f;

        // Separator line
        FillRect(PanelPadding, y, FileListWidth - PanelPadding * 2, 1, 0.3f, 0.3f, 0.35f, 1f);
        y += 3f;

        // File entries
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

            // Truncate filename if too wide
            var maxChars = (int)((FileListWidth - PanelPadding * 2) / (FontSize * 0.6f));
            var displayName = fileName.Length > maxChars ? fileName[..(maxChars - 2)] + ".." : fileName;

            var textColor = isSelected ? (R: 1f, G: 1f, B: 1f) : (R: 0.8f, G: 0.8f, B: 0.8f);
            DrawText(displayName, PanelPadding, itemY + 2f, FontSize, textColor.R, textColor.G, textColor.B);

            RegisterClickable(0, itemY, FileListWidth, itemHeight, new HitResult.ListItemHit("FileList", fileIndex));
        }

        // Scroll indicator
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
        var headerOffset = PanelPadding + FontSize + 4f + 3f; // header + separator
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

    // --- Image ---

    private void RenderImage(AstroImageDocument? document, ViewerState state, StretchUniforms stretch, WCS? gridWcs = null)
    {
        var fileListW = state.ShowFileList ? FileListWidth : 0;
        var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
        var areaW = (float)(_width - fileListW - panelW);
        var areaH = (float)(_height - ToolbarHeight - StatusBarHeight);

        // Compute the fit scale (scale that makes the image fit the viewport)
        var fitScale = MathF.Min(areaW / _imageWidth, areaH / _imageHeight);

        // In ZoomToFit mode, update state.Zoom to match the fit scale
        if (state.ZoomToFit)
        {
            state.Zoom = fitScale;
        }

        var scale = state.Zoom;

        var drawW = _imageWidth * scale;
        var drawH = _imageHeight * scale;
        var offsetX = fileListW + (areaW - drawW) / 2f + state.PanOffset.X;
        var offsetY = ToolbarHeight + (areaH - drawH) / 2f + state.PanOffset.Y;

        var bgLevel = document is not null
            ? stretch.ComputePostStretchBackground(document.PerChannelBackground, document.LumaBackground)
            : 0.15f;

        // WCS grid parameters
        bool gridEnabled = gridWcs is not null;
        float gridSpacingRA = 0f, gridSpacingDec = 0f, gridLineWidth = 0f;
        float crPix1 = 0f, crPix2 = 0f, crValRA = 0f, crValDec = 0f;
        ReadOnlySpan<float> cdMatrix = ReadOnlySpan<float>.Empty;
        float[] cdMatrixArr = new float[4];

        if (gridWcs is { } gw)
        {
            var pixelScaleArcsec = gw.PixelScaleArcsec;
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

            var spacingRad = (float)(spacingArcsec / 3600.0 * (Math.PI / 180.0));
            var spacingRArad = (float)(spacingArcsec / 3600.0 / 15.0 * (Math.PI / 12.0));
            gridSpacingRA = spacingRArad;
            gridSpacingDec = spacingRad;
            gridLineWidth = (float)(1.5 * pixelScaleArcsec / scale / 3600.0 * (Math.PI / 180.0));

            crPix1 = (float)gw.CRPix1;
            crPix2 = (float)gw.CRPix2;
            crValRA = (float)(gw.CenterRA * (Math.PI / 12.0));
            crValDec = (float)(gw.CenterDec * (Math.PI / 180.0));

            var degToRad = (float)(Math.PI / 180.0);
            cdMatrixArr[0] = (float)gw.CD1_1 * degToRad;
            cdMatrixArr[1] = (float)gw.CD2_1 * degToRad;
            cdMatrixArr[2] = (float)gw.CD1_2 * degToRad;
            cdMatrixArr[3] = (float)gw.CD2_2 * degToRad;
            cdMatrix = cdMatrixArr;
        }

        var cmd = _renderer.CurrentCommandBuffer;

        _fitsPipeline.UpdateStretchUBO(
            cmd,
            channelCount: ChannelTextureCount,
            stretchMode: (int)stretch.Mode,
            normFactor: stretch.NormFactor,
            curvesBoost: state.CurvesBoost,
            curvesMidpoint: bgLevel,
            hdrAmount: state.HdrAmount,
            hdrKnee: state.HdrKnee,
            pedestal: (stretch.Pedestal.R, stretch.Pedestal.G, stretch.Pedestal.B),
            shadows: (stretch.Shadows.R, stretch.Shadows.G, stretch.Shadows.B),
            midtones: (stretch.Midtones.R, stretch.Midtones.G, stretch.Midtones.B),
            highlights: (stretch.Highlights.R, stretch.Highlights.G, stretch.Highlights.B),
            rescale: (stretch.Rescale.R, stretch.Rescale.G, stretch.Rescale.B),
            gridEnabled: gridEnabled,
            gridSpacingRA: gridSpacingRA,
            gridSpacingDec: gridSpacingDec,
            gridLineWidth: gridLineWidth,
            imageW: _imageWidth,
            imageH: _imageHeight,
            crPix1: crPix1,
            crPix2: crPix2,
            crValRA: crValRA,
            crValDec: crValDec,
            cdMatrix: cdMatrix);

        _fitsPipeline.RecordImageDraw(
            cmd,
            _renderer.Surface,
            left: offsetX,
            top: offsetY,
            right: offsetX + drawW,
            bottom: offsetY + drawH,
            projW: _width,
            projH: _height);
    }

    // --- WCS Grid ---

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
        if (_fontPath is null || _imageWidth <= 0 || _imageHeight <= 0)
        {
            return;
        }

        var fileListW = state.ShowFileList ? FileListWidth : 0;
        var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
        var areaW = (float)(_width - fileListW - panelW);
        var areaH = (float)(_height - ToolbarHeight - StatusBarHeight);
        var scale = state.Zoom;
        var drawW = _imageWidth * scale;
        var drawH = _imageHeight * scale;
        var imgOffsetX = fileListW + (areaW - drawW) / 2f + state.PanOffset.X;
        var imgOffsetY = ToolbarHeight + (areaH - drawH) / 2f + state.PanOffset.Y;

        // Visible image pixel bounds (1-based FITS coordinates)
        var visLeft = Math.Max(1.0, (fileListW - imgOffsetX) / scale + 1);
        var visRight = Math.Min((double)_imageWidth, (fileListW + areaW - imgOffsetX) / scale + 1);
        var visTop = Math.Max(1.0, (ToolbarHeight - imgOffsetY) / scale + 1);
        var visBottom = Math.Min((double)_imageHeight, (ToolbarHeight + areaH - imgOffsetY) / scale + 1);

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
            // Wrap: shift RAs < 12h up by 24h, recompute range
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

        // Determine which edges get RA labels vs Dec labels from the CD matrix.
        var raOnHorizEdges = Math.Abs(wcs.CD1_1) > Math.Abs(wcs.CD1_2);

        // Corner exclusion zone: skip labels near viewport corners to prevent cross-edge overlap
        var cornerMargin = labelSize * 4f;

        var numSamples = 300;

        // Scan along the viewport-visible pixel bounds so labels stay fixed on screen.
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
            // Horizontal edge (top/bottom): labels offset left/right of the grid line
            var labelX = isFirstEdge ? lx + lineOffset : lx - MeasureText(label, labelSize) - lineOffset;
            // Top edge: label just inside (below edge); Bottom edge: just inside (above edge)
            var labelY = isFirstEdge ? ly + labelPad : ly - labelSize - labelPad;
            DrawText(label, labelX, labelY, labelSize, 0.0f, 0.85f, 0.0f);
        }
        else
        {
            // Vertical edge (left/right): labels offset above/below the grid line
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

    // --- Star Overlay ---

    private void RenderStarOverlay(ViewerState state, StarList stars)
    {
        var fileListW = state.ShowFileList ? FileListWidth : 0;
        var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
        var areaW = (float)(_width - fileListW - panelW);
        var areaH = (float)(_height - ToolbarHeight - StatusBarHeight);

        // Image-to-screen transform
        var drawW = _imageWidth * state.Zoom;
        var drawH = _imageHeight * state.Zoom;
        var offsetX = fileListW + (areaW - drawW) / 2f + state.PanOffset.X;
        var offsetY = ToolbarHeight + (areaH - drawH) / 2f + state.PanOffset.Y;

        // Clip to image viewport area
        var clipLeft = fileListW;
        var clipTop = (float)ToolbarHeight;
        var clipRight = fileListW + areaW;
        var clipBottom = ToolbarHeight + areaH;

        foreach (var star in stars)
        {
            // Centroids are 0-based with integer = pixel center; texture maps pixel N
            // from offsetX + N*zoom to offsetX + (N+1)*zoom, so center is at +0.5*zoom
            var cx = offsetX + (star.XCentroid + 0.5f) * state.Zoom;
            var cy = offsetY + (star.YCentroid + 0.5f) * state.Zoom;
            var radius = MathF.Max(star.HFD * 0.5f * state.Zoom, 6f);

            // Skip stars outside the viewport
            if (cx + radius < clipLeft || cx - radius > clipRight ||
                cy + radius < clipTop || cy - radius > clipBottom)
            {
                continue;
            }

            // Alpha scales with zoom: faint when zoomed out (many circles), solid when zoomed in
            var alpha = MathF.Min(1.0f, 0.3f + state.Zoom * 0.7f);
            DrawEllipse(cx, cy, radius, radius, 0f, 0f, 0.8f, 0.2f, alpha, 1.5f);
        }
    }

    // --- Object Overlays ---

    private void RenderOverlays(ViewerState state, WCS wcs, ICelestialObjectDB db)
    {
        if (_fontPath is null || _imageWidth <= 0 || _imageHeight <= 0)
        {
            return;
        }

        var fileListW = state.ShowFileList ? FileListWidth : 0;
        var panelW = state.ShowInfoPanel ? InfoPanelWidth : 0;
        var areaW = (float)(_width - fileListW - panelW);
        var areaH = (float)(_height - ToolbarHeight - StatusBarHeight);

        var layout = new ViewportLayout(
            WindowWidth: _width,
            WindowHeight: _height,
            ImageWidth: _imageWidth,
            ImageHeight: _imageHeight,
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
                    DrawEllipse(cx, cy, ellipse.SemiMajorPx, ellipse.SemiMinorPx, ellipse.AngleRad, r, g, b, 1.0f, thickness: 1.5f);
                    break;
                case OverlayMarker.Cross cross:
                    DrawCross(cx, cy, cross.ArmPx, r, g, b, 1.0f);
                    break;
                case OverlayMarker.Circle circle:
                    DrawEllipse(cx, cy, circle.RadiusPx, circle.RadiusPx, 0f, r, g, b, 0.9f, thickness: 1.5f);
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

                // Try 4 candidate positions: right, left, above, below
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

                // If all positions collide and this is a bright object, force-place right
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

    /// <summary>
    /// Draws an ellipse approximation using FillEllipse.
    /// For the Vulkan renderer, we use a filled semi-transparent ellipse rather than an outline.
    /// </summary>
    private void DrawEllipse(float cx, float cy, float semiMajor, float semiMinor, float angle, float r, float g, float b, float a, float thickness = 0f)
    {
        // Compute axis-aligned bounding box of the rotated ellipse
        var cosA = MathF.Cos(angle);
        var sinA = MathF.Sin(angle);
        var bboxW = MathF.Sqrt(semiMajor * semiMajor * cosA * cosA + semiMinor * semiMinor * sinA * sinA);
        var bboxH = MathF.Sqrt(semiMajor * semiMajor * sinA * sinA + semiMinor * semiMinor * cosA * cosA);

        var color = new RGBAColor32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), (byte)(a * 255));
        var rect = new RectInt(
            new PointInt((int)(cx + bboxW), (int)(cy + bboxH)),
            new PointInt((int)(cx - bboxW), (int)(cy - bboxH)));

        if (thickness > 0f)
        {
            _renderer.DrawEllipseOutline(rect, color, thickness * DpiScale);
        }
        else
        {
            _renderer.FillEllipse(rect, color);
        }
    }

    private void DrawCross(float cx, float cy, float arm, float r, float g, float b, float a)
    {
        var color = new RGBAColor32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), (byte)(a * 255));
        var thickness = Math.Max(1, (int)(DpiScale));

        // Horizontal arm
        var hRect = new RectInt(
            new PointInt((int)(cx + arm), (int)(cy + thickness)),
            new PointInt((int)(cx - arm), (int)(cy - thickness)));
        _renderer.FillRectangle(hRect, color);

        // Vertical arm
        var vRect = new RectInt(
            new PointInt((int)(cx + thickness), (int)(cy + arm)),
            new PointInt((int)(cx - thickness), (int)(cy - arm)));
        _renderer.FillRectangle(vRect, color);
    }

    // --- Histogram overlay ---

    private const float BaseHistogramWidth = 256f;
    private const float BaseHistogramHeight = 128f;
    private const float BaseHistogramMargin = 8f;

    private (float Left, float Top, float Width, float Height) GetHistogramRect(ViewerState state)
    {
        var histW = BaseHistogramWidth * DpiScale;
        var histH = BaseHistogramHeight * DpiScale;
        var margin = BaseHistogramMargin * DpiScale;
        var rightEdge = state.ShowInfoPanel ? _width - InfoPanelWidth : (float)_width;
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
        if (!state.ShowHistogram || _histogramDisplay is not { ChannelCount: > 0 })
        {
            return false;
        }
        var (bx, by, bw, bh) = GetHistogramLogButtonRect(state);
        return screenX >= bx && screenX < bx + bw && screenY >= by && screenY < by + bh;
    }

    private void RenderHistogram(AstroImageDocument document, ViewerState state)
    {
        if (_histogramDisplay is not { ChannelCount: > 0 })
        {
            return;
        }

        // Recompute histogram textures when stretch mode changes
        var stretch = document.ComputeStretchUniforms(state.StretchMode, state.StretchParameters);
        if (stretch.Mode != _histogramLastStretchMode || stretch.NormFactor != _histogramLastNormFactor)
        {
            UpdateHistogramTextures(stretch);
        }

        var (histLeft, histTop, histW, histH) = GetHistogramRect(state);

        // Semi-transparent background
        FillRect(histLeft, histTop, histW, histH, 0f, 0f, 0f, 0.6f);

        var cmd = _renderer.CurrentCommandBuffer;

        _fitsPipeline.UpdateHistogramUBO(
            cmd,
            channelCount: _histogramDisplay.ChannelCount,
            logPeak: _histogramDisplay.LogPeak,
            linearPeak: _histogramDisplay.LinearPeak,
            logScale: state.HistogramLogScale);

        _fitsPipeline.RecordHistogramDraw(
            cmd,
            _renderer.Surface,
            left: histLeft,
            top: histTop,
            right: histLeft + histW,
            bottom: histTop + histH,
            projW: _width,
            projH: _height);

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
                () => { state.HistogramLogScale = !state.HistogramLogScale; });
        }
    }

    // --- Info panel ---

    private void RenderInfoPanel(AstroImageDocument document, ViewerState state)
    {
        if (_fontPath is null)
        {
            return;
        }

        // Draw panel background on right side
        var panelLeft = _width - InfoPanelWidth;
        FillRect(panelLeft, ToolbarHeight, InfoPanelWidth, _height - ToolbarHeight - StatusBarHeight, 0.15f, 0.15f, 0.15f, 0.85f);

        var y = (float)ToolbarHeight + PanelPadding;
        var x = panelLeft + PanelPadding;

        var maxTextWidth = InfoPanelWidth - PanelPadding * 2;

        // Metadata section
        DrawTextLine(ref y, x, "-- Metadata --", 0.6f, 0.8f, 1f);
        foreach (var line in InfoPanelData.GetMetadataLines(document))
        {
            DrawWrappedTextLine(ref y, x, line, maxTextWidth, 0.9f, 0.9f, 0.9f);
        }

        y += FontSize;

        // Statistics section
        DrawTextLine(ref y, x, "-- Statistics --", 0.6f, 0.8f, 1f);
        foreach (var line in InfoPanelData.GetStatisticsLines(document))
        {
            DrawTextLine(ref y, x, line, 0.9f, 0.9f, 0.9f);
        }

        // Cursor section
        if (state.CursorPixelInfo is not null)
        {
            y += FontSize;
            DrawTextLine(ref y, x, "-- Cursor --", 0.6f, 0.8f, 1f);
            foreach (var line in InfoPanelData.GetCursorLines(state))
            {
                DrawTextLine(ref y, x, line, 0.9f, 0.9f, 0.9f);
            }
        }

        // Controls help at bottom of panel — only show lines that fit below content
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
        var availableLines = (int)((_height - StatusBarHeight - PanelPadding - y - lineHeight) / lineHeight);
        if (availableLines >= 2)
        {
            var clipped = availableLines < controlLabels.Length;
            var linesToDraw = clipped ? availableLines - 1 : controlLabels.Length;
            var totalLines = clipped ? linesToDraw + 1 : linesToDraw;
            y = _height - StatusBarHeight - lineHeight * totalLines - PanelPadding;
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

    // --- Status bar ---

    private void RenderStatusBar(AstroImageDocument? document, ViewerState state)
    {
        if (_fontPath is null)
        {
            return;
        }

        var barY = _height - StatusBarHeight;
        FillRect(0, barY, _width, StatusBarHeight, 0.2f, 0.2f, 0.2f, 0.95f);

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

    // --- Text helpers ---

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

        // Find the "Label: " prefix to use as indent for continuation
        var colonIdx = text.IndexOf(": ", StringComparison.Ordinal);
        if (colonIdx < 0)
        {
            // No label, just truncate
            DrawTextLine(ref y, x, text, r, g, b);
            return;
        }

        var label = text[..(colonIdx + 2)];
        var value = text[(colonIdx + 2)..];
        var indent = new string(' ', label.Length);

        // First line: label + as much value as fits
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

            // Find how many chars fit
            var fit = remaining.Length;
            while (fit > 1 && MeasureText(prefix + remaining[..fit], FontSize) > maxWidth)
            {
                fit--;
            }

            // Try to break at a word boundary (after space or hyphen)
            var breakAt = -1;
            for (var i = fit; i > 0; i--)
            {
                if (remaining[i - 1] is ' ' or '-')
                {
                    breakAt = i; // wrap after the space or hyphen
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

    private float MeasureText(string text, float fontSize)
    {
        if (_fontPath is null)
        {
            return text.Length * fontSize * 0.6f;
        }

        return _renderer.MeasureText(text.AsSpan(), _fontPath, fontSize).Width;
    }

    private void DrawText(string text, float screenX, float screenY, float fontSize, float r, float g, float b)
    {
        if (_fontPath is null)
        {
            return;
        }

        var color = new RGBAColor32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255);
        var lh = (int)(fontSize * 1.3f);
        var rect = new RectInt(
            new PointInt((int)(screenX + _width), (int)screenY + lh),
            new PointInt((int)screenX, (int)screenY));
        _renderer.DrawText(text.AsSpan(), _fontPath, fontSize, color, rect, TextAlign.Near, TextAlign.Near);
    }

    private void FillRect(float x, float y, float w, float h, float r, float g, float b, float a)
    {
        var ix = (int)x;
        var iy = (int)y;
        var iw = (int)w;
        var ih = (int)h;
        _renderer.FillRectangle(
            new RectInt(new PointInt(ix + iw, iy + ih), new PointInt(ix, iy)),
            new RGBAColor32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), (byte)(a * 255)));
    }

    private void ResolveFontPath()
    {
        // Try common system monospace fonts
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Windows\Fonts\consola.ttf", @"C:\Windows\Fonts\cour.ttf"]
            : OperatingSystem.IsMacOS()
                ? ["/System/Library/Fonts/Menlo.ttc", "/System/Library/Fonts/Monaco.dfont"]
                : ["/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", "/usr/share/fonts/TTF/DejaVuSansMono.ttf"];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                _fontPath = path;
                return;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Keyboard handling
    // -----------------------------------------------------------------------

    public override bool HandleKeyDown(InputKey key, InputModifier modifiers)
    {
        if (_state is not { } state)
        {
            return false;
        }

        var ctrl = (modifiers & InputModifier.Ctrl) != 0;
        var shift = (modifiers & InputModifier.Shift) != 0;

        // Ctrl+key shortcuts
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
                OnExit?.Invoke();
                return true;
            case InputKey.F11:
                OnToggleFullscreen?.Invoke();
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
                OnPlateSolve?.Invoke();
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
    // Mouse wheel handling
    // -----------------------------------------------------------------------

    public override bool HandleMouseWheel(float scrollY, float mouseX, float mouseY)
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

            // Adjust pan so the point under the cursor stays fixed
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

    public void Dispose()
    {
        _fitsPipeline.Dispose();
    }
}
