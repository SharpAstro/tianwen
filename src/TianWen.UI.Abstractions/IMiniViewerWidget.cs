using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Renderer-agnostic interface for a mini image preview widget.
/// The live session tab calls <see cref="QueueImage"/> when a new frame arrives,
/// and <see cref="Render"/> each frame to draw into the given rect.
/// The GUI provides a Vulkan implementation; TUI can provide a no-op or sixel version.
/// </summary>
public interface IMiniViewerWidget
{
    /// <summary>
    /// Whether the widget has an image loaded and ready to render.
    /// </summary>
    bool HasImage { get; }

    /// <summary>Widget state (zoom, stretch, boost).</summary>
    MiniViewerState State { get; }

    /// <summary>
    /// Queues a new image for display. The widget will compute stretch stats and
    /// upload textures on the next <see cref="Render"/> call or asynchronously.
    /// </summary>
    void QueueImage(Image image);

    /// <summary>
    /// Optional plate-solved WCS for the current frame. When non-null and
    /// <see cref="MiniViewerState.ShowGrid"/> is true, the renderer overlays
    /// an RA/Dec coordinate grid using the same shader path the FITS viewer
    /// uses. Caller is responsible for clearing this when the active image
    /// no longer matches the solve (e.g., a new exposure invalidates the WCS).
    /// </summary>
    WCS? Wcs { get; set; }

    /// <summary>
    /// Optional font path used by the WCS-annotation overlay to render
    /// ring / marker labels. When null the renderer skips label drawing
    /// (the rest of the overlay still draws). Caller (live session tab)
    /// sets this from its own font-resolution path so mini viewer text
    /// and side-panel text use the same face.
    /// </summary>
    string? FontPath { get; set; }

    /// <summary>
    /// Renders the current image with toolbar into the given rectangle.
    /// No-op if <see cref="HasImage"/> is false.
    /// </summary>
    /// <param name="rect">Target rectangle in window coordinates.</param>
    /// <param name="windowWidth">Full window width for projection.</param>
    /// <param name="windowHeight">Full window height for projection.</param>
    void Render(RectF32 rect, uint windowWidth, uint windowHeight);
}
