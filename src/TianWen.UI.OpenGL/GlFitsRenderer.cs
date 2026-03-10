using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.OpenGL;

/// <summary>
/// OpenGL renderer for the FITS viewer. Renders the image as a textured quad
/// and overlays UI panels using text rendering.
/// </summary>
public sealed class GlFitsRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly GlFontAtlas _fontAtlas;
    private readonly GlShaderProgram _imageShader;
    private readonly GlShaderProgram _flatShader;
    private readonly GlShaderProgram _textShader;

    private readonly uint[] _channelTextures = new uint[3];
    private int _channelTextureCount;
    private uint _vao;
    private uint _vbo;

    private uint _width;
    private uint _height;
    private int _imageWidth;
    private int _imageHeight;

    private string? _fontPath;

    public uint Width => _width;
    public uint Height => _height;

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

    public GlFitsRenderer(GL gl, uint width, uint height)
    {
        _gl = gl;
        _width = width;
        _height = height;

        _fontAtlas = new GlFontAtlas(gl);
        _imageShader = GlShaderProgram.Create(gl, ImageVertexShader, ImageFragmentShader);
        _flatShader = GlShaderProgram.Create(gl, FlatVertexShader, FlatFragmentShader);
        _textShader = GlShaderProgram.Create(gl, TextVertexShader, TextFragmentShader);

        for (int i = 0; i < _channelTextures.Length; i++)
        {
            _channelTextures[i] = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _channelTextures[i]);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        }

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();

        ResolveFontPath();
    }

    public void Resize(uint width, uint height)
    {
        _width = width;
        _height = height;
        _gl.Viewport(0, 0, width, height);
    }

    /// <summary>
    /// Uploads per-channel R32f textures. Channels are stored as flat float arrays (height * width).
    /// </summary>
    public void UploadChannelTextures(ReadOnlySpan<float[]> channels, int imageWidth, int imageHeight)
    {
        _imageWidth = imageWidth;
        _imageHeight = imageHeight;
        _channelTextureCount = channels.Length;

        for (int i = 0; i < channels.Length && i < _channelTextures.Length; i++)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _channelTextures[i]);
            _gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.R32f,
                (uint)imageWidth, (uint)imageHeight, 0,
                PixelFormat.Red, PixelType.Float, channels[i].AsSpan());
        }
    }

    /// <summary>
    /// Lazy-initialized celestial object database used for object overlays.
    /// </summary>
    public AsyncLazy<ICelestialObjectDB>? CelestialObjectDB { get; set; }

    public void Render(FitsDocument? document, ViewerState state)
    {
        _gl.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        RenderToolbar(document, state);

        if (state.ShowFileList)
        {
            RenderFileList(state);
        }

        if (_imageWidth > 0 && _imageHeight > 0)
        {
            var stretch = document?.ComputeStretchUniforms(state.StretchMode, state.StretchParameters)
                ?? new GpuStretchUniforms(0, 1f, default, default, default, default, default);
            var gridWcs = state.ShowGrid && document?.Wcs is { HasCDMatrix: true } w ? w : (WCS?)null;
            RenderImage(state, stretch, gridWcs);
        }

        if (state.ShowGrid && document?.Wcs is { HasCDMatrix: true } wcs)
        {
            RenderGridLabels(state, wcs);
        }

        if (state.ShowOverlays && document?.Wcs is { HasCDMatrix: true } overlayWcs && CelestialObjectDB?.ValueOrDefault is { } db)
        {
            RenderOverlays(state, overlayWcs, db);
        }

        if (state.ShowInfoPanel && document is not null)
        {
            RenderInfoPanel(document, state);
        }

        if (document is not null)
        {
            RenderStatusBar(document, state);
        }

        _fontAtlas.Flush();
    }

    // --- Toolbar ---

    private void RenderToolbar(FitsDocument? document, ViewerState state)
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

            x += btnW + ButtonSpacing;
        }
    }

    private bool IsToolbarButtonEnabled(ToolbarAction action, FitsDocument? document) => action switch
    {
        // Debayer only makes sense for Bayer sensors (e.g. RGGB)
        ToolbarAction.Debayer => document?.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB,
        // Channel cycling only useful when there are multiple channels (after debayer or native color)
        ToolbarAction.Channel => document is not null && document.UnstretchedImage.ChannelCount > 1,
        ToolbarAction.CurvesBoost or ToolbarAction.Hdr => document is not null,
        // Stretch buttons need a loaded document; link/params only when stretch is active
        ToolbarAction.StretchToggle => document is not null,
        ToolbarAction.StretchLink or ToolbarAction.StretchParams => document is not null,
        // Grid needs a WCS with CD matrix (solved or approximate)
        ToolbarAction.Grid => document?.Wcs is { HasCDMatrix: true },
        // Overlays also need the DB to be initialized
        ToolbarAction.Overlays => document?.Wcs is { HasCDMatrix: true } && CelestialObjectDB?.IsReady == true,
        // Plate solve needs a loaded, unsolved document
        ToolbarAction.PlateSolve => document is not null && !document.IsPlateSolved,
        // Zoom needs a loaded document
        ToolbarAction.ZoomFit or ToolbarAction.ZoomActual
            => document is not null,
        // Open is always enabled
        _ => true,
    };

    private bool IsToolbarButtonActive(ToolbarAction action, FitsDocument? document, ViewerState state)
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
            ToolbarAction.ZoomFit => state.ZoomToFit,
            ToolbarAction.ZoomActual => !state.ZoomToFit && MathF.Abs(state.Zoom - 1f) < 0.001f,
            _ => false,
        };
    }

    private string GetToolbarButtonLabel(string baseLabel, ToolbarAction action, FitsDocument? document, ViewerState state)
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
            ToolbarAction.Overlays when CelestialObjectDB is { IsReady: false } => "Objects...",
            ToolbarAction.Overlays => "Objects",
            ToolbarAction.PlateSolve when state.IsPlateSolving => "Solving...",
            ToolbarAction.PlateSolve when document?.IsPlateSolved == true => "Solved",
            _ => baseLabel,
        };
    }

    /// <summary>
    /// Hit-tests the toolbar using actual rendered button widths for the current state.
    /// </summary>
    public ToolbarAction? HitTestToolbar(float screenX, float screenY, FitsDocument? document, ViewerState state)
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

        for (var i = 0; i < visibleCount && i + state.FileListScrollOffset < state.FitsFileNames.Count; i++)
        {
            var fileIndex = i + state.FileListScrollOffset;
            var fileName = state.FitsFileNames[fileIndex];
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
        }

        // Scroll indicator
        if (state.FitsFileNames.Count > visibleCount)
        {
            var scrollFraction = (float)state.FileListScrollOffset / Math.Max(1, state.FitsFileNames.Count - visibleCount);
            var scrollBarH = Math.Max(20f, listHeight * visibleCount / state.FitsFileNames.Count);
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
        if (itemIndex >= 0 && itemIndex < state.FitsFileNames.Count)
        {
            return itemIndex;
        }

        return -1;
    }

    // --- Image ---

    private void RenderImage(ViewerState state, GpuStretchUniforms stretch, WCS? gridWcs = null)
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

        // Convert to NDC (-1..1)
        var left = (offsetX / _width) * 2f - 1f;
        var right = ((offsetX + drawW) / _width) * 2f - 1f;
        var top = 1f - (offsetY / _height) * 2f;
        var bottom = 1f - ((offsetY + drawH) / _height) * 2f;

        ReadOnlySpan<float> vertices =
        [
            // pos x, pos y, tex u, tex v
            left,  top,    0f, 0f,
            right, top,    1f, 0f,
            left,  bottom, 0f, 1f,
            right, top,    1f, 0f,
            right, bottom, 1f, 1f,
            left,  bottom, 0f, 1f,
        ];

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StreamDraw);

        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        _gl.EnableVertexAttribArray(1);

        // Scissor clip to the image viewing area so the image doesn't overlap toolbar/panels
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor((int)fileListW, (int)StatusBarHeight, (uint)areaW, (uint)areaH);

        _imageShader.Use();
        _imageShader.SetInt("uChannelCount", _channelTextureCount);
        _imageShader.SetFloat("uCurvesBoost", state.CurvesBoost);
        _imageShader.SetFloat("uCurvesMidpoint", (float)state.StretchParameters.Factor);
        _imageShader.SetFloat("uHdrAmount", state.HdrAmount);
        _imageShader.SetFloat("uHdrKnee", state.HdrKnee);

        // Stretch uniforms
        _imageShader.SetInt("uStretchMode", stretch.Mode);
        _imageShader.SetFloat("uNormFactor", stretch.NormFactor);
        _imageShader.SetVector3("uPedestal", stretch.Pedestal.R, stretch.Pedestal.G, stretch.Pedestal.B);
        _imageShader.SetVector3("uShadows", stretch.Shadows.R, stretch.Shadows.G, stretch.Shadows.B);
        _imageShader.SetVector3("uMidtones", stretch.Midtones.R, stretch.Midtones.G, stretch.Midtones.B);
        _imageShader.SetVector3("uHighlights", stretch.Highlights.R, stretch.Highlights.G, stretch.Highlights.B);
        _imageShader.SetVector3("uRescale", stretch.Rescale.R, stretch.Rescale.G, stretch.Rescale.B);

        // WCS grid uniforms
        if (gridWcs is { } gw)
        {
            _imageShader.SetInt("uGridEnabled", 1);
            _imageShader.SetVector2("uImageSize", _imageWidth, _imageHeight);
            _imageShader.SetVector2("uCRPix", (float)gw.CRPix1, (float)gw.CRPix2);
            // Convert RA hours→radians, Dec degrees→radians
            var ra0Rad = (float)(gw.CenterRA * (Math.PI / 12.0));
            var dec0Rad = (float)(gw.CenterDec * (Math.PI / 180.0));
            _imageShader.SetVector2("uCRVal", ra0Rad, dec0Rad);
            // CD matrix in radians/pixel (column-major for GLSL mat2)
            var degToRad = (float)(Math.PI / 180.0);
            ReadOnlySpan<float> cdMatrix =
            [
                (float)gw.CD1_1 * degToRad, (float)gw.CD2_1 * degToRad,  // column 0
                (float)gw.CD1_2 * degToRad, (float)gw.CD2_2 * degToRad,  // column 1
            ];
            _imageShader.SetMatrix2("uCDMatrix", cdMatrix);

            // Compute grid spacing
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
            _imageShader.SetFloat("uGridSpacingRA", spacingRArad);
            _imageShader.SetFloat("uGridSpacingDec", spacingRad);
            // Line width: ~1.5 pixels in sky-coordinate space
            _imageShader.SetFloat("uGridLineWidth", (float)(1.5 * pixelScaleArcsec / 3600.0 * (Math.PI / 180.0)));
        }
        else
        {
            _imageShader.SetInt("uGridEnabled", 0);
        }

        for (int i = 0; i < _channelTextureCount && i < _channelTextures.Length; i++)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + i);
            _gl.BindTexture(TextureTarget.Texture2D, _channelTextures[i]);
            _imageShader.SetInt($"uChannel{i}", i);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        _gl.Disable(EnableCap.ScissorTest);
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

        // Scissor to image area
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor((int)fileListW, (int)StatusBarHeight, (uint)areaW, (uint)areaH);

        // Scan each image edge for RA and Dec grid line crossings.
        // Place a label at each crossing, right at the edge of the image.
        var numSamples = 300;

        // Determine which edges get RA labels vs Dec labels from the CD matrix.
        // RA varies more along the axis where |CD1_x| is larger; that axis's
        // perpendicular edges get RA labels. Same logic for Dec with CD2_x.
        var raOnHorizEdges = Math.Abs(wcs.CD1_1) > Math.Abs(wcs.CD1_2); // RA varies more with X → RA lines are vertical → label on horiz edges

        // Corner exclusion zone: skip labels near viewport corners to prevent cross-edge overlap
        var cornerMargin = labelSize * 4f;

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
            // Which side of the edge: top/left = first, bottom/right = second
            var isFirstEdge = isHoriz ? (y0 <= visTop + 1) : (x0 <= visLeft + 1);

            // Screen-space start/end of this edge for corner exclusion
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

                            // Skip labels too close to edge corners to prevent cross-edge overlap
                            var distToStart = MathF.Abs(isHoriz ? lx - edgeStartX : ly - edgeStartY);
                            var distToEnd = MathF.Abs(isHoriz ? lx - edgeEndX : ly - edgeEndY);
                            if (distToStart < cornerMargin || distToEnd < cornerMargin)
                            {
                                continue;
                            }

                            var normalizedRA = gridRA % 24.0;
                            if (normalizedRA < 0) normalizedRA += 24.0;
                            var label = FormatRALabel(normalizedRA, spacingArcsec);
                            PlaceEdgeLabel(label, lx, ly, labelSize, labelPad, isHoriz, isFirstEdge);
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

                            var label = FormatDecLabel(gridDec, spacingArcsec);
                            PlaceEdgeLabel(label, lx, ly, labelSize, labelPad, isHoriz, isFirstEdge);
                        }
                    }
                }

                prevRA = s.RA;
                prevDec = s.Dec;
                prevScreenX = screenX;
                prevScreenY = screenY;
            }
        }

        _gl.Disable(EnableCap.ScissorTest);
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

        // Scissor to image area
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor((int)fileListW, (int)StatusBarHeight, (uint)areaW, (uint)areaH);

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
                    DrawEllipse(cx, cy, ellipse.SemiMajorPx, ellipse.SemiMinorPx, ellipse.AngleRad, r, g, b, 1.0f);
                    break;
                case OverlayMarker.Cross cross:
                    DrawCross(cx, cy, cross.ArmPx, r, g, b, 1.0f);
                    break;
                case OverlayMarker.Circle circle:
                    DrawEllipse(cx, cy, circle.RadiusPx, circle.RadiusPx, 0f, r, g, b, 0.9f);
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

        _gl.Disable(EnableCap.ScissorTest);
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
    /// Draws an ellipse outline using line segments via the flat shader.
    /// </summary>
    private void DrawEllipse(float cx, float cy, float semiMajor, float semiMinor, float angle, float r, float g, float b, float a)
    {
        const int segments = 48;
        var cosA = MathF.Cos(angle);
        var sinA = MathF.Sin(angle);

        // Generate line segment pairs
        Span<float> verts = stackalloc float[segments * 4]; // 2 vertices per segment, 2 floats each
        var prevX = cx + semiMajor * cosA;
        var prevY = cy - semiMajor * sinA; // screen Y is inverted

        for (int i = 0; i < segments; i++)
        {
            var t = (i + 1) * 2f * MathF.PI / segments;
            var ex = semiMajor * MathF.Cos(t);
            var ey = semiMinor * MathF.Sin(t);
            var nextX = cx + ex * cosA - ey * sinA;
            var nextY = cy - (ex * sinA + ey * cosA); // screen Y is inverted

            // Convert to NDC
            verts[i * 4 + 0] = (prevX / _width) * 2f - 1f;
            verts[i * 4 + 1] = 1f - (prevY / _height) * 2f;
            verts[i * 4 + 2] = (nextX / _width) * 2f - 1f;
            verts[i * 4 + 3] = 1f - (nextY / _height) * 2f;

            prevX = nextX;
            prevY = nextY;
        }

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.StreamDraw);

        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        _gl.EnableVertexAttribArray(0);
        _gl.DisableVertexAttribArray(1);

        _flatShader.Use();
        _flatShader.SetVector4("uColor", r, g, b, a);

        _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(segments * 2));
    }

    private void DrawCross(float cx, float cy, float arm, float r, float g, float b, float a)
    {
        // 4 line endpoints: horizontal + vertical
        Span<float> verts = stackalloc float[8];
        // Horizontal line
        verts[0] = ((cx - arm) / _width) * 2f - 1f;
        verts[1] = 1f - (cy / _height) * 2f;
        verts[2] = ((cx + arm) / _width) * 2f - 1f;
        verts[3] = verts[1];
        // Vertical line
        verts[4] = (cx / _width) * 2f - 1f;
        verts[5] = 1f - ((cy - arm) / _height) * 2f;
        verts[6] = verts[4];
        verts[7] = 1f - ((cy + arm) / _height) * 2f;

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.StreamDraw);

        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        _gl.EnableVertexAttribArray(0);
        _gl.DisableVertexAttribArray(1);

        _flatShader.Use();
        _flatShader.SetVector4("uColor", r, g, b, a);

        _gl.DrawArrays(PrimitiveType.Lines, 0, 4);
    }

    // --- Info panel ---

    private void RenderInfoPanel(FitsDocument document, ViewerState state)
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

        // Controls help at bottom of panel
        var lineHeight = FontSize + 2f;
        var controlLines = 16; // header + 15 controls
        y = _height - StatusBarHeight - lineHeight * controlLines - PanelPadding;
        if (y > ToolbarHeight + lineHeight * 5)
        {
            DrawTextLine(ref y, x, "-- Controls --", 0.6f, 0.8f, 1f);
            DrawTextLine(ref y, x, "S: Cycle stretch", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "+/-: Stretch factor", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "C: Cycle channel", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "D: Cycle debayer", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "H: Cycle HDR", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "G: Toggle grid", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "P: Plate solve", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "Ctrl+Wheel: Zoom", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "Ctrl++/-: Zoom in/out", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "Ctrl+0: Zoom to fit", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "Ctrl+1: Zoom 1:1", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "I: Toggle info panel", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "L: Toggle file list", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "F11: Fullscreen", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "Esc: Quit", 0.7f, 0.7f, 0.7f);
        }
    }

    // --- Status bar ---

    private void RenderStatusBar(FitsDocument document, ViewerState state)
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

        if (document.Wcs is { HasCDMatrix: true } wcs)
        {
            var scale = wcs.PixelScaleArcsec;
            var label = wcs.IsApproximate ? "approx" : "solved";
            var ra = CoordinateUtils.HoursToHMS(wcs.CenterRA);
            var dec = CoordinateUtils.DegreesToDMS(wcs.CenterDec);
            statusParts.Add($"WCS: {label} ({scale:F2}\"/px)  RA {ra}  Dec {dec}");
        }

        var zoomPct = state.Zoom * 100f;
        statusParts.Add($"Zoom: {zoomPct:F0}%");

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

        var width = 0f;
        foreach (var ch in text)
        {
            var glyph = _fontAtlas.GetGlyph(_fontPath, fontSize, ch);
            width += glyph.AdvanceX;
        }
        return width;
    }

    private void DrawText(string text, float screenX, float screenY, float fontSize, float r, float g, float b)
    {
        if (_fontPath is null)
        {
            return;
        }

        _textShader.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _fontAtlas.TextureHandle);
        _textShader.SetInt("uTexture", 0);
        _textShader.SetVector4("uColor", r, g, b, 1f);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        var cursorX = screenX;
        foreach (var ch in text)
        {
            var glyph = _fontAtlas.GetGlyph(_fontPath, fontSize, ch);
            if (glyph.Width == 0 && glyph.Height == 0)
            {
                cursorX += glyph.AdvanceX;
                continue;
            }

            var left = (cursorX / _width) * 2f - 1f;
            var right = ((cursorX + glyph.Width) / _width) * 2f - 1f;
            var top = 1f - (screenY / _height) * 2f;
            var bottom = 1f - ((screenY + glyph.Height) / _height) * 2f;

            ReadOnlySpan<float> verts =
            [
                left,  top,    glyph.U0, glyph.V0,
                right, top,    glyph.U1, glyph.V0,
                left,  bottom, glyph.U0, glyph.V1,
                right, top,    glyph.U1, glyph.V0,
                right, bottom, glyph.U1, glyph.V1,
                left,  bottom, glyph.U0, glyph.V1,
            ];

            _gl.BufferData(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.StreamDraw);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

            cursorX += glyph.AdvanceX;
        }
    }

    private void FillRect(float x, float y, float w, float h, float r, float g, float b, float a)
    {
        var left = (x / _width) * 2f - 1f;
        var right = ((x + w) / _width) * 2f - 1f;
        var top = 1f - (y / _height) * 2f;
        var bottom = 1f - ((y + h) / _height) * 2f;

        ReadOnlySpan<float> verts =
        [
            left, top,
            right, top,
            left, bottom,
            right, top,
            right, bottom,
            left, bottom,
        ];

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.StreamDraw);

        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        _gl.EnableVertexAttribArray(0);
        _gl.DisableVertexAttribArray(1);

        _flatShader.Use();
        _flatShader.SetVector4("uColor", r, g, b, a);

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
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

    public void Dispose()
    {
        _imageShader.Dispose();
        _flatShader.Dispose();
        _textShader.Dispose();
        _fontAtlas.Dispose();

        for (int i = 0; i < _channelTextures.Length; i++)
        {
            if (_channelTextures[i] != 0)
            {
                _gl.DeleteTexture(_channelTextures[i]);
                _channelTextures[i] = 0;
            }
        }
        if (_vao != 0)
        {
            _gl.DeleteVertexArray(_vao);
            _vao = 0;
        }
        if (_vbo != 0)
        {
            _gl.DeleteBuffer(_vbo);
            _vbo = 0;
        }
    }

    // --- Shaders ---

    private const string ImageVertexShader = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aTexCoord;
        out vec2 vTexCoord;
        void main() {
            gl_Position = vec4(aPos, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    private const string ImageFragmentShader = """
        #version 330 core
        in vec2 vTexCoord;
        out vec4 FragColor;
        uniform sampler2D uChannel0;
        uniform sampler2D uChannel1;
        uniform sampler2D uChannel2;
        uniform int uChannelCount;
        uniform float uCurvesBoost;
        uniform float uCurvesMidpoint;
        uniform float uHdrAmount;
        uniform float uHdrKnee;

        // Stretch uniforms: 0=none, 1=per-channel (linked/unlinked), 2=luma
        uniform int uStretchMode;
        uniform float uNormFactor;
        uniform vec3 uPedestal;
        uniform vec3 uShadows;
        uniform vec3 uMidtones;
        uniform vec3 uHighlights;
        uniform vec3 uRescale;

        // WCS grid uniforms
        uniform bool uGridEnabled;
        uniform vec2 uImageSize;    // image dimensions in pixels
        // WCS parameters
        uniform vec2 uCRPix;        // reference pixel (1-based)
        uniform vec2 uCRVal;        // reference sky coord (RA in radians, Dec in radians)
        uniform mat2 uCDMatrix;     // CD matrix in radians/pixel
        // Grid spacing in radians
        uniform float uGridSpacingRA;
        uniform float uGridSpacingDec;
        uniform float uGridLineWidth; // in sky-coordinate space (radians)

        const float PI = 3.14159265358979323846;

        // Midtones Transfer Function — same as PixInsight STF
        float mtf(float m, float v) {
            float c = clamp(v, 0.0, 1.0);
            if (v != c) return c;
            return (m - 1.0) * v / ((2.0 * m - 1.0) * v - m);
        }

        // Per-channel stretch: normalize, subtract pedestal, clip shadows/highlights, apply MTF
        float stretchChannel(float raw, int ch) {
            float norm = raw * uNormFactor - uPedestal[ch];
            float rescaled = (1.0 - uHighlights[ch] + norm - uShadows[ch]) * uRescale[ch];
            return mtf(uMidtones[ch], rescaled);
        }

        // S-curve contrast enhancement
        float applyCurve(float v, float boost) {
            float mid = uCurvesMidpoint;
            if (v <= 0.0 || v >= 1.0 || mid <= 0.0 || mid >= 1.0) return v;
            if (v <= mid) {
                float t = v / mid;
                return mid * pow(t, 1.0 + boost);
            } else {
                float t = (v - mid) / (1.0 - mid);
                return mid + (1.0 - mid) * pow(t, 1.0 / (1.0 + boost));
            }
        }

        // Hermite soft-knee HDR compression
        float applyHdr(float v, float amount, float knee) {
            if (v <= knee) return v;
            float range = 1.0 - knee;
            float t = (v - knee) / range;
            return knee + range * t / (1.0 + amount * t);
        }

        // Pixel to sky via gnomonic (TAN) deprojection — returns (RA, Dec) in radians
        vec2 pixelToSky(vec2 pixel) {
            vec2 dp = pixel - uCRPix;
            vec2 uv = uCDMatrix * dp; // intermediate world coords in radians

            float xi = uv.x;
            float eta = uv.y;
            float rho = length(uv);

            float ra0 = uCRVal.x;
            float dec0 = uCRVal.y;
            float sinDec0 = sin(dec0);
            float cosDec0 = cos(dec0);

            if (rho < 1e-10) return uCRVal;

            float c = atan(rho);
            float sinC = sin(c);
            float cosC = cos(c);

            float dec = asin(cosC * sinDec0 + eta * sinC * cosDec0 / rho);
            float ra = ra0 + atan(xi * sinC, rho * cosDec0 * cosC - eta * sinDec0 * sinC);

            return vec2(ra, dec);
        }

        // Check if this pixel is on a grid line
        float gridIntensity(vec2 pixel) {
            vec2 sky = pixelToSky(pixel);
            float ra = sky.x;
            float dec = sky.y;

            // Distance to nearest RA grid line
            float raGrid = ra / uGridSpacingRA;
            float raFrac = abs(raGrid - round(raGrid)) * uGridSpacingRA;

            // Distance to nearest Dec grid line
            float decGrid = dec / uGridSpacingDec;
            float decFrac = abs(decGrid - round(decGrid)) * uGridSpacingDec;

            // RA line width scaled by cos(dec) for convergence at poles
            float raWidth = uGridLineWidth / max(cos(dec), 0.01);

            float raLine = 1.0 - smoothstep(0.0, raWidth, raFrac);
            float decLine = 1.0 - smoothstep(0.0, uGridLineWidth, decFrac);

            return max(raLine, decLine);
        }

        void main() {
            float r = texture(uChannel0, vTexCoord).r;
            if (uChannelCount >= 3) {
                float g = texture(uChannel1, vTexCoord).r;
                float b = texture(uChannel2, vTexCoord).r;

                // Stretch
                if (uStretchMode == 1) {
                    r = stretchChannel(r, 0);
                    g = stretchChannel(g, 1);
                    b = stretchChannel(b, 2);
                } else if (uStretchMode == 2) {
                    float nr = r * uNormFactor;
                    float ng = g * uNormFactor;
                    float nb = b * uNormFactor;
                    float Y = 0.2126 * nr + 0.7152 * ng + 0.0722 * nb;
                    float rescaled = (1.0 - uHighlights.x + Y - uShadows.x) * uRescale.x;
                    float Yp = mtf(uMidtones.x, rescaled);
                    float scale = Y > 1e-7 ? Yp / Y : 0.0;
                    r = clamp(nr * scale, 0.0, 1.0);
                    g = clamp(ng * scale, 0.0, 1.0);
                    b = clamp(nb * scale, 0.0, 1.0);
                }

                if (uCurvesBoost > 0.0) {
                    r = applyCurve(r, uCurvesBoost);
                    g = applyCurve(g, uCurvesBoost);
                    b = applyCurve(b, uCurvesBoost);
                }
                if (uHdrAmount > 0.0) {
                    r = applyHdr(r, uHdrAmount, uHdrKnee);
                    g = applyHdr(g, uHdrAmount, uHdrKnee);
                    b = applyHdr(b, uHdrAmount, uHdrKnee);
                }

                // Grid overlay
                if (uGridEnabled) {
                    vec2 pixel = vec2(vTexCoord.x * uImageSize.x + 1.0, vTexCoord.y * uImageSize.y + 1.0);
                    float grid = gridIntensity(pixel);
                    vec3 gridColor = vec3(0.0, 0.8, 0.0);
                    r = mix(r, gridColor.r, grid * 0.7);
                    g = mix(g, gridColor.g, grid * 0.7);
                    b = mix(b, gridColor.b, grid * 0.7);
                }

                FragColor = vec4(r, g, b, 1.0);
            } else {
                // Mono
                if (uStretchMode >= 1) {
                    r = stretchChannel(r, 0);
                }
                if (uCurvesBoost > 0.0) {
                    r = applyCurve(r, uCurvesBoost);
                }
                if (uHdrAmount > 0.0) {
                    r = applyHdr(r, uHdrAmount, uHdrKnee);
                }

                // Grid overlay
                if (uGridEnabled) {
                    vec2 pixel = vec2(vTexCoord.x * uImageSize.x + 1.0, vTexCoord.y * uImageSize.y + 1.0);
                    float grid = gridIntensity(pixel);
                    vec3 gridColor = vec3(0.0, 0.8, 0.0);
                    float mono = mix(r, gridColor.r, grid * 0.7);
                    FragColor = vec4(
                        mix(r, gridColor.r, grid * 0.7),
                        mix(r, gridColor.g, grid * 0.7),
                        mix(r, gridColor.b, grid * 0.7),
                        1.0);
                } else {
                    FragColor = vec4(r, r, r, 1.0);
                }
            }
        }
        """;

    private const string FlatVertexShader = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        void main() {
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    private const string FlatFragmentShader = """
        #version 330 core
        out vec4 FragColor;
        uniform vec4 uColor;
        void main() {
            FragColor = uColor;
        }
        """;

    private const string TextVertexShader = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aTexCoord;
        out vec2 vTexCoord;
        void main() {
            gl_Position = vec4(aPos, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    private const string TextFragmentShader = """
        #version 330 core
        in vec2 vTexCoord;
        out vec4 FragColor;
        uniform sampler2D uTexture;
        uniform vec4 uColor;
        void main() {
            vec4 texel = texture(uTexture, vTexCoord);
            FragColor = vec4(uColor.rgb, texel.a * uColor.a);
        }
        """;
}
