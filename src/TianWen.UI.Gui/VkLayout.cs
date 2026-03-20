using System.Collections.Generic;

namespace TianWen.UI.Gui
{
    /// <summary>
    /// Dock direction for <see cref="VkLayout"/>.
    /// </summary>
    public enum VkDockStyle { Top, Bottom, Left, Right }

    /// <summary>
    /// Dock-based layout engine. Consumes strips from the edges of a root rectangle,
    /// identical to Console.Lib's TerminalLayout but in pixel coordinates.
    /// </summary>
    public sealed class VkLayout
    {
        private VkRect _remaining;
        private readonly List<(VkDockStyle Style, float Size)> _docks = [];

        public VkLayout(VkRect root)
        {
            _remaining = root;
        }

        /// <summary>
        /// Allocates a strip of the given <paramref name="size"/> from the specified edge
        /// and returns its rectangle. The remaining space shrinks accordingly.
        /// </summary>
        public VkRect Dock(VkDockStyle style, float size)
        {
            _docks.Add((style, size));

            VkRect result;
            switch (style)
            {
                case VkDockStyle.Top:
                    result = new VkRect(_remaining.X, _remaining.Y, _remaining.Width, size);
                    _remaining = new VkRect(_remaining.X, _remaining.Y + size, _remaining.Width, _remaining.Height - size);
                    break;
                case VkDockStyle.Bottom:
                    result = new VkRect(_remaining.X, _remaining.Bottom - size, _remaining.Width, size);
                    _remaining = new VkRect(_remaining.X, _remaining.Y, _remaining.Width, _remaining.Height - size);
                    break;
                case VkDockStyle.Left:
                    result = new VkRect(_remaining.X, _remaining.Y, size, _remaining.Height);
                    _remaining = new VkRect(_remaining.X + size, _remaining.Y, _remaining.Width - size, _remaining.Height);
                    break;
                case VkDockStyle.Right:
                    result = new VkRect(_remaining.Right - size, _remaining.Y, size, _remaining.Height);
                    _remaining = new VkRect(_remaining.X, _remaining.Y, _remaining.Width - size, _remaining.Height);
                    break;
                default:
                    result = _remaining;
                    break;
            }

            return result;
        }

        /// <summary>
        /// Returns the remaining rectangle after all docks have been applied.
        /// </summary>
        public VkRect Fill() => _remaining;

        /// <summary>
        /// Replays the recorded dock sequence against a new root rectangle.
        /// Useful for resize without rebuilding the layout logic.
        /// </summary>
        public void Recompute(VkRect newRoot)
        {
            _remaining = newRoot;
            var count = _docks.Count;
            for (var i = 0; i < count; i++)
            {
                var (style, size) = _docks[i];
                switch (style)
                {
                    case VkDockStyle.Top:
                        _remaining = new VkRect(_remaining.X, _remaining.Y + size, _remaining.Width, _remaining.Height - size);
                        break;
                    case VkDockStyle.Bottom:
                        _remaining = new VkRect(_remaining.X, _remaining.Y, _remaining.Width, _remaining.Height - size);
                        break;
                    case VkDockStyle.Left:
                        _remaining = new VkRect(_remaining.X + size, _remaining.Y, _remaining.Width - size, _remaining.Height);
                        break;
                    case VkDockStyle.Right:
                        _remaining = new VkRect(_remaining.X, _remaining.Y, _remaining.Width - size, _remaining.Height);
                        break;
                }
            }
        }
    }
}
