namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Axis-aligned rectangle in pixel coordinates, used for layout and hit testing.
    /// Renderer-agnostic — works with any <see cref="DIR.Lib.Renderer{TSurface}"/>.
    /// </summary>
    public readonly record struct PixelRect(float X, float Y, float Width, float Height)
    {
        public float Right => X + Width;
        public float Bottom => Y + Height;
        public bool Contains(float px, float py) => px >= X && px < Right && py >= Y && py < Bottom;
        public PixelRect Inset(float padding) => new PixelRect(X + padding, Y + padding, Width - padding * 2, Height - padding * 2);
    }
}
