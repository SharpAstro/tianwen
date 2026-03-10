using System.Collections.Generic;

namespace TianWen.UI.Abstractions.Overlays;

/// <summary>
/// A fully computed overlay item ready for rendering.
/// Contains screen coordinates, colors, marker shape, and label lines.
/// </summary>
public sealed class OverlayItem
{
    /// <summary>Screen X coordinate of the object center.</summary>
    public float ScreenX { get; init; }

    /// <summary>Screen Y coordinate of the object center.</summary>
    public float ScreenY { get; init; }

    /// <summary>Overlay color (R, G, B) in 0..1 range.</summary>
    public (float R, float G, float B) Color { get; init; }

    /// <summary>The visual marker to draw (ellipse, cross, or circle).</summary>
    public required OverlayMarker Marker { get; init; }

    /// <summary>Label lines (first line = primary name, rest = secondary info).</summary>
    public required IReadOnlyList<string> LabelLines { get; init; }

    /// <summary>Whether this is a bright object that should force-place its label on collision.</summary>
    public bool ForcePlaceLabel { get; init; }
}
