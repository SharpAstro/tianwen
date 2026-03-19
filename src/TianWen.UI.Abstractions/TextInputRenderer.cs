using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Renders a single-line text input field onto any <see cref="Renderer{TSurface}"/>.
/// Works with VkRenderer (GPU) and RgbaImageRenderer (TUI).
/// </summary>
public static class TextInputRenderer
{
    private static readonly RGBAColor32 FieldBackground = new(40, 40, 50, 255);
    private static readonly RGBAColor32 FieldBackgroundActive = new(50, 50, 65, 255);
    private static readonly RGBAColor32 FieldBorder = new(80, 80, 100, 255);
    private static readonly RGBAColor32 FieldBorderActive = new(100, 140, 200, 255);
    private static readonly RGBAColor32 TextColor = new(220, 220, 220, 255);
    private static readonly RGBAColor32 PlaceholderColor = new(120, 120, 140, 255);
    private static readonly RGBAColor32 CursorColor = new(200, 200, 255, 255);

    /// <summary>
    /// Renders a text input field at the specified position.
    /// </summary>
    /// <param name="renderer">Target renderer.</param>
    /// <param name="state">Text input state.</param>
    /// <param name="x">Left edge in pixels.</param>
    /// <param name="y">Top edge in pixels.</param>
    /// <param name="width">Field width in pixels.</param>
    /// <param name="height">Field height in pixels.</param>
    /// <param name="fontFamily">Font path for text rendering.</param>
    /// <param name="fontSize">Font size in pixels.</param>
    /// <param name="frameCount">Frame counter for cursor blink (blinks every 30 frames).</param>
    public static void Render<TSurface>(
        Renderer<TSurface> renderer,
        TextInputState state,
        int x, int y, int width, int height,
        string fontFamily, float fontSize,
        long frameCount = 0)
    {
        var bgColor = state.IsActive ? FieldBackgroundActive : FieldBackground;
        var borderColor = state.IsActive ? FieldBorderActive : FieldBorder;

        // Background
        renderer.FillRectangle(
            new RectInt(new PointInt(x + width, y + height), new PointInt(x, y)),
            bgColor);

        // Border
        renderer.DrawRectangle(
            new RectInt(new PointInt(x + width, y + height), new PointInt(x, y)),
            borderColor, 1);

        // Text or placeholder
        var padding = (int)(fontSize * 0.4f);
        var textX = x + padding;
        var textY = y;
        var textW = width - padding * 2;
        var textH = height;

        var displayText = state.Text.Length > 0 ? state.Text : (state.IsActive ? "" : state.Placeholder);
        var textColor = state.Text.Length > 0 ? TextColor : PlaceholderColor;

        if (displayText.Length > 0)
        {
            var layoutRect = new RectInt(
                new PointInt(textX + textW, textY + textH),
                new PointInt(textX, textY));

            renderer.DrawText(
                displayText.AsSpan(),
                fontFamily,
                fontSize,
                textColor,
                layoutRect,
                TextAlign.Near,
                TextAlign.Center);
        }

        // Cursor (blinking)
        if (state.IsActive && (frameCount / 30) % 2 == 0)
        {
            // Measure text up to cursor position to find cursor X
            var textBeforeCursor = state.Text.Length > 0 && state.CursorPos > 0
                ? state.Text[..state.CursorPos]
                : "";
            var cursorX = textX + (int)renderer.MeasureText(textBeforeCursor.AsSpan(), fontFamily, fontSize).Width;
            var cursorY = y + (int)(height * 0.15f);
            var cursorH = (int)(height * 0.7f);

            renderer.FillRectangle(
                new RectInt(new PointInt(cursorX + 2, cursorY + cursorH), new PointInt(cursorX, cursorY)),
                CursorColor);
        }
    }

    /// <summary>
    /// Hit-tests whether a click is inside the text field.
    /// </summary>
    public static bool HitTest(int clickX, int clickY, int fieldX, int fieldY, int fieldWidth, int fieldHeight)
    {
        return clickX >= fieldX && clickX < fieldX + fieldWidth
            && clickY >= fieldY && clickY < fieldY + fieldHeight;
    }
}
