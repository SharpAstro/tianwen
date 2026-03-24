using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Renderer-agnostic interface for a mini image preview widget.
/// The live session tab calls <see cref="UpdateImageAsync"/> when a new frame arrives,
/// and <see cref="Render"/> each frame to draw into the given rect.
/// The GUI provides a Vulkan implementation; TUI can provide a no-op or sixel version.
/// </summary>
public interface IMiniViewerWidget
{
    /// <summary>
    /// Whether the widget has an image loaded and ready to render.
    /// </summary>
    bool HasImage { get; }

    /// <summary>
    /// Queues a new image for display. The widget will compute stretch stats and
    /// upload textures on the next <see cref="Render"/> call or asynchronously.
    /// </summary>
    void QueueImage(Image image);

    /// <summary>
    /// Renders the current image into the given rectangle.
    /// No-op if <see cref="HasImage"/> is false.
    /// </summary>
    /// <param name="rect">Target rectangle in window coordinates.</param>
    /// <param name="windowWidth">Full window width for projection.</param>
    /// <param name="windowHeight">Full window height for projection.</param>
    void Render(RectF32 rect, uint windowWidth, uint windowHeight);
}
