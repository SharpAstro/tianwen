using System;
using System.Collections.Generic;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic widget for hit testing and click dispatch.
    /// </summary>
    public interface IPixelWidget
    {
        /// <summary>Hit-tests the last rendered frame. Returns null for no hit.</summary>
        HitResult? HitTest(float x, float y);

        /// <summary>Hit-tests and invokes the <see cref="ClickableRegion.OnClick"/> handler if present.</summary>
        HitResult? HitTestAndDispatch(float x, float y);

        /// <summary>Returns all registered text inputs in order (for Tab cycling).</summary>
        List<TextInputState> GetRegisteredTextInputs();
    }

    /// <summary>
    /// Base class for pixel-coordinate widgets. Provides the clickable region system
    /// (RegisterClickable / HitTest / HitTestAndDispatch) and common drawing helpers.
    /// Generic over <typeparamref name="TSurface"/> so it works with any <see cref="Renderer{TSurface}"/>.
    /// </summary>
    public abstract class PixelWidgetBase<TSurface>(Renderer<TSurface> renderer) : IPixelWidget
    {
        private readonly List<ClickableRegion> _clickableRegions = [];

        protected Renderer<TSurface> Renderer { get; } = renderer;

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
        /// Registers a clickable region with an optional direct click handler.
        /// </summary>
        protected void RegisterClickable(float x, float y, float w, float h, HitResult result, Action? onClick = null)
        {
            _clickableRegions.Add(new ClickableRegion(x, y, w, h, result, onClick));
        }

        /// <summary>
        /// Registers a text input field — renders it and registers the clickable region.
        /// </summary>
        protected void RenderTextInput(TextInputState state, int x, int y, int width, int height, string fontPath, float fontSize)
        {
            TextInputRenderer.Render(Renderer, state, x, y, width, height, fontPath, fontSize, FrameCount);
            RegisterClickable(x, y, width, height, new HitResult.TextInputHit(state));
        }

        /// <summary>
        /// Renders a button and registers the clickable region with an optional direct handler.
        /// </summary>
        protected void RenderButton(string label, float x, float y, float w, float h, string fontPath, float fontSize,
            RGBAColor32 bgColor, RGBAColor32 textColor, string action, Action? onClick = null)
        {
            FillRect(x, y, w, h, bgColor);
            DrawText(label.AsSpan(), fontPath, x, y, w, h, fontSize, textColor, TextAlign.Center, TextAlign.Center);
            RegisterClickable(x, y, w, h, new HitResult.ButtonHit(action), onClick);
        }

        /// <summary>
        /// Measures text width for button sizing.
        /// </summary>
        protected float MeasureButtonWidth(string label, string fontPath, float fontSize, float padding)
        {
            return Renderer.MeasureText(label.AsSpan(), fontPath, fontSize).Width + padding * 2f;
        }

        /// <summary>
        /// Returns all TextInputState instances registered during the last Render call,
        /// in registration order. Used for Tab/Shift+Tab cycling.
        /// </summary>
        public List<TextInputState> GetRegisteredTextInputs()
        {
            var result = new List<TextInputState>();
            foreach (var r in _clickableRegions)
            {
                if (r.Result is HitResult.TextInputHit { Input: { } input } && !result.Contains(input))
                {
                    result.Add(input);
                }
            }
            return result;
        }

        /// <summary>
        /// Hit-tests using regions registered during the last Render call.
        /// Returns the last (topmost) matching region's result.
        /// </summary>
        public HitResult? HitTest(float x, float y)
        {
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

        /// <summary>
        /// Hit-tests and invokes the OnClick handler if present. Returns the hit result.
        /// </summary>
        public HitResult? HitTestAndDispatch(float x, float y)
        {
            for (var i = _clickableRegions.Count - 1; i >= 0; i--)
            {
                var r = _clickableRegions[i];
                if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height)
                {
                    r.OnClick?.Invoke();
                    return r.Result;
                }
            }
            return null;
        }

        // --- Drawing helpers ---

        protected void FillRect(float x, float y, float w, float h, RGBAColor32 color)
        {
            if (w <= 0 || h <= 0) return;
            Renderer.FillRectangle(
                new RectInt(new PointInt((int)(x + w), (int)(y + h)), new PointInt((int)x, (int)y)),
                color);
        }

        protected void DrawText(ReadOnlySpan<char> text, string fontPath, float x, float y, float w, float h,
            float fontSize, RGBAColor32 color, TextAlign horizAlign = TextAlign.Near, TextAlign vertAlign = TextAlign.Center)
        {
            if (string.IsNullOrEmpty(fontPath)) return;
            Renderer.DrawText(text, fontPath, fontSize, color,
                new RectInt(new PointInt((int)(x + w), (int)(y + h)), new PointInt((int)x, (int)y)),
                horizAlign, vertAlign);
        }
    }
}
