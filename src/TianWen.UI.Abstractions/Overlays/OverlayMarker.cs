namespace TianWen.UI.Abstractions.Overlays;

/// <summary>
/// Discriminator for <see cref="OverlayMarker"/> variants. Kept as an enum instead
/// of an inheritance hierarchy so the marker can be a value-type struct --
/// OverlayItems churn at ~hundreds/frame during wide-FOV pan and every heap alloc
/// shows up as GC pressure.
/// </summary>
public enum OverlayMarkerKind : byte
{
    Ellipse = 0,
    Cross = 1,
    Circle = 2,
}

/// <summary>
/// Describes the visual marker for an overlay item as a tagged value type. The
/// renderer switches on <see cref="Kind"/> and reads only the fields relevant to
/// that variant (Ellipse uses SemiMajorPx / SemiMinorPx / AngleRad; Circle aliases
/// <see cref="RadiusPx"/> to SemiMajorPx; Cross uses ArmPx).
/// </summary>
public readonly record struct OverlayMarker
{
    public OverlayMarkerKind Kind { get; init; }

    /// <summary>Ellipse semi-major axis in pixels. For <see cref="OverlayMarkerKind.Circle"/>
    /// this field also holds the radius -- see <see cref="RadiusPx"/>.</summary>
    public float SemiMajorPx { get; init; }

    /// <summary>Ellipse semi-minor axis in pixels. Unused for Cross / Circle.</summary>
    public float SemiMinorPx { get; init; }

    /// <summary>Ellipse rotation from +X in radians. Unused for Cross / Circle.</summary>
    public float AngleRad { get; init; }

    /// <summary>Cross arm length in pixels. Unused for Ellipse / Circle.</summary>
    public float ArmPx { get; init; }

    /// <summary>Alias of <see cref="SemiMajorPx"/> for Circle callers.</summary>
    public float RadiusPx => SemiMajorPx;

    public static OverlayMarker Ellipse(float semiMajorPx, float semiMinorPx, float angleRad) => new()
    {
        Kind = OverlayMarkerKind.Ellipse,
        SemiMajorPx = semiMajorPx,
        SemiMinorPx = semiMinorPx,
        AngleRad = angleRad,
    };

    public static OverlayMarker Cross(float armPx) => new()
    {
        Kind = OverlayMarkerKind.Cross,
        ArmPx = armPx,
    };

    public static OverlayMarker Circle(float radiusPx) => new()
    {
        Kind = OverlayMarkerKind.Circle,
        SemiMajorPx = radiusPx,
    };
}
