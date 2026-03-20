using System.Collections.Generic;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Base class for GPU-rendered tabs. Provides the clickable region system
/// (RegisterClickable / HitTest) and common drawing helpers.
/// </summary>
public abstract class VkTabBase
{
    private readonly VkRenderer _renderer;
    private readonly List<ClickableRegion> _clickableRegions = [];

    protected VkTabBase(VkRenderer renderer)
    {
        _renderer = renderer;
    }

    protected VkRenderer Renderer => _renderer;

    /// <summary>Frame counter for cursor blink etc.</summary>
    public long FrameCount { get; set; }

    /// <summary>
    /// Clears clickable regions. Call at the start of each Render pass.
    /// </summary>
    protected void BeginFrame()
    {
        _clickableRegions.Clear();
    }

    /// <summary>
    /// Registers a clickable region. Call immediately after rendering the element.
    /// </summary>
    protected void RegisterClickable(float x, float y, float w, float h, HitResult result)
    {
        _clickableRegions.Add(new ClickableRegion(x, y, w, h, result));
    }

    /// <summary>
    /// Registers a text input field — renders it and registers the clickable region.
    /// </summary>
    protected void RenderTextInput(TextInputState state, int x, int y, int width, int height, string fontPath, float fontSize)
    {
        TextInputRenderer.Render(_renderer, state, x, y, width, height, fontPath, fontSize, FrameCount);
        RegisterClickable(x, y, width, height, new HitResult.TextInputHit(state));
    }

    /// <summary>
    /// Registers a button — renders it and registers the clickable region.
    /// </summary>
    protected void RenderButton(string label, float x, float y, float w, float h, string fontPath, float fontSize,
        RGBAColor32 bgColor, RGBAColor32 textColor, string action)
    {
        FillRect(x, y, w, h, bgColor);
        DrawText(label.AsSpan(), fontPath, x, y, w, h, fontSize, textColor, TextAlign.Center, TextAlign.Center);
        RegisterClickable(x, y, w, h, new HitResult.ButtonHit(action));
    }

    /// <summary>
    /// Measures text width for button sizing.
    /// </summary>
    protected float MeasureButtonWidth(string label, string fontPath, float fontSize, float padding)
    {
        return _renderer.MeasureText(label.AsSpan(), fontPath, fontSize).Width + padding * 2f;
    }

    /// <summary>
    /// Hit-tests using regions registered during the last Render call.
    /// Returns the last (topmost) matching region.
    /// </summary>
    public HitResult? HitTest(float x, float y)
    {
        // Walk in reverse — last registered is on top
        for (var i = _clickableRegions.Count - 1; i >= 0; i--)
        {
            var r = _clickableRegions[i];
            if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height)
            {
                return r.Result;
            }
        }
        return null;
    }

    // --- Drawing helpers (same pattern as VkGuiRenderer) ---

    protected void FillRect(float x, float y, float w, float h, RGBAColor32 color)
    {
        if (w <= 0 || h <= 0) return;
        _renderer.FillRectangle(
            new RectInt(new PointInt((int)(x + w), (int)(y + h)), new PointInt((int)x, (int)y)),
            color);
    }

    protected void DrawText(ReadOnlySpan<char> text, string fontPath, float x, float y, float w, float h,
        float fontSize, RGBAColor32 color, TextAlign horizAlign = TextAlign.Near, TextAlign vertAlign = TextAlign.Center)
    {
        if (string.IsNullOrEmpty(fontPath)) return;
        _renderer.DrawText(text, fontPath, fontSize, color,
            new RectInt(new PointInt((int)(x + w), (int)(y + h)), new PointInt((int)x, (int)y)),
            horizAlign, vertAlign);
    }
}
