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

    /// <summary>Right ascension in hours (J2000). Populated by the compute engine so
    /// callers can apply sky-position filtering (e.g. below-horizon dimming on the sky map).</summary>
    public double RA { get; init; }

    /// <summary>Declination in degrees (J2000).</summary>
    public double Dec { get; init; }

    /// <summary>Overlay color (R, G, B) in 0..1 range.</summary>
    public (float R, float G, float B) Color { get; init; }

    /// <summary>The visual marker to draw (ellipse, cross, or circle).</summary>
    public required OverlayMarker Marker { get; init; }

    /// <summary>Label lines (first line = primary name, rest = secondary info).</summary>
    public required IReadOnlyList<string> LabelLines { get; init; }

    /// <summary>
    /// Label placement priority (higher = more important). Composed from whether
    /// the object has a common name, its magnitude, and its on-sky size. Labels
    /// are drawn in priority order so that bright / named / large objects claim
    /// their preferred slot first; lower-priority labels drop silently when they
    /// collide rather than rotating slots or overlaying — this trades "every
    /// object gets a label" for stable placement that doesn't flicker as the user
    /// pans.
    /// </summary>
    public float LabelPriority { get; init; }

    /// <summary>
    /// Preferred label slot index (0..3) for the 4-position collision-avoidance scheme.
    /// Derived from a stable property of the catalog entry so the same object prefers
    /// the same slot across frames — critical for stable label placement while the
    /// user pans the sky map. 0 = right, 1 = left, 2 = above, 3 = below. Defaults to
    /// 0 (right-of-marker), preserving the FITS viewer's original behaviour.
    /// </summary>
    public int LabelSlotHint { get; init; }
}
