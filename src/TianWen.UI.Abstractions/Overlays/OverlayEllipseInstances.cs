using System;
using System.Collections.Generic;
using System.Numerics;
using DIR.Lib;
using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.UI.Abstractions.Overlays;

/// <summary>
/// Builds the per-instance vertex stream for the instanced overlay-ellipse pipeline -- the one
/// draw that replaces a per-marker CPU trace. Shared by the Vulkan pipeline
/// (<c>VkSkyMapPipeline.DrawOverlayEllipses</c>) and the WebGL one
/// (<c>WebGlSkyMapPipeline.DrawOverlay</c>), which run byte-identical shaders modulo GL's flipped
/// NDC Y.
///
/// <para>Nothing here projects: the vertex shader stereographic-projects each unit vector and
/// recovers the screen-space position angle by finite-differencing a point one arcmin north, so
/// this pass is pure geometry selection -- which markers exist, how big in ARCMINUTES, what
/// colour. That is also why it is worth sharing rather than mirroring: the halo sizing alone has
/// four rules (uniform scale, legibility floor, axis-ratio preservation, per-marker-kind
/// fallback) and both surfaces had to agree on all of them.</para>
///
/// <para><b>Cross markers are deliberately absent.</b> A cross is two straight strokes with no
/// angular extent, so it has nothing to gain from a projection the shader would do for it, and
/// giving the instance a kind discriminant would put a branch in front of every ellipse. Callers
/// draw crosses with line primitives from the projected items. At a full-sky zoom that is 772
/// markers against 7,072 here.</para>
/// </summary>
public static class OverlayEllipseInstances
{
    /// <summary>
    /// Floats per instance: <c>vec3 unitVec, vec2 sizeArcmin, float paFromNorth, float thickness,
    /// vec4 color</c>. Matches the attribute layout both pipelines declare.
    /// </summary>
    public const int FloatsPerInstance = 11;

    /// <summary>Stroke width of an ordinary (non-halo) marker ring, in pixels.</summary>
    public const float MarkerStrokePx = 1.5f;

