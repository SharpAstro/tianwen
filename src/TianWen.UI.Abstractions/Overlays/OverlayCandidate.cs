using System.Collections.Generic;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions.Overlays;

/// <summary>
/// View-matrix-independent snapshot of a sky-map overlay object. Produced by
/// <see cref="OverlayEngine.GatherSkyMapOverlayCandidates"/> (the heavy spatial-grid
/// walk + filtering pass) and consumed by
/// <see cref="OverlayEngine.ProjectSkyMapCandidatesInto"/> (the cheap per-frame
/// projection pass).
/// </summary>
/// <remarks>
/// Splitting the overlay computation lets the GUI tab cache the candidate list
/// independently of the current view matrix. At wide FOV (>= 90 deg) the spatial
/// scan already sweeps the whole sphere, so the candidate set is strictly a
/// function of FOV-bucket + rect + dpi + pins, not the pan angle. Caching on
/// that key means a drag-pan triggers only the lightweight projection phase,
/// instead of the full grid walk + hundreds of List/HashSet allocs per frame.
/// </remarks>
public readonly record struct OverlayCandidate
{
    public required CatalogIndex CatalogIndex { get; init; }

    /// <summary>Right ascension in hours (J2000).</summary>
    public required double RA { get; init; }

    /// <summary>Declination in degrees (J2000).</summary>
    public required double Dec { get; init; }

    public required (float R, float G, float B) Color { get; init; }

    /// <summary>Marker shape in view-independent form -- screen PA / pixel sizes
    /// are derived at projection time from <see cref="OverlayEngine.GetArcminToPixels"/>.</summary>
    public required OverlayCandidateMarker Marker { get; init; }

    public required IReadOnlyList<string> LabelLines { get; init; }

    public required bool IsPinned { get; init; }

    public required float LabelPriority { get; init; }

    public required int LabelSlotHint { get; init; }
}

/// <summary>Marker payload that still requires the current view matrix (for screen PA)
/// or the current dpi / ppr (for pixel sizes) before it becomes an <see cref="OverlayMarker"/>.</summary>
public abstract record OverlayCandidateMarker
{
    /// <summary>Ellipse with semi-axes in arcminutes and position angle from north
    /// (J2000). Converted to an <see cref="OverlayMarker.Ellipse"/> at projection
    /// time via arcmin-to-pixel scaling plus tangent-plane screen PA.</summary>
    public sealed record Ellipse(float SemiMajArcmin, float SemiMinArcmin, System.Half PositionAngle)
        : OverlayCandidateMarker;

    /// <summary>Fixed-size cross marker (stars) -- pixel size is a function of dpi only.</summary>
    public sealed record Cross(float ArmPxAtDpi1) : OverlayCandidateMarker;

    /// <summary>Fixed-size circle marker (default / no shape) -- pixel size is dpi-only.</summary>
    public sealed record Circle(float RadiusPxAtDpi1) : OverlayCandidateMarker;
}
