using System;
using System.Collections.Generic;
using System.Numerics;
using DIR.Lib;
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

            // Add cross-catalog indices, capped to keep the label block readable.
            // Some NGC/IC/UGC entries have 50+ cross-references (NED designations,
            // mirror entries, etc.) which would otherwise dump a wall of text over
            // a single object. Three lines is plenty for a user to recognize the
            // object; the catalog browser can show the full list on demand.
            const int MaxCrossIndices = 3;
            if (db.TryGetCrossIndices(idx, out var crossIndices))
            {
                var added = 0;
                foreach (var crossIdx in crossIndices)
                {
                    if (added >= MaxCrossIndices) break;

                    var crossCanon = crossIdx.ToCanonical();
                    // Skip Tycho entries (too verbose) and already-shown canonical
                    if (crossIdx.ToCatalog() != Catalog.Tycho2 && crossCanon != canonical)
                    {
                        lines.Add(crossCanon);
                        added++;
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
    /// Computes a label priority score (higher = more important) used to decide
    /// which labels to place when crowded. Factors in: has-common-name bonus,
    /// brightness (V_Mag), and on-sky size (shape major axis).
    /// </summary>
    /// <remarks>
    /// The score is a stable function of the object alone — it does not depend
    /// on the current viewport or on neighbouring objects. That is what makes
    /// priority-based label placement stable under panning: the relative order
    /// of items never changes frame-to-frame for a given catalog state.
    /// </remarks>
    public static float ComputeLabelPriority(CelestialObject obj, CatalogIndex idx, ICelestialObjectDB db)
    {
        var priority = 0f;

        // Having a common name (e.g. "Andromeda", "Sirius") is the strongest
        // signal that the object is culturally / observationally significant.
        if (obj.CommonNames.Count > 0)
        {
            priority += 6f;
        }

        // Brightness: V_Mag 0 contributes ~15, V_Mag 15 contributes 0. Objects
        // with unknown magnitude get a small baseline so they can still be
        // labeled in sparse regions.
        if (!Half.IsNaN(obj.V_Mag))
        {
            priority += Math.Max(0f, 15f - (float)obj.V_Mag);
        }
        else
        {
            priority += 2f;
        }

        // Size: log-scaled so a 1 deg object doesn't dominate a 0.1 deg one
        // by 10x. Capped so giant nebulae don't drown everything else.
        if (db.TryGetShape(idx, out var shape) && !Half.IsNaN(shape.MajorAxis))
        {
            var majorArcmin = (float)shape.MajorAxis;
            if (majorArcmin > 0f)
            {
                priority += Math.Clamp(MathF.Log10(majorArcmin + 1f), 0f, 3f);
            }
        }

        return priority;
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

        // Sort by magnitude (brightest first) with CatalogIndex as stable tiebreaker.
        // The tiebreaker is what keeps label placement from twitching when panning the
        // sky map — List<T>.Sort is unstable (QuickSort), and equal-magnitude objects
        // would otherwise swap order between frames, causing the collision loop to
        // hand out different label slots frame-to-frame.
        candidates.Sort((a, b) =>
        {
            var aMag = Half.IsNaN(a.Obj.V_Mag) ? 99.0 : (double)a.Obj.V_Mag;
            var bMag = Half.IsNaN(b.Obj.V_Mag) ? 99.0 : (double)b.Obj.V_Mag;
            var c = aMag.CompareTo(bMag);
            return c != 0 ? c : ((ulong)a.Index).CompareTo((ulong)b.Index);
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

            result.Add(new OverlayItem
            {
                ScreenX = cx,
                ScreenY = cy,
                RA = obj.RA,
                Dec = obj.Dec,
                Color = color,
                Marker = marker,
                LabelLines = lines,
                LabelPriority = ComputeLabelPriority(obj, idx, db),
                LabelSlotHint = (int)((ulong)idx & 3),
            });
        }

        return result;
    }

    /// <summary>
    /// Computes overlay items for the Sky Map tab. Parallel of <see cref="ComputeOverlays"/>
    /// but projects via <see cref="SkyMapProjection.ProjectWithMatrix"/> instead of a WCS,
    /// and derives FOV directly from <see cref="SkyMapState.FieldOfViewDeg"/> rather than
    /// from per-pixel plate scale. Used for the <c>[O]</c> object overlay on the sky map.
    /// </summary>
    /// <param name="state">Current sky map viewport (view matrix, FOV).</param>
    /// <param name="contentRect">Sky map area in screen pixels.</param>
    /// <param name="dpiScale">Backing-store DPI scale factor (marker sizes scale with this).</param>
    /// <param name="db">Celestial object database.</param>
    /// <param name="measureText">Text width measurement callback.</param>
    /// <param name="baseFontSize">Base font size before DPI scaling.</param>
    /// <param name="pinnedCatalogIndices">Catalog indices of pinned planner targets.
    /// Items in this set bypass the FOV-based magnitude cutoff, get <c>IsPinned = true</c>,
    /// and receive a boosted <see cref="OverlayItem.LabelPriority"/> so their labels are
    /// never dropped by the collision-avoidance logic. Pass an empty set when no targets
    /// are pinned.</param>
    /// <returns>Sorted list of overlay items (brightest first).</returns>
    public static List<OverlayItem> ComputeSkyMapOverlays(
        SkyMapState state,
        RectF32 contentRect,
        float dpiScale,
        ICelestialObjectDB db,
        Func<string, float, float> measureText,
        float baseFontSize,
        IReadOnlySet<CatalogIndex>? pinnedCatalogIndices = null)
    {
        var result = new List<OverlayItem>();

        if (contentRect.Width <= 0 || contentRect.Height <= 0)
        {
            return result;
        }

        // Projection parameters (must match the GPU vertex shader — see SkyMapProjection.ProjectWithMatrix)
        var viewMatrix = state.CurrentViewMatrix;
        var ppr = SkyMapProjection.PixelsPerRadian(contentRect.Height, state.FieldOfViewDeg);
        var cxView = contentRect.X + contentRect.Width * 0.5f;
        var cyView = contentRect.Y + contentRect.Height * 0.5f;

        // FOV-driven magnitude cutoffs share the viewer's heuristics; FOV is in arcminutes.
        var fovArcmin = state.FieldOfViewDeg * 60.0;
        var magCutoff = GetExtendedMagCutoff(fovArcmin);
        var starMagCutoff = GetStarMagCutoff(fovArcmin);

        // RA/Dec bounds: sample a 5x5 grid of viewport points and take the min/max.
        // A coarser 3x3 grid missed RA/Dec extent at certain viewing angles (especially
        // in Horizon mode where the equatorial grid is rotated relative to the viewport),
        // causing overlays to vanish on one side of the screen.
        Span<(double RA, double Dec)> corners = stackalloc (double, double)[25];
        var idx = 0;
        for (var iy = 0; iy < 5; iy++)
        {
            for (var ix = 0; ix < 5; ix++)
            {
                var sx = contentRect.X + ix * 0.25f * contentRect.Width;
                var sy = contentRect.Y + iy * 0.25f * contentRect.Height;
                corners[idx++] = SkyMapProjection.UnprojectWithMatrix(sx, sy, viewMatrix, ppr, cxView, cyView);
            }
        }

        double minRA = double.MaxValue, maxRA = double.MinValue;
        double minDec = double.MaxValue, maxDec = double.MinValue;
        foreach (var (ra, dec) in corners)
        {
            if (double.IsNaN(ra) || double.IsNaN(dec)) continue;
            minRA = Math.Min(minRA, ra);
            maxRA = Math.Max(maxRA, ra);
            minDec = Math.Min(minDec, dec);
            maxDec = Math.Max(maxDec, dec);
        }

        if (minRA > maxRA || minDec > maxDec)
        {
            return result;
        }

        // RA wraparound: if the projected span straddles 0h/24h, re-scan with shifted RA.
        var raWrapped = maxRA - minRA > 12.0;
        if (raWrapped)
        {
            double wrapMin = double.MaxValue, wrapMax = double.MinValue;
            foreach (var (ra, _) in corners)
            {
                if (double.IsNaN(ra)) continue;
                var raShifted = ra < 12.0 ? ra + 24.0 : ra;
                wrapMin = Math.Min(wrapMin, raShifted);
                wrapMax = Math.Max(wrapMax, raShifted);
            }
            minRA = wrapMin;
            maxRA = wrapMax;
        }

        // If a celestial pole is inside (or near) the view frustum, the 9-corner
        // sample's RA bounds are meaningless — every RA projects through the pole.
        // Detect pole-in-view directly by projecting both poles and widen to a full
        // RA/Dec sweep if either sits inside the viewport plus a cull-margin band.
        // This replaces a hard FieldOfViewDeg >= 90 switch that caused objects to
        // pop in/out as the user zoomed across 90 degrees.
        var polePadding = 200f;
        var poleInView =
            (SkyMapProjection.ProjectWithMatrix(0.0, 90.0, viewMatrix, ppr, cxView, cyView,
                out var npx, out var npy)
             && npx >= contentRect.X - polePadding && npx <= contentRect.X + contentRect.Width + polePadding
             && npy >= contentRect.Y - polePadding && npy <= contentRect.Y + contentRect.Height + polePadding)
            ||
            (SkyMapProjection.ProjectWithMatrix(0.0, -90.0, viewMatrix, ppr, cxView, cyView,
                out var spx, out var spy)
             && spx >= contentRect.X - polePadding && spx <= contentRect.X + contentRect.Width + polePadding
             && spy >= contentRect.Y - polePadding && spy <= contentRect.Y + contentRect.Height + polePadding);

        if (poleInView || state.FieldOfViewDeg >= 90.0)
        {
            minRA = 0.0;
            maxRA = 24.0;
            minDec = -90.0;
            maxDec = 90.0;
            raWrapped = false;
        }
        else
        {
            // Expand by 1 deg to catch near-edge objects
            minRA -= 1.0 / 15.0;
            maxRA += 1.0 / 15.0;
            minDec = Math.Max(-90.0, minDec - 1.0);
            maxDec = Math.Min(90.0, maxDec + 1.0);
        }

        var grid = db.DeepSkyCoordinateGrid;
        var seen = new HashSet<CatalogIndex>();
        var candidates = new List<(CatalogIndex Index, CelestialObject Obj, float ScreenX, float ScreenY)>();

        // Arcmin -> pixels (same derivation as used later for marker sizing). Hoisted
        // above the scan loop so the off-screen cull below can expand its margin to
        // cover large Barnard-class nebulae whose centres may lie well outside the
        // viewport while their body still intersects it — without this, large shapes
        // pop in/out as the user pans across them.
        var arcminToPixels = (float)(ppr * Math.PI / (180.0 * 60.0));

        // Dark nebulae dominate wide-FOV overlays (there are hundreds of Barnard
        // objects, many several degrees across). They only add useful detail at
        // narrow FOV, so skip them when zoomed out. Keep the threshold matched to
        // the mag cutoff breakpoints above (>5 deg = Messier-class only).
        var hideDarkNebulae = state.FieldOfViewDeg > 10.0;

        var decStep = 1.0;
        var raStep = 1.0 / 15.0;

        for (var dec = Math.Floor(minDec); dec <= maxDec; dec += decStep)
        {
            for (var ra = Math.Floor(minRA * 15.0) / 15.0; ra <= maxRA; ra += raStep)
            {
                var queryRA = ra;
                if (raWrapped && queryRA >= 24.0) queryRA -= 24.0;
                if (queryRA < 0.0) queryRA += 24.0;
                if (queryRA >= 24.0) queryRA -= 24.0;

                foreach (var catIdx in grid[queryRA, dec])
                {
                    if (!seen.Add(catIdx))
                    {
                        continue;
                    }

                    if (!db.TryLookupByIndex(catIdx, out var obj))
                    {
                        continue;
                    }

                    var isExtended = IsExtendedObjectType(obj.ObjectType);
                    var isStar = IsStarType(obj.ObjectType);

                    if (!isExtended && !isStar)
                    {
                        continue;
                    }

                    if (hideDarkNebulae && obj.ObjectType == ObjectType.DarkNeb
                        && !(pinnedCatalogIndices is not null && obj.Index != default && pinnedCatalogIndices.Contains(obj.Index)))
                    {
                        continue;
                    }

                    // Cross-catalog dedupe (e.g. HIP/HD/HR for the same star)
                    if (db.TryGetCrossIndices(catIdx, out var crossIndices))
                    {
                        var isDuplicate = false;
                        foreach (var crossIdx in crossIndices)
                        {
                            if (crossIdx != catIdx && seen.Contains(crossIdx))
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

                    // Pinned planner targets bypass magnitude cutoff and dark-nebula
                    // filter so the user always sees their planned targets on the map.
                    var isPinned = pinnedCatalogIndices is not null
                        && obj.Index != default
                        && pinnedCatalogIndices.Contains(obj.Index);

                    if (!isPinned)
                    {
                        var effectiveMagCutoff = isStar ? starMagCutoff : magCutoff;
                        if (!Half.IsNaN(obj.V_Mag) && (double)obj.V_Mag > effectiveMagCutoff)
                        {
                            continue;
                        }
                    }

                    // Project through the same view matrix the GPU uses
                    if (!SkyMapProjection.ProjectWithMatrix(obj.RA, obj.Dec, viewMatrix, ppr,
                            cxView, cyView, out var screenX, out var screenY))
                    {
                        continue;
                    }

                    // Off-screen cull with generous margin — for large shapes the centre
                    // may be far outside the viewport while the body still overlaps, so
                    // extend the margin by the on-screen semi-major axis (see viewer's
                    // ComputeOverlays for the equivalent WCS-based logic).
                    var margin = 100f;
                    if (db.TryGetShape(catIdx, out var earlyShape) && !Half.IsNaN(earlyShape.MajorAxis))
                    {
                        var shapeScreenPx = (float)((double)earlyShape.MajorAxis / 2.0 * arcminToPixels) + 50f;
                        if (shapeScreenPx > margin) margin = shapeScreenPx;
                    }
                    if (screenX < contentRect.X - margin || screenX > contentRect.X + contentRect.Width + margin ||
                        screenY < contentRect.Y - margin || screenY > contentRect.Y + contentRect.Height + margin)
                    {
                        continue;
                    }

                    candidates.Add((catIdx, obj, screenX, screenY));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return result;
        }

        // Sort by magnitude (brightest first) so collision avoidance favours bright labels
        candidates.Sort((a, b) =>
        {
            var aMag = Half.IsNaN(a.Obj.V_Mag) ? 99.0 : (double)a.Obj.V_Mag;
            var bMag = Half.IsNaN(b.Obj.V_Mag) ? 99.0 : (double)b.Obj.V_Mag;
            return aMag.CompareTo(bMag);
        });

        // Zoom-equivalent knob for label verbosity. At narrow FOV (zoomed in) we show more
        // cross-index detail; the viewer's BuildOverlayLabel uses an image-zoom scalar with
        // the same 0.5/1.0 breakpoints, so map FOV to an equivalent zoom value.
        var labelZoom = (float)Math.Clamp(10.0 / Math.Max(state.FieldOfViewDeg, 0.5), 0.25, 2.0);

        foreach (var (catIdx, obj, cx, cy) in candidates)
        {
            var isPinned = pinnedCatalogIndices is not null
                && obj.Index != default
                && pinnedCatalogIndices.Contains(obj.Index);

            var color = GetOverlayColor(obj.ObjectType);

            OverlayMarker marker;
            if (db.TryGetShape(catIdx, out var shape) &&
                !Half.IsNaN(shape.MajorAxis) && !Half.IsNaN(shape.MinorAxis))
            {
                var semiMajPx = (float)((double)shape.MajorAxis / 2.0 * arcminToPixels);
                var semiMinPx = (float)((double)shape.MinorAxis / 2.0 * arcminToPixels);

                {
                    // PA via tangent-plane trick: project a small RA/Dec step and measure the
                    // screen angle. Same technique as ComputeScreenPA but using the sky-map
                    // projection instead of WCS. Render at natural size even when tiny --
                    // falling back to a fixed-size circle creates visual noise at wide FOV.
                    var paScreen = ComputeSkyMapScreenPA(obj.RA, obj.Dec, shape.PositionAngle,
                        viewMatrix, ppr, cxView, cyView);
                    marker = new OverlayMarker.Ellipse(Math.Max(semiMajPx, 1f), Math.Max(semiMinPx, 0.5f), paScreen);
                }
            }
            else if (IsStarType(obj.ObjectType))
            {
                var arm = 6f * dpiScale;
                marker = new OverlayMarker.Cross(arm);
            }
            else
            {
                var markerRadius = 8f * dpiScale;
                marker = new OverlayMarker.Circle(markerRadius);
            }

            var lines = BuildOverlayLabel(obj, catIdx, db, labelZoom);

            // Pinned items get a large priority boost so their labels are never
            // dropped by collision avoidance. The +100 puts them well above any
            // natural ComputeLabelPriority score (~20 max for a bright named DSO).
            var priority = ComputeLabelPriority(obj, catIdx, db);
            if (isPinned) priority += 100f;

            result.Add(new OverlayItem
            {
                ScreenX = cx,
                ScreenY = cy,
                RA = obj.RA,
                Dec = obj.Dec,
                Color = color,
                Marker = marker,
                LabelLines = lines,
                IsPinned = isPinned,
                LabelPriority = priority,
                LabelSlotHint = (int)((ulong)catIdx & 3),
            });
        }

        return result;
    }

    /// <summary>
    /// Screen position angle for an object on the sky map. Mirrors <see cref="ComputeScreenPA"/>
    /// but projects via the sky-map view matrix instead of a WCS.
    /// </summary>
    private static float ComputeSkyMapScreenPA(double raH, double decDeg, Half paFromNorth,
        in Matrix4x4 viewMatrix, double ppr, float cxView, float cyView)
    {
        if (Half.IsNaN(paFromNorth))
        {
            return 0f;
        }

        var paRad = (double)paFromNorth * Math.PI / 180.0;
        var offsetDeg = 1.0 / 60.0; // 1 arcmin
        var dDecDeg = offsetDeg * Math.Cos(paRad);
        var dRADeg = offsetDeg * Math.Sin(paRad) / Math.Cos(decDeg * Math.PI / 180.0);
        var dRAH = dRADeg / 15.0;

        if (!SkyMapProjection.ProjectWithMatrix(raH, decDeg, viewMatrix, ppr, cxView, cyView,
                out var cx, out var cy) ||
            !SkyMapProjection.ProjectWithMatrix(raH + dRAH, decDeg + dDecDeg, viewMatrix, ppr,
                cxView, cyView, out var tx, out var ty))
        {
            return 0f;
        }

        var dx = tx - cx;
        var dy = -(ty - cy); // screen Y is flipped vs. sky-up
        return MathF.Atan2(dx, dy);
    }

    /// <summary>
    /// Places labels for the given overlay items using a 4-position collision-avoidance
    /// scheme shared between the FITS viewer and the sky map. The caller supplies the
    /// marker-and-label draw delegate; this helper owns the geometry and placed-label set.
    /// </summary>
    /// <param name="items">Overlay items (already sorted brightest-first).</param>
    /// <param name="labelSize">Font size in pixels for label lines.</param>
    /// <param name="labelPad">Padding in pixels between the marker and label box.</param>
    /// <param name="measureText">Callback: measure (text, fontSize) → pixel width.</param>
    /// <param name="drawLabelLines">Callback: draw a label block at (x, y) with the given
    /// base RGB color. The block's top-left is at (x, y); line-height is <paramref name="labelSize"/> * 1.2.</param>
    /// <param name="maxLabels">Label cap to prevent clutter. Defaults to <see cref="MaxOverlayLabels"/>.</param>
    public static void PlaceLabels(
        IReadOnlyList<OverlayItem> items,
        float labelSize,
        float labelPad,
        Func<string, float, float> measureText,
        Action<OverlayItem, float, float> drawLabelLines,
        int maxLabels = MaxOverlayLabels)
    {
        // Iterate in priority order (high -> low) so bright / named / large
        // objects claim their preferred slot first; lower-priority labels drop
        // silently when they collide. This produces stable placement under
        // panning — priority is a function of the object alone, not of
        // neighbours, so the relative order never flips frame-to-frame.
        // Tiebreaker on the reference hash code of the item gives a
        // deterministic order for exactly-equal priorities.
        var sorted = new List<OverlayItem>(items);
        sorted.Sort((a, b) =>
        {
            var c = b.LabelPriority.CompareTo(a.LabelPriority);
            return c != 0 ? c : a.LabelSlotHint.CompareTo(b.LabelSlotHint);
        });

        var placedLabels = new List<(float X, float Y, float W, float H)>();
        var labelCount = 0;

        foreach (var item in sorted)
        {
            if (labelCount >= maxLabels || item.LabelLines.Count == 0)
            {
                continue;
            }

            var cx = item.ScreenX;
            var cy = item.ScreenY;

            var maxLineW = 0f;
            foreach (var line in item.LabelLines)
            {
                var w = measureText(line, labelSize);
                if (w > maxLineW) maxLineW = w;
            }
            var lineH = labelSize * 1.2f;
            var totalH = lineH * item.LabelLines.Count;

            (float X, float Y)[] positions =
            [
                (cx + labelPad + 6f, cy - totalH / 2f),                 // 0 = right
                (cx - maxLineW - labelPad - 6f, cy - totalH / 2f),      // 1 = left
                (cx - maxLineW / 2f, cy - totalH - labelPad - 6f),      // 2 = above
                (cx - maxLineW / 2f, cy + labelPad + 6f),               // 3 = below
            ];

            // Start from the item's stable preferred slot so the same object keeps
            // the same label side across frames — otherwise panning causes labels to
            // fight for position 0 and reshuffle every frame.
            var startSlot = item.LabelSlotHint & 3;

            var placed = false;
            for (var p = 0; p < 4; p++)
            {
                var posIdx = (startSlot + p) & 3;
                var (lx, ly) = positions[posIdx];
                var overlaps = false;
                foreach (var (px, py, pw, ph) in placedLabels)
                {
                    if (lx < px + pw && lx + maxLineW > px && ly < py + ph && ly + totalH > py)
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (!overlaps)
                {
                    drawLabelLines(item, lx, ly);
                    placedLabels.Add((lx, ly, maxLineW, totalH));
                    placed = true;
                    labelCount++;
                    break;
                }
            }

            // If all 4 rotations collided, the label is dropped (no force fallback).
            // Because sorted is priority-ordered, the dropped labels are always the
            // least important ones in a dense region — stable and principled.
        }
    }
}
