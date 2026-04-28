using System.Collections.Immutable;
using DIR.Lib;

namespace TianWen.UI.Abstractions.Overlays
{
    /// <summary>
    /// Data-driven sky-annotation input for the WCS overlay layer. Holds two
    /// kinds of items: <see cref="SkyMarker"/>s (point glyphs at sky positions)
    /// and <see cref="SkyRing"/>s (circles of arcmin radius around a sky position).
    ///
    /// Generic primitive — not polar-alignment-specific. Intended consumers:
    /// the polar-alignment mode (two pole crosses + 5'/15'/30' rings + axis
    /// marker), the FITS viewer (plate-solve cross-checks, search rings around
    /// clicked targets), the live preview (target markers, dither circle),
    /// the mosaic composer (next-panel boundary).
    /// </summary>
    public readonly record struct WcsAnnotation(
        ImmutableArray<SkyMarker> Markers,
        ImmutableArray<SkyRing> Rings,
        ImmutableArray<SkyArrow> Arrows = default)
    {
        public static readonly WcsAnnotation Empty = new([], [], []);

        public bool IsEmpty =>
            Markers.IsDefaultOrEmpty && Rings.IsDefaultOrEmpty && Arrows.IsDefaultOrEmpty;
    }

    /// <summary>
    /// Single sky-position marker (cross, dot, etc.) drawn through the active WCS.
    /// </summary>
    /// <param name="RaHours">J2000 RA in hours [0, 24).</param>
    /// <param name="DecDeg">J2000 Dec in degrees [-90, 90].</param>
    /// <param name="Glyph">Visual style — drives which primitive the renderer dispatches.</param>
    /// <param name="Color">Stroke colour with alpha.</param>
    /// <param name="Label">Optional label drawn next to the marker; null for no label.</param>
    /// <param name="SizePx">Glyph size in screen pixels (cross arm length / dot radius / etc.).</param>
    public readonly record struct SkyMarker(
        double RaHours,
        double DecDeg,
        SkyMarkerGlyph Glyph,
        RGBAColor32 Color,
        string? Label,
        float SizePx);

    /// <summary>
    /// Concentric circle of fixed angular radius drawn around a sky position.
    /// Circular in the local tangent plane — the renderer treats it as an
    /// ellipse using the local pixel scale (good approximation for small
    /// rings; below ~5° radius the foreshortening error is sub-pixel).
    /// </summary>
    /// <param name="CenterRaHours">Centre RA in hours.</param>
    /// <param name="CenterDecDeg">Centre Dec in degrees.</param>
    /// <param name="RadiusArcmin">Ring radius on the sky in arcminutes.</param>
    /// <param name="Color">Stroke colour with alpha.</param>
    /// <param name="Label">Optional label (e.g. "5'") drawn at the ring edge.</param>
    public readonly record struct SkyRing(
        double CenterRaHours,
        double CenterDecDeg,
        float RadiusArcmin,
        RGBAColor32 Color,
        string? Label);

    /// <summary>
    /// Directed arrow drawn from one sky position to another, projected
    /// through the WCS at render time. Used by the polar-alignment overlay
    /// to point toward where a bright star (or, for our first pass, the
    /// frame centre) needs to move to bring the rotation axis onto the
    /// refracted pole — a SharpCap-style "follow this arrow" hint.
    /// </summary>
    /// <param name="StartRaHours">Tail RA in hours.</param>
    /// <param name="StartDecDeg">Tail Dec in degrees.</param>
    /// <param name="EndRaHours">Head RA in hours (where the user should drive the start point).</param>
    /// <param name="EndDecDeg">Head Dec in degrees.</param>
    /// <param name="Color">Stroke colour with alpha.</param>
    /// <param name="Label">Optional label drawn near the head; null suppresses.</param>
    /// <param name="ThicknessPx">Line thickness in screen pixels.</param>
    /// <param name="HeadSizePx">Arrow-head leg length in screen pixels.</param>
    public readonly record struct SkyArrow(
        double StartRaHours,
        double StartDecDeg,
        double EndRaHours,
        double EndDecDeg,
        RGBAColor32 Color,
        string? Label,
        float ThicknessPx,
        float HeadSizePx);

    /// <summary>
    /// Visual style for a <see cref="SkyMarker"/>. Each style maps to one of
    /// <see cref="ImageRendererBase{TSurface}"/>'s primitive draw methods.
    /// </summary>
    public enum SkyMarkerGlyph
    {
        /// <summary>Plus-shape cross (e.g. NCP marker).</summary>
        Cross,
        /// <summary>Filled dot.</summary>
        Dot,
        /// <summary>Hollow circle outline.</summary>
        Circle,
        /// <summary>Cross inscribed in a circle (e.g. mount-axis reticle).</summary>
        CircledCross,
    }
}
