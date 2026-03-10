namespace TianWen.UI.Abstractions.Overlays;

/// <summary>
/// Describes the visual marker for an overlay item.
/// The renderer maps each variant to the appropriate drawing primitive.
/// </summary>
public abstract record OverlayMarker
{
    private OverlayMarker() { }

    /// <summary>An ellipse outline (DSOs with shape data).</summary>
    public sealed record Ellipse(float SemiMajorPx, float SemiMinorPx, float AngleRad) : OverlayMarker;

    /// <summary>A cross/plus marker (stars).</summary>
    public sealed record Cross(float ArmPx) : OverlayMarker;

    /// <summary>A small circle marker (extended objects without shape data).</summary>
    public sealed record Circle(float RadiusPx) : OverlayMarker;
}
