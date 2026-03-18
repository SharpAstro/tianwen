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
    float DpiScale
)
{
    /// <summary>Computed drawn image width on screen.</summary>
    public readonly float DrawWidth => ImageWidth * Zoom;

    /// <summary>Computed drawn image height on screen.</summary>
    public readonly float DrawHeight => ImageHeight * Zoom;

    /// <summary>X offset where the image starts on screen.</summary>
    public readonly float ImageOffsetX => AreaLeft + (AreaWidth - DrawWidth) / 2f + PanOffset.X;

    /// <summary>Y offset where the image starts on screen.</summary>
    public readonly float ImageOffsetY => AreaTop + (AreaHeight - DrawHeight) / 2f + PanOffset.Y;
}
