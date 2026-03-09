using Silk.NET.OpenGL;
using TianWen.UI.Abstractions;

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
    private const float BaseInfoPanelWidth = 260f;
    private const float BaseStatusBarHeight = 24f;
    private const float BaseToolbarHeight = 40f;
    private const float BaseFileListWidth = 260f;
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
            RenderImage(state);
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
            var active = IsToolbarButtonActive(action, state);

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

    private static bool IsToolbarButtonEnabled(ToolbarAction action, FitsDocument? document)
    {
        return action switch
        {
            // Debayer only makes sense for Bayer sensors (e.g. RGGB)
            ToolbarAction.Debayer => document?.RawImage.ImageMeta.SensorType is TianWen.Lib.Imaging.SensorType.RGGB,
            // Channel cycling only useful when there are multiple channels (after debayer or native color)
            ToolbarAction.Channel => document is not null && document.DisplayImage.ChannelCount > 1,
            ToolbarAction.CurvesBoost or ToolbarAction.Hdr => document is not null,
            // Stretch buttons need a loaded document; link/params only when stretch is active
            ToolbarAction.StretchToggle => document is not null,
            ToolbarAction.StretchLink or ToolbarAction.StretchParams => document is not null,
            // Zoom, plate solve need a loaded document
            ToolbarAction.ZoomFit or ToolbarAction.ZoomActual or ToolbarAction.PlateSolve
                => document is not null,
            // Open is always enabled
            _ => true,
        };
    }

    private static bool IsToolbarButtonActive(ToolbarAction action, ViewerState state)
    {
        return action switch
        {
            ToolbarAction.StretchToggle or ToolbarAction.StretchLink or ToolbarAction.StretchParams
                => state.StretchMode is not StretchMode.None,
            ToolbarAction.CurvesBoost => state.CurvesBoost > 0f,
            ToolbarAction.Hdr => state.HdrAmount > 0f,
            ToolbarAction.ZoomFit => state.ZoomToFit,
            ToolbarAction.ZoomActual => !state.ZoomToFit && MathF.Abs(state.Zoom - 1f) < 0.001f,
            _ => false,
        };
    }

    private static string GetToolbarButtonLabel(string baseLabel, ToolbarAction action, FitsDocument? document, ViewerState state)
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
            ToolbarAction.Debayer => $"Debayer: {state.DebayerAlgorithm}",
            ToolbarAction.CurvesBoost => $"Boost: {state.CurvesBoost:P0}",
            ToolbarAction.Hdr => state.HdrAmount > 0f ? $"HDR: {state.HdrAmount:F1}" : "HDR",
            ToolbarAction.ZoomFit => "Fit",
            ToolbarAction.ZoomActual => "1:1",
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

    private void RenderImage(ViewerState state)
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
        for (int i = 0; i < _channelTextureCount && i < _channelTextures.Length; i++)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + i);
            _gl.BindTexture(TextureTarget.Texture2D, _channelTextures[i]);
            _imageShader.SetInt($"uChannel{i}", i);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        _gl.Disable(EnableCap.ScissorTest);
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

        // Metadata section
        DrawTextLine(ref y, x, "-- Metadata --", 0.6f, 0.8f, 1f);
        foreach (var line in InfoPanelData.GetMetadataLines(document))
        {
            DrawTextLine(ref y, x, line, 0.9f, 0.9f, 0.9f);
        }

        y += FontSize;

        // Statistics section
        DrawTextLine(ref y, x, "-- Statistics --", 0.6f, 0.8f, 1f);
        foreach (var line in InfoPanelData.GetStatisticsLines(document))
        {
            DrawTextLine(ref y, x, line, 0.9f, 0.9f, 0.9f);
        }

        // WCS section
        if (document.IsPlateSolved)
        {
            y += FontSize;
            DrawTextLine(ref y, x, "-- WCS --", 0.6f, 0.8f, 1f);
            foreach (var line in InfoPanelData.GetWcsLines(document))
            {
                DrawTextLine(ref y, x, line, 0.9f, 0.9f, 0.9f);
            }
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
        var controlLines = 11; // header + 10 controls
        y = _height - StatusBarHeight - lineHeight * controlLines - PanelPadding;
        if (y > ToolbarHeight + lineHeight * 5)
        {
            DrawTextLine(ref y, x, "-- Controls --", 0.6f, 0.8f, 1f);
            DrawTextLine(ref y, x, "S: Cycle stretch", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "+/-: Stretch factor", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "C: Cycle channel", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "D: Cycle debayer", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "H: Cycle HDR", 0.7f, 0.7f, 0.7f);
            DrawTextLine(ref y, x, "P: Plate solve", 0.7f, 0.7f, 0.7f);
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

        if (document.IsPlateSolved)
        {
            statusParts.Add("WCS: solved");
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

        // S-curve contrast enhancement inspired by the PixInsight
        // Statistical Stretch "Final Sigma Curves" boost.
        // Darkens below midpoint, brightens above — anchored at 0, mid, and 1.
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

        // Hermite soft-knee HDR compression.
        // Values below knee pass through unchanged. Values above knee are
        // compressed toward 1.0 using a smooth Hermite (smoothstep) curve.
        // Amount controls the compression strength (0 = off, higher = more).
        float applyHdr(float v, float amount, float knee) {
            if (v <= knee) return v;
            float range = 1.0 - knee;
            float t = (v - knee) / range;
            // Hermite interpolation: compressed = knee + range * smoothstep blend
            // At amount=0 this is identity; higher amount flattens highlights more.
            float compressed = knee + range * t / (1.0 + amount * t);
            return compressed;
        }

        void main() {
            float r = texture(uChannel0, vTexCoord).r;
            if (uChannelCount >= 3) {
                float g = texture(uChannel1, vTexCoord).r;
                float b = texture(uChannel2, vTexCoord).r;
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
                FragColor = vec4(r, g, b, 1.0);
            } else {
                if (uCurvesBoost > 0.0) {
                    r = applyCurve(r, uCurvesBoost);
                }
                if (uHdrAmount > 0.0) {
                    r = applyHdr(r, uHdrAmount, uHdrKnee);
                }
                FragColor = vec4(r, r, r, 1.0);
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
