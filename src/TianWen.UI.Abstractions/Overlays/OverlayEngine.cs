using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions.Overlays;

/// <summary>
/// Backend-agnostic overlay computation engine.
/// Computes which celestial objects are visible and produces <see cref="OverlayItem"/>s
/// ready for rendering by any backend (OpenGL, Skia, etc.).
/// </summary>
public static class OverlayEngine
{
    /// <summary>
    /// Maximum number of overlay labels to emit (to prevent clutter).
    /// </summary>
    public const int MaxOverlayLabels = 80;

    /// <summary>
    /// Object types considered "extended" (drawn as ellipses/markers).
    /// </summary>
    public static bool IsExtendedObjectType(ObjectType ot) => ot is
        ObjectType.Galaxy or ObjectType.PairG or ObjectType.GroupG or
        ObjectType.OpenCluster or ObjectType.GlobCluster or
        ObjectType.GalNeb or ObjectType.PlanetaryNeb or ObjectType.EmObj or
        ObjectType.HIIReg or ObjectType.RefNeb or ObjectType.DarkNeb or
        ObjectType.SNRemnant or ObjectType.Association or
        ObjectType.Unknown;

    /// <summary>
    /// Whether the object type is a star (single, double, variable, etc.).
    /// </summary>
    public static bool IsStarType(ObjectType ot) => ot.IsStar;

    /// <summary>
    /// Returns a priority score for a common name (lower = better).
    /// IAU proper names (e.g. "Sirius") > Bayer (e.g. "eta Ori") > Flamsteed (e.g. "28 Ori") > other.
    /// </summary>
    public static int GetNamePriority(string name)
    {
        if (name.Length == 0) return 100;

        // Flamsteed numbers start with a digit (e.g. "28 Ori")
        if (char.IsAsciiDigit(name[0])) return 3;

        // Bayer designations start with a Greek letter abbreviation (lowercase, e.g. "eta Ori", "alf CMa")
        if (char.IsAsciiLetterLower(name[0]) && name.Length > 2 && name.Contains(' ')) return 2;

        // IAU proper name or other named object (e.g. "Sirius", "Whirlpool Galaxy")
        if (char.IsAsciiLetterUpper(name[0])) return 1;

        return 50;
    }

    /// <summary>
    /// Returns overlay color (R, G, B) based on object type.
    /// </summary>
    public static (float R, float G, float B) GetOverlayColor(ObjectType ot) => ot switch
    {
        ObjectType.Galaxy or ObjectType.PairG or ObjectType.GroupG => (0.0f, 0.8f, 0.8f),       // cyan
        ObjectType.OpenCluster or ObjectType.GlobCluster or ObjectType.Association => (1.0f, 0.8f, 0.0f), // yellow
        ObjectType.PlanetaryNeb => (0.6f, 0.3f, 1.0f),  // purple
        ObjectType.DarkNeb => (0.6f, 0.6f, 0.6f),       // gray
        _ when ot.IsStar => (1.0f, 1.0f, 1.0f),       // white (stars)
        _ => (1.0f, 0.4f, 0.25f),                        // orange (emission, HII, reflection, SNR, etc.)
    };

    /// <summary>
    /// Builds label lines for an overlay object based on zoom level.
    /// ≤50%: best name only. 50-100%: name + catalog designation. ≥100%: all names + cross indices.
    /// </summary>
    public static List<string> BuildOverlayLabel(CelestialObject obj, CatalogIndex idx, ICelestialObjectDB db, float zoom)
    {
        var lines = new List<string>(4);
        var canonical = obj.Index.ToCanonical();

        // Get best common name (sorted by priority)
        string? bestName = null;
        if (obj.CommonNames.Count > 0)
        {
            var bestScore = int.MaxValue;
            foreach (var name in obj.CommonNames)
            {
                var score = GetNamePriority(name);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestName = name;
                }
            }
        }