    /// <summary>
    /// Appends one instance per ellipse / circle marker in <paramref name="candidates"/> (plus a
    /// halo instance ahead of each pinned one, so the halo draws underneath) to
    /// <paramref name="instances"/>, which is cleared first.
    /// </summary>
    /// <param name="candidates">The cached, view-independent candidate list.</param>
    /// <param name="instances">Destination float buffer; <see cref="FloatsPerInstance"/> per instance.</param>
    /// <param name="arcminToPx">Current arcminutes-to-pixels scale. Markers whose size is defined in
    /// SCREEN pixels (circles, the halo floor) are converted back to arcminutes with its reciprocal,
    /// because the shader only speaks arcminutes; the round trip is exact since both sides derive the
    /// scale from the same pixels-per-radian.</param>
    /// <param name="dpiScale">Surface DPI scale, applied to the pixel-defined sizes.</param>
    /// <param name="fovAlpha">Wide-FOV fade already resolved by the caller. Applied to non-pinned
    /// markers only.</param>
    /// <param name="dimBelowHorizon">Whether to dim objects currently below the horizon.</param>
    /// <param name="site">Site context for that horizon test.</param>
    /// <param name="pinnedMarkerColor">Colour of a pinned target's own marker, alpha ignored.</param>
    /// <param name="pinnedHaloColor">Colour of the halo behind it, alpha included -- the caller has
    /// already folded <paramref name="fovAlpha"/> into it.</param>
    public static void Build(
        IReadOnlyList<OverlayCandidate> candidates,
        List<float> instances,
        float arcminToPx,
        float dpiScale,
        float fovAlpha,
        bool dimBelowHorizon,
        SiteContext site,
        RGBAColor32 pinnedMarkerColor,
        RGBAColor32 pinnedHaloColor)
    {
        instances.Clear();
        if (candidates.Count == 0)
        {
            return;
        }

        var pxToArcmin = 1f / arcminToPx;
        var haloFloorPx = OverlayEngine.PinnedHaloMinSemiMajorPx * dpiScale;
        var haloR = pinnedHaloColor.RedF;
        var haloG = pinnedHaloColor.GreenF;
        var haloB = pinnedHaloColor.BlueF;
        var haloA = pinnedHaloColor.AlphaF;

        for (var i = 0; i < candidates.Count; i++)
        {
            var cand = candidates[i];
            if (cand.Marker is OverlayCandidateMarker.Cross)
            {
                continue;
            }

            var alpha = OverlayEngine.MarkerAlpha(
                cand.IsPinned, cand.RA, cand.Dec, dimBelowHorizon, site, fovAlpha);

            // Halo first so it lands behind the marker. An ELLIPSE marker gets an ELLIPSE halo --
            // one uniform scale on both semi-axes plus the marker's own position angle, so the halo
            // keeps the object's shape. Sizing a circle from the major axis alone (which both paths
            // used to do) puts a wide round halo around an edge-on galaxy.
            if (cand.IsPinned)
            {
                if (cand.Marker is OverlayCandidateMarker.Ellipse he)
                {
                    var haloScale = OverlayEngine.EllipseLegibilityScale(
                        he.SemiMajArcmin * arcminToPx, haloFloorPx, OverlayEngine.PinnedHaloScale);
                    // Same 1 px / 0.5 px floors as the marker below, so a catalog shape with a zero
                    // minor axis still traces a visible ring rather than a degenerate line.
                    Append(instances, cand.UnitVec,
                        MathF.Max(he.SemiMajArcmin * haloScale, pxToArcmin),
                        MathF.Max(he.SemiMinArcmin * haloScale, 0.5f * pxToArcmin),
                        PositionAngleRad(he),
                        OverlayEngine.PinnedHaloStrokePx,
                        haloR, haloG, haloB, haloA);
                }
                else
                {
                    var haloPx = cand.Marker is OverlayCandidateMarker.Circle hc
                        ? MathF.Max(hc.RadiusPxAtDpi1 * dpiScale * OverlayEngine.PinnedHaloScale, haloFloorPx)
                        : haloFloorPx;
                    var haloArcmin = haloPx * pxToArcmin;
                    Append(instances, cand.UnitVec, haloArcmin, haloArcmin, 0f,
                        OverlayEngine.PinnedHaloStrokePx, haloR, haloG, haloB, haloA);
                }
            }

            float r, g, b;
            if (cand.IsPinned)
            {
                r = pinnedMarkerColor.RedF;
                g = pinnedMarkerColor.GreenF;
                b = pinnedMarkerColor.BlueF;
            }
            else
            {
                (r, g, b) = cand.Color;
            }

            switch (cand.Marker)
            {
                case OverlayCandidateMarker.Ellipse e:
                    // 1 px / 0.5 px floors keep tiny galaxies legible at wide FOV.
                    Append(instances, cand.UnitVec,
                        MathF.Max(e.SemiMajArcmin, pxToArcmin),
                        MathF.Max(e.SemiMinArcmin, 0.5f * pxToArcmin),
                        PositionAngleRad(e),
                        MarkerStrokePx, r, g, b, alpha);
                    break;
                case OverlayCandidateMarker.Circle c:
                    var circleArcmin = c.RadiusPxAtDpi1 * dpiScale * pxToArcmin;
                    Append(instances, cand.UnitVec, circleArcmin, circleArcmin, 0f,
                        MarkerStrokePx, r, g, b, alpha);
                    break;
            }
        }
    }

    /// <summary>An unknown catalog position angle draws unrotated rather than not at all.</summary>
    private static float PositionAngleRad(OverlayCandidateMarker.Ellipse e)
        => Half.IsNaN(e.PositionAngle) ? 0f : (float)((double)e.PositionAngle * Math.PI / 180.0);

    private static void Append(
        List<float> instances, Vector3 unitVec,
        float semiMajArcmin, float semiMinArcmin, float paFromNorthRad, float thickness,
        float r, float g, float b, float a)
    {
        instances.Add(unitVec.X);
        instances.Add(unitVec.Y);
        instances.Add(unitVec.Z);
        instances.Add(semiMajArcmin);
        instances.Add(semiMinArcmin);
        instances.Add(paFromNorthRad);
        instances.Add(thickness);
        instances.Add(r);
        instances.Add(g);
        instances.Add(b);
        instances.Add(a);
    }
}
