namespace TianWen.UI.Abstractions.Overlays;

/// <summary>
/// Describes the viewport geometry needed for overlay computation.
/// All values are in screen pixels.
/// </summary>
public readonly record struct ViewportLayout(
    /// <summary>Total window width in screen pixels.</summary>
    float WindowWidth,
    /// <summary>Total window height in screen pixels.</summary>
    float WindowHeight,
    /// <summary>Image width in image pixels.</summary>
    int ImageWidth,
    /// <summary>Image height in image pixels.</summary>
    int ImageHeight,
    /// <summary>Current zoom/scale factor (1.0 = 100%).</summary>
    float Zoom,
    /// <summary>Pan offset in screen pixels (X, Y).</summary>
    (float X, float Y) PanOffset,
    /// <summary>Left edge of the image area in screen pixels (after file list sidebar).</summary>
    float AreaLeft,
    /// <summary>Top edge of the image area in screen pixels (after toolbar).</summary>
    float AreaTop,
    /// <summary>Width of the image area in screen pixels.</summary>
    float AreaWidth,
    /// <summary>Height of the image area in screen pixels.</summary>
    float AreaHeight,
    /// <summary>DPI scale factor (1.0 on non-HiDPI displays).</summary>
    float DpiScale,
    /// <summary>
    /// Where the picture's top-left corner was actually DRAWN, in screen pixels -- the viewer's own
    /// placement, which carries the display crop -- or null to derive it from the area, zoom and pan
    /// as a centred, uncropped frame. The viewer always passes it: a crop shifts the quad by its
    /// offset, and a layout that re-derives the origin from the pan alone puts every overlay on the
    /// uncropped frame's geometry instead of the one on screen. The derived form is for callers with
    /// no placement to read.
    /// </summary>
    (float X, float Y)? ImageOrigin = null
)
{
    /// <summary>Computed drawn image width on screen.</summary>
    public readonly float DrawWidth => ImageWidth * Zoom;

    /// <summary>Computed drawn image height on screen.</summary>
    public readonly float DrawHeight => ImageHeight * Zoom;

    /// <summary>
    /// Screen x of the picture's left edge: the placement when the host passed one, the centred
    /// uncropped geometry otherwise. Pixel 0 of the frame spans <c>[ImageOffsetX, ImageOffsetX + Zoom)</c>;
    /// the mapping from a frame coordinate to a screen one is <see cref="WcsAnnotationLayer.ImageToScreen"/>
    /// and nothing else.
    /// </summary>
    public readonly float ImageOffsetX => ImageOrigin is { } origin
        ? origin.X
        : AreaLeft + (AreaWidth - DrawWidth) / 2f + PanOffset.X;

    /// <summary>Screen y of the picture's top edge. See <see cref="ImageOffsetX"/>.</summary>
    public readonly float ImageOffsetY => ImageOrigin is { } origin
        ? origin.Y
        : AreaTop + (AreaHeight - DrawHeight) / 2f + PanOffset.Y;
}