        if (zoom <= 0.5f)
        {
            // Zoomed out: best name only, or catalog designation if no name
            lines.Add(bestName ?? canonical);
        }
        else if (zoom < 1.0f)
        {
            // Medium zoom: best name + catalog designation if different
            lines.Add(bestName ?? canonical);
            if (bestName is not null && bestName != canonical)
            {
                lines.Add(canonical);
            }
        }
        else
        {
            // Full zoom (≥100%): all common names + primary designation + cross indices
            lines.Add(bestName ?? canonical);

            // Add remaining common names (sorted by priority)
            if (obj.CommonNames.Count > 1)
            {
                var sortedNames = new List<(int Priority, string Name)>();
                foreach (var name in obj.CommonNames)
                {
                    if (name != bestName)
                    {
                        sortedNames.Add((GetNamePriority(name), name));
                    }
                }
                sortedNames.Sort((a, b) => a.Priority.CompareTo(b.Priority));
                foreach (var (_, name) in sortedNames)
                {
                    lines.Add(name);
                }
            }

            // Add canonical designation if not already shown as a common name
            if (bestName is not null && !obj.CommonNames.Contains(canonical))
            {
                lines.Add(canonical);
            }

            // Add cross-catalog indices
            if (db.TryGetCrossIndices(idx, out var crossIndices))
            {
                foreach (var crossIdx in crossIndices)
                {
                    var crossCanon = crossIdx.ToCanonical();
                    // Skip Tycho entries (too verbose) and already-shown canonical
                    if (crossIdx.ToCatalog() != Catalog.Tycho2 && crossCanon != canonical)
                    {
                        lines.Add(crossCanon);
                    }
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// Computes the screen-space position angle by projecting a small sky offset through the WCS.
    /// Returns angle in radians (0 = up on screen, clockwise positive).
    /// </summary>
    public static float ComputeScreenPA(WCS wcs, double raH, double decDeg, Half paFromNorth)
    {
        if (Half.IsNaN(paFromNorth))
        {
            return 0f;
        }

        var paDeg = (double)paFromNorth;
        var paRad = paDeg * Math.PI / 180.0;

        // Compute a small offset along the PA direction in sky coordinates
        var offsetArcmin = 1.0;
        var offsetDeg = offsetArcmin / 60.0;

        // PA is measured N through E on the sky
        var dDecDeg = offsetDeg * Math.Cos(paRad);
        var dRADeg = offsetDeg * Math.Sin(paRad) / Math.Cos(decDeg * Math.PI / 180.0);
        var dRAH = dRADeg / 15.0;

        var center = wcs.SkyToPixel(raH, decDeg);
        var tip = wcs.SkyToPixel(raH + dRAH, decDeg + dDecDeg);

        if (center is not { } c || tip is not { } t)
        {
            return 0f;
        }

        // Screen-space angle (note: screen Y increases downward, pixel Y increases upward)
        var dx = (float)(t.X - c.X);
        var dy = -(float)(t.Y - c.Y); // negate because screen Y is flipped
        return MathF.Atan2(dx, dy);
    }

    /// <summary>
    /// Computes the extended-object magnitude cutoff based on field-of-view in arcminutes.
    /// </summary>
    public static double GetExtendedMagCutoff(double fovArcmin) => fovArcmin switch
    {
        > 300.0 => 8.0,   // > 5 degrees: Messier-class only
        > 60.0 => 12.0,   // 1-5 degrees: bright NGC/IC
        _ => 20.0          // < 1 degree: show all
    };

    /// <summary>
    /// Computes the star magnitude cutoff based on field-of-view in arcminutes.
    /// </summary>
    public static double GetStarMagCutoff(double fovArcmin) => fovArcmin switch
    {
        > 300.0 => 1.0,   // > 5 degrees: only the very brightest
        > 120.0 => 2.5,   // 2-5 degrees: naked-eye bright
        > 60.0 => 4.0,    // 1-2 degrees: moderate
        > 30.0 => 5.5,    // 0.5-1 degrees
        _ => 7.0           // < 0.5 degrees: show fainter stars
    };

    /// <summary>
    /// Computes all overlay items for the current viewport.
    /// </summary>
    /// <param name="layout">Viewport geometry.</param>
    /// <param name="wcs">World Coordinate System for pixel ↔ sky conversions.</param>
    /// <param name="db">Celestial object database.</param>
    /// <param name="measureText">Callback to measure text width: (text, fontSize) → width in pixels.</param>
    /// <param name="baseFontSize">Base font size (before DPI scaling) for labels.</param>
    /// <returns>Sorted list of overlay items (brightest first).</returns>
    public static List<OverlayItem> ComputeOverlays(
        ViewportLayout layout,
        WCS wcs,
        ICelestialObjectDB db,
        Func<string, float, float> measureText,
        float baseFontSize)
    {
        var result = new List<OverlayItem>();

        if (layout.ImageWidth <= 0 || layout.ImageHeight <= 0)
        {
            return result;
        }

        var scale = layout.Zoom;
        var imgOffsetX = layout.ImageOffsetX;
        var imgOffsetY = layout.ImageOffsetY;

        // Use the full image extent for RA/Dec query (matching the WCS grid),
        // so overlays are found for all objects on the image regardless of pan position.
        // Off-screen culling below still clips to the visible viewport for rendering.
        var visLeft = 1.0;
        var visRight = (double)layout.ImageWidth;
        var visTop = 1.0;
        var visBottom = (double)layout.ImageHeight;

        // Compute FOV for zoom-dependent filtering
        var pixelScaleArcsec = wcs.PixelScaleArcsec;
        var viewImagePixels = MathF.Min(layout.AreaWidth, layout.AreaHeight) / scale;
        var fovArcmin = viewImagePixels * pixelScaleArcsec / 60.0;

        var magCutoff = GetExtendedMagCutoff(fovArcmin);
        var starMagCutoff = GetStarMagCutoff(fovArcmin);

        // Get RA/Dec bounds of the visible area
        var corners = new (double RA, double Dec)?[]
        {
            wcs.PixelToSky(visLeft, visTop),
            wcs.PixelToSky(visRight, visTop),
            wcs.PixelToSky(visLeft, visBottom),
            wcs.PixelToSky(visRight, visBottom),
            wcs.PixelToSky((visLeft + visRight) / 2, (visTop + visBottom) / 2),
        };

        double minRA = double.MaxValue, maxRA = double.MinValue;
        double minDec = double.MaxValue, maxDec = double.MinValue;
        foreach (var c in corners)
        {
            if (c is not { } sky)
            {
                continue;
            }
            minRA = Math.Min(minRA, sky.RA);
            maxRA = Math.Max(maxRA, sky.RA);
            minDec = Math.Min(minDec, sky.Dec);
            maxDec = Math.Max(maxDec, sky.Dec);
        }

        if (minRA > maxRA || minDec > maxDec)
        {
            return result;
        }

        // Handle RA wraparound
        var raWrapped = maxRA - minRA > 12.0;
        if (raWrapped)
        {
            double wrapMin = double.MaxValue, wrapMax = double.MinValue;
            foreach (var c in corners)
            {
                if (c is not { } sky) continue;
                var ra = sky.RA < 12.0 ? sky.RA + 24.0 : sky.RA;
                wrapMin = Math.Min(wrapMin, ra);
                wrapMax = Math.Max(wrapMax, ra);
            }
            minRA = wrapMin;
            maxRA = wrapMax;
        }

        // Expand bounds slightly (1 degree) to catch objects near edges
        minRA -= 1.0 / 15.0;
        maxRA += 1.0 / 15.0;
        minDec = Math.Max(-90.0, minDec - 1.0);
        maxDec = Math.Min(90.0, maxDec + 1.0);

        // Query the spatial index for candidate objects (deep-sky only, no Tycho2)
        var grid = db.DeepSkyCoordinateGrid;
        var seen = new HashSet<CatalogIndex>();
        var candidates = new List<(CatalogIndex Index, CelestialObject Obj, float ScreenX, float ScreenY)>();

        // Iterate over 1-degree RA/Dec cells covering the viewport
        var decStep = 1.0;
        var raStep = 1.0 / 15.0;

        for (var dec = Math.Floor(minDec); dec <= maxDec; dec += decStep)
        {
            for (var ra = Math.Floor(minRA * 15.0) / 15.0; ra <= maxRA; ra += raStep)
            {
                var queryRA = ra;
                if (raWrapped && queryRA >= 24.0) queryRA -= 24.0;
                if (queryRA < 0.0) queryRA += 24.0;

                foreach (var idx in grid[queryRA, dec])
                {
                    if (!seen.Add(idx))
                    {
                        continue;
                    }

                    if (!db.TryLookupByIndex(idx, out var obj))
                    {
                        continue;
                    }

                    var isExtended = IsExtendedObjectType(obj.ObjectType);
                    var isStar = IsStarType(obj.ObjectType);

                    if (!isExtended && !isStar)
                    {
                        continue;
                    }

                    // Deduplicate cross-catalog entries (e.g. HIP/HD/HR for the same star)
                    if (db.TryGetCrossIndices(idx, out var crossIndices))
                    {
                        var isDuplicate = false;
                        foreach (var crossIdx in crossIndices)
                        {
                            if (crossIdx != idx && seen.Contains(crossIdx))
                            {
                                isDuplicate = true;
                                break;
                            }
                        }
                        if (isDuplicate)
                        {
                            continue;
                        }
                    }

                    // Magnitude cutoff
                    var effectiveMagCutoff = isStar ? starMagCutoff : magCutoff;
                    if (!Half.IsNaN(obj.V_Mag) && (double)obj.V_Mag > effectiveMagCutoff)
                    {
                        continue;
                    }

                    // Project to pixel coordinates
                    var pixel = wcs.SkyToPixel(obj.RA, obj.Dec);
                    if (pixel is not { } px)
                    {
                        continue;
                    }

                    // Convert to screen coordinates
                    var screenX = imgOffsetX + (float)(px.X - 1) * scale;
                    var screenY = imgOffsetY + (float)(px.Y - 1) * scale;

                    // Skip if off-screen — margin based on actual object extent
                    var margin = 100f;
                    if (db.TryGetShape(idx, out var earlyShape) && !Half.IsNaN(earlyShape.MajorAxis))
                    {
                        var shapeScreenPx = (float)((double)earlyShape.MajorAxis / 2.0 * scale / (pixelScaleArcsec / 60.0)) + 50f;
                        if (shapeScreenPx > margin) margin = shapeScreenPx;
                    }
                    if (screenX < layout.AreaLeft - margin || screenX > layout.AreaLeft + layout.AreaWidth + margin ||
                        screenY < layout.AreaTop - margin || screenY > layout.AreaTop + layout.AreaHeight + margin)
                    {
                        continue;
                    }

                    candidates.Add((idx, obj, screenX, screenY));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return result;
        }

        // Sort by magnitude (brightest first)
        candidates.Sort((a, b) =>
        {
            var aMag = Half.IsNaN(a.Obj.V_Mag) ? 99.0 : (double)a.Obj.V_Mag;
            var bMag = Half.IsNaN(b.Obj.V_Mag) ? 99.0 : (double)b.Obj.V_Mag;
            return aMag.CompareTo(bMag);
        });

        var arcminToPixels = scale / (pixelScaleArcsec / 60.0);
        var labelSize = baseFontSize * layout.DpiScale * 0.85f;

        foreach (var (idx, obj, cx, cy) in candidates)
        {
            var color = GetOverlayColor(obj.ObjectType);

            OverlayMarker marker;
            if (db.TryGetShape(idx, out var shape) &&
                !Half.IsNaN(shape.MajorAxis) && !Half.IsNaN(shape.MinorAxis))
            {
                var semiMajPx = (float)((double)shape.MajorAxis / 2.0 * arcminToPixels);
                var semiMinPx = (float)((double)shape.MinorAxis / 2.0 * arcminToPixels);

                // Skip tiny ellipses (< 3 pixels)
                if (semiMajPx < 3f)
                {
                    continue;
                }

                var paScreen = ComputeScreenPA(wcs, obj.RA, obj.Dec, shape.PositionAngle);
                marker = new OverlayMarker.Ellipse(semiMajPx, semiMinPx, paScreen);
            }
            else if (IsStarType(obj.ObjectType))
            {
                var arm = 6f * layout.DpiScale;
                marker = new OverlayMarker.Cross(arm);
            }
            else
            {
                var markerRadius = 8f * layout.DpiScale;
                marker = new OverlayMarker.Circle(markerRadius);
            }

            var lines = BuildOverlayLabel(obj, idx, db, scale);
            var forcePlaceLabel = !Half.IsNaN(obj.V_Mag) && (double)obj.V_Mag < 10.0;

            result.Add(new OverlayItem
            {
                ScreenX = cx,
                ScreenY = cy,
                Color = color,
                Marker = marker,
                LabelLines = lines,
                ForcePlaceLabel = forcePlaceLabel,
            });
        }

        return result;
    }
}
