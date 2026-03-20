namespace TianWen.UI.Gui
{
    /// <summary>
    /// Axis-aligned rectangle in pixel coordinates, used for layout and hit testing.
    /// </summary>
    public readonly record struct VkRect(float X, float Y, float Width, float Height)
    {
        public float Right => X + Width;
        public float Bottom => Y + Height;
        public bool Contains(float px, float py) => px >= X && px < Right && py >= Y && py < Bottom;
        public VkRect Inset(float padding) => new VkRect(X + padding, Y + padding, Width - padding * 2, Height - padding * 2);
    }
}
