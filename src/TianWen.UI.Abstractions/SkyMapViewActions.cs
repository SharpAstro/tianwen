using System;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Pure viewport mutations for the sky map. Renderer-agnostic - shared by the search
/// commit path, the DEBUG inspector's <c>SkyMapSetViewSignal</c> handler, and any other
/// caller that needs to recentre or rescale the view. Centralising the centring + FOV
/// clamp here keeps "where the map looks" to a single source of truth (the [0.5, 180]
/// FOV clamp must match <c>SkyMapTab</c>'s scroll-zoom clamp).
/// </summary>
public static class SkyMapViewActions
{
    /// <summary>Minimum vertical field of view in degrees (matches the scroll-zoom clamp).</summary>
    public const double MinFieldOfViewDeg = 0.5;

    /// <summary>Maximum vertical field of view in degrees (matches the scroll-zoom clamp).</summary>
    public const double MaxFieldOfViewDeg = 180.0;

    /// <summary>
    /// Recentre the viewport on a J2000 RA/Dec, normalising RA to [0, 24) and clamping
    /// Dec away from the projection poles. Flags a redraw. This is the single centring
    /// primitive - the search-commit path and the inspector view-control signal both
    /// route through it.
    /// </summary>
    public static void CenterOn(SkyMapState skyMap, double raHours, double decDeg)
    {
        skyMap.CenterRA = raHours;
        skyMap.CenterDec = decDeg;
        skyMap.NormalizeCenter();
        skyMap.NeedsRedraw = true;
    }

    /// <summary>
    /// Apply a partial viewport update: any non-null argument is applied, the rest are
    /// left as-is. FOV is clamped to [<see cref="MinFieldOfViewDeg"/>,
    /// <see cref="MaxFieldOfViewDeg"/>]. Always flags a redraw when anything changed.
    /// </summary>
    /// <returns>True when at least one field was applied.</returns>
    public static bool SetView(
        SkyMapState skyMap,
        double? centerRaHours = null,
        double? centerDecDeg = null,
        double? fieldOfViewDeg = null,
        bool? showObjectOverlay = null,
        bool? showDarkNebulae = null)
    {
        var changed = false;

        // Centre: only call CenterOn when at least one of RA/Dec is supplied, falling
        // back to the current value for the missing axis so a one-axis nudge works.
        if (centerRaHours.HasValue || centerDecDeg.HasValue)
        {
            CenterOn(skyMap, centerRaHours ?? skyMap.CenterRA, centerDecDeg ?? skyMap.CenterDec);
            changed = true;
        }

        if (fieldOfViewDeg is { } fov)
        {
            skyMap.FieldOfViewDeg = Math.Clamp(fov, MinFieldOfViewDeg, MaxFieldOfViewDeg);
            changed = true;
        }

        if (showObjectOverlay is { } showObjects)
        {
            skyMap.ShowObjectOverlay = showObjects;
            changed = true;
        }

        if (showDarkNebulae is { } showDark)
        {
            skyMap.ShowDarkNebulae = showDark;
            changed = true;
        }

        if (changed)
        {
            skyMap.NeedsRedraw = true;
        }

        return changed;
    }
}
