using System.Collections.Generic;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions.Overlays;

/// <summary>
/// A fully computed overlay item ready for rendering.
/// Contains screen coordinates, colors, marker shape, and label lines.
/// </summary>
public sealed class OverlayItem
{
    /// <summary>
    /// The catalogue entry this item was projected from, so a consumer that recorded WHERE the item
    /// was drawn can answer "which object is drawn here" without carrying the object along.
    /// </summary>
    /// <remarks>
    /// <see cref="StableSortKey"/> holds this index's raw bits and always has, but it is named for
    /// what it is used for, an ordering tiebreak, and reading an identity back out of a sort key is
    /// the kind of reuse that survives exactly until someone changes the key.
    /// </remarks>
    public CatalogIndex Index { get; init; }

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
    /// True for objects that are pinned in the planner's proposal list. Pinned items
    /// bypass FOV-based magnitude cutoffs and render with a bright highlight halo so
    /// the user can see their planned targets on the sky map at a glance.
    /// </summary>
    public bool IsPinned { get; init; }

    /// <summary>
    /// Label placement priority (higher = more important). Composed from whether
    /// the object has a common name, its magnitude, and its on-sky size. Labels
    /// are drawn in priority order so that bright / named / large objects claim
    /// their preferred slot first; lower-priority labels drop silently when they
    /// collide rather than rotating slots or overlaying; this trades "every
    /// object gets a label" for stable placement that doesn't flicker as the user
    /// pans.
    /// </summary>
    public float LabelPriority { get; init; }

    /// <summary>
    /// Preferred label slot index (0..3) for the 4-position collision-avoidance scheme.
    /// Derived from a stable property of the catalog entry so the same object prefers
    /// the same slot across frames: critical for stable label placement while the
    /// user pans the sky map. 0 = right, 1 = left, 2 = above, 3 = below. Defaults to
    /// 0 (right-of-marker), preserving the FITS viewer's original behaviour.
    /// </summary>
    public int LabelSlotHint { get; init; }

    /// <summary>
    /// Full-width stable tiebreaker for sort orderings keyed on <see cref="LabelPriority"/>.
    /// Typically the raw catalog-index bits -- unique per object, deterministic across
    /// frames. Without this the 2-bit <see cref="LabelSlotHint"/> is the only tiebreaker
    /// and many bright stars at wide FOV end up ordering indeterminately under
    /// <see cref="List{T}.Sort"/> (unstable QuickSort), which manifests as flickering
    /// labels as the user pans.
    /// </summary>
    public ulong StableSortKey { get; init; }

    /// <summary>
    /// Index of the <see cref="OverlayCandidate"/> this was projected from, or -1 when the item did
    /// not come from a candidate list (the FITS viewer builds items directly).
    ///
    /// <para>It exists so a marker pass can be driven FROM the projected items instead of projecting
    /// every candidate a second time. The sky map used to do exactly that: one pass projected every
    /// candidate to place markers, then <see cref="OverlayEngine.ProjectSkyMapCandidatesInto"/>
    /// projected the identical set again to place labels. Markers still have to read the candidate
    /// (the projected item drops the arcmin extent and position angle, which the GPU path reads off
    /// the candidate instead), so the link back is what removes the duplication.</para>
    /// </summary>
    public int CandidateIndex { get; init; } = -1;
}
