using System.Collections.Generic;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Dock direction for <see cref="PixelLayout"/>.
    /// </summary>
    public enum PixelDockStyle { Top, Bottom, Left, Right }

    /// <summary>
    /// Dock-based layout engine. Consumes strips from the edges of a root rectangle.
    /// Renderer-agnostic — works with any <see cref="DIR.Lib.Renderer{TSurface}"/>.
    /// </summary>
    public sealed class PixelLayout
    {
        private PixelRect _remaining;
        private readonly List<(PixelDockStyle Style, float Size)> _docks = [];

        public PixelLayout(PixelRect root)
        {
            _remaining = root;
        }

        /// <summary>
        /// Allocates a strip of the given <paramref name="size"/> from the specified edge
        /// and returns its rectangle. The remaining space shrinks accordingly.
        /// </summary>
        public PixelRect Dock(PixelDockStyle style, float size)
        {
            _docks.Add((style, size));

            PixelRect result;
            switch (style)
            {
                case PixelDockStyle.Top:
                    result = new PixelRect(_remaining.X, _remaining.Y, _remaining.Width, size);
                    _remaining = new PixelRect(_remaining.X, _remaining.Y + size, _remaining.Width, _remaining.Height - size);
                    break;
                case PixelDockStyle.Bottom:
                    result = new PixelRect(_remaining.X, _remaining.Bottom - size, _remaining.Width, size);
                    _remaining = new PixelRect(_remaining.X, _remaining.Y, _remaining.Width, _remaining.Height - size);
                    break;
                case PixelDockStyle.Left:
                    result = new PixelRect(_remaining.X, _remaining.Y, size, _remaining.Height);
                    _remaining = new PixelRect(_remaining.X + size, _remaining.Y, _remaining.Width - size, _remaining.Height);
                    break;
                case PixelDockStyle.Right:
                    result = new PixelRect(_remaining.Right - size, _remaining.Y, size, _remaining.Height);
                    _remaining = new PixelRect(_remaining.X, _remaining.Y, _remaining.Width - size, _remaining.Height);
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
        public PixelRect Fill() => _remaining;

        /// <summary>
        /// Replays the recorded dock sequence against a new root rectangle.
        /// </summary>
        public void Recompute(PixelRect newRoot)
        {
            _remaining = newRoot;
            var count = _docks.Count;
            for (var i = 0; i < count; i++)
            {
                var (style, size) = _docks[i];
                switch (style)
                {
                    case PixelDockStyle.Top:
                        _remaining = new PixelRect(_remaining.X, _remaining.Y + size, _remaining.Width, _remaining.Height - size);
                        break;
                    case PixelDockStyle.Bottom:
                        _remaining = new PixelRect(_remaining.X, _remaining.Y, _remaining.Width, _remaining.Height - size);
                        break;
                    case PixelDockStyle.Left:
                        _remaining = new PixelRect(_remaining.X + size, _remaining.Y, _remaining.Width - size, _remaining.Height);
                        break;
                    case PixelDockStyle.Right:
                        _remaining = new PixelRect(_remaining.X, _remaining.Y, _remaining.Width - size, _remaining.Height);
                        break;
                }
            }
        }
    }
}
