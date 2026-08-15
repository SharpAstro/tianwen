using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
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
    /// Chooses the marker shape for an overlay object. The ellipse is gated on the object
    /// being an EXTENDED type (galaxy / nebula / cluster), NOT merely on a shape entry
    /// existing: a star can pick up a stray or cross-linked shape -- e.g. Antares (alpha
    /// Sco) sits inside the rho-Oph dark-cloud complex, so a nebula's shape can be
    /// cross-linked onto the star's catalog index -- and must still render as a cross,
    /// never an extended-object ellipse. Single source of truth shared by
    /// <see cref="ComputeOverlays"/>, <see cref="GatherSkyMapOverlayCandidates"/>, and the
    /// sky-map search selection marker (<c>SkyMapTab.TryDrawShapeMarker</c>).
    /// </summary>
    /// <param name="hasShape">Whether a usable (non-NaN) angular shape exists for the object.</param>
    public static OverlayMarkerKind ChooseMarkerKind(ObjectType type, bool hasShape) => type switch
    {
        _ when hasShape && IsExtendedObjectType(type) => OverlayMarkerKind.Ellipse,
        _ when IsStarType(type)                       => OverlayMarkerKind.Cross,
        _                                             => OverlayMarkerKind.Circle,
    };

    /// <summary>
    /// Returns a priority score for a common name (lower = better). Delegates to
    /// <see cref="CelestialObject.NamePriority"/> so label placement and the sky-map
    /// info panel agree on which name to show.
    /// </summary>
    public static int GetNamePriority(string name) => CelestialObject.NamePriority(name);

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

        // Primary label name. Delegate to CelestialObject.DisplayName so the overlay
        // label and the sky-map info panel (SkyMapInfoPanelData.FromCatalogObject, which
        // reads DisplayName) ALWAYS pick the same name. A bare lowest-score scan keeps the
        // FIRST equal-priority name in HashSet iteration order, which surfaced an arbitrary
        // short alias (e.g. "OPHIUCUS" instead of "Ophiuchus Molecular Cloud") for objects
        // carrying several IAU-style names. DisplayName breaks priority ties by
        // longest-then-alphabetical, so the two agree. DisplayName itself falls back to the
        // canonical designation when there are no common names; we keep null in that case so
        // the `?? canonical` below, and the "add canonical" logic further down, are unchanged.
        string? bestName = obj.CommonNames.Count > 0 ? obj.DisplayName : null;

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
    /// Single source of truth for overlay-ellipse orientation, shared by the CPU
    /// selection marker (<c>SkyMapTab.TryDrawShapeMarker</c>) and -- as a
    /// hand-maintained GPU mirror -- the sky-map overlay shader
    /// (<c>VkSkyMapPipeline.OverlayEllipseVertexSource</c>). The shader computes the
    /// equivalent angle form <c>totalAngle = atan2(north.y, north.x) - paFromNorth</c>
    /// and draws the major axis along <c>(cos(totalAngle), sin(totalAngle))</c>; keep
    /// the two in lockstep.
    /// <para>
    /// Given the screen-space direction of celestial north at the object (already
    /// projected; screen +x is right, +y is DOWN) and the position angle in radians
    /// measured from north toward east, returns the screen-space unit vectors of the
    /// ellipse major and minor axes. The sky map is east-left (see
    /// <see cref="SkyMapProjection"/>: "RA increases to the left"), so a positive PA
    /// rotates the major axis from north toward screen-left -- i.e. true sky position
    /// angle. At PA = 0 the major axis lies along celestial north.
    /// </para>
    /// </summary>
    /// <returns>Major and minor axis screen-space unit vectors. Falls back to the
    /// screen axes (1,0)/(0,1) when the supplied north direction is degenerate.</returns>
    public static (float MajorX, float MajorY, float MinorX, float MinorY)
        ComputeEllipseScreenAxes(float northX, float northY, float paRad)
    {
        var nlen = MathF.Sqrt(northX * northX + northY * northY);
        if (nlen < 1e-6f)
        {
            return (1f, 0f, 0f, 1f);
        }
        var nx = northX / nlen;
        var ny = northY / nlen;

        // East = north rotated -90 deg in screen space (east-left map): (ny, -nx).
        var ex = ny;
        var ey = -nx;

        var (sin, cos) = MathF.SinCos(paRad);
        // Major axis = cos(PA) * north + sin(PA) * east.
        var majorX = cos * nx + sin * ex;
        var majorY = cos * ny + sin * ey;
        // Minor axis is the major axis rotated +90 deg in screen space. The sign is
        // irrelevant for a symmetric ellipse, but matching the GPU keeps the two exact.
        var minorX = -majorY;
        var minorY = majorX;
        return (majorX, majorY, minorX, minorY);
    }

    /// <summary>Pinned-target halo geometry, shared by the CPU primitive overlay
    /// (<c>SkyMapTab.RenderObjectOverlayPrimitive</c>) and the GPU instanced overlay
    /// (<c>VkSkyMapTab</c>) so the two surfaces draw the same halo. The numbers used to be
    /// restated in both, in code and again in each comment.</summary>
    /// <remarks>1.5x the marker's own size, never under 16 px (dpi-scaled) on the semi-major
    /// axis, stroked 3 px. Feed the first two to <see cref="EllipseLegibilityScale"/> for an
    /// ellipse marker so the halo keeps the object's axis ratio.</remarks>
    public const float PinnedHaloScale = 1.5f;

    /// <inheritdoc cref="PinnedHaloScale"/>
    public const float PinnedHaloMinSemiMajorPx = 16f;

    /// <inheritdoc cref="PinnedHaloScale"/>
    public const float PinnedHaloStrokePx = 3f;

    /// <summary>
    /// The halo's colour, here for the same reason the geometry is: it is drawn by the object overlay
    /// for a pinned catalog target AND by the comet layer for a pinned comet (which cannot go through
    /// the overlay at all, since comets are not in the object DB), and a pinned target that changes
    /// colour depending on which layer happens to draw it is not a landmark. Callers scale
    /// <see cref="RGBAColor32.Alpha"/> for the wide-FOV fade rather than restating the RGB.
    /// </summary>
    public static readonly RGBAColor32 PinnedHaloColor = new(0xFF, 0x60, 0x20, 0x50);

    /// <summary>
    /// A pinned target's own marker colour (the ring inside <see cref="PinnedHaloColor"/>). Alpha is
    /// a placeholder: every caller substitutes the horizon / wide-FOV fade it resolved. Here for the
    /// same reason the halo colour is -- it was written out as three literals on the CPU path and
    /// again as three float divisions on the GPU one.
    /// </summary>
    public static readonly RGBAColor32 PinnedMarkerColor = new(0xFF, 0x70, 0x30, 0xFF);

    /// <summary>
    /// The alpha an overlay marker or label draws at: 0.35 when it is below the horizon and the
    /// caller dims those, times <paramref name="fovAlpha"/> unless it is pinned.
    /// </summary>
    /// <remarks>
    /// A pinned target is exempt from the wide-FOV fade but NOT from horizon dimming, which is the
    /// half that kept getting restated slightly differently: the rule appears once per marker kind
    /// per surface (ellipse instances, crosses, labels; desktop and browser), and a landmark that
    /// fades with zoom on one of them stops being a landmark.
    /// </remarks>
    public static float MarkerAlpha(
        bool isPinned, double ra, double dec, bool dimBelowHorizon, SiteContext site, float fovAlpha)
    {
        var alpha = dimBelowHorizon && !site.IsAboveHorizon(ra, dec) ? 0.35f : 1f;
        return isPinned ? alpha : alpha * fovAlpha;
    }

    /// <summary>
    /// Uniform scale factor that grows a projected ellipse to a legibility floor on its
    /// semi-major axis, never returning less than <paramref name="minScale"/>.
    /// <para>
    /// Shared by the three places that need a marker to stay visible when the object projects
    /// to a couple of pixels: the search selection ellipse
    /// (<c>SkyMapTab.TryDrawShapeMarker</c>) and the pinned-target halo on both the CPU
    /// (<c>SkyMapTab.DrawOverlayEllipse</c>) and GPU (<c>VkSkyMapTab</c>) overlay paths. The
    /// factor is deliberately UNIFORM (callers apply it to both semi-axes), so the marker
    /// keeps the object's real axis ratio and position angle. Sizing one axis alone (or
    /// substituting a circle of the major-axis radius, which both halo paths used to do) turns
    /// an elongated galaxy's marker into a circle, which is what made a selected or pinned
    /// ellipse read as a circle at ordinary zooms.
    /// </para>
    /// </summary>
    /// <param name="semiMajorPx">Projected semi-major axis in screen pixels.</param>
    /// <param name="minSemiMajorPx">Legibility floor for the semi-major axis, in screen pixels
    /// (callers pass a dpi-scaled value).</param>
    /// <param name="minScale">Scale applied even when the projected size already clears the
    /// floor; 1 keeps the true size, more sits outside the object's own outline.</param>
    /// <returns>A factor >= <paramref name="minScale"/>. A non-positive or non-finite
    /// <paramref name="semiMajorPx"/> yields <paramref name="minScale"/> rather than an
    /// infinite or NaN scale.</returns>
    public static float EllipseLegibilityScale(float semiMajorPx, float minSemiMajorPx, float minScale)
        => semiMajorPx > 0f && float.IsFinite(semiMajorPx)
            ? MathF.Max(minSemiMajorPx / semiMajorPx, minScale)
            : minScale;

    /// <summary>
    /// Computes a label priority score (higher = more important) used to decide
    /// which labels to place when crowded. Factors in: has-common-name bonus,
    /// brightness (V_Mag), and on-sky size (shape major axis).
    /// </summary>
    /// <remarks>
    /// The score is a stable function of the object alone; it does not depend
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

                    // Skip if off-screen: margin based on actual object extent
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
        // sky map: List<T>.Sort is unstable (QuickSort), and equal-magnitude objects
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
            var hasShape = db.TryGetShape(idx, out var shape)
                && !Half.IsNaN(shape.MajorAxis) && !Half.IsNaN(shape.MinorAxis);
            switch (ChooseMarkerKind(obj.ObjectType, hasShape))
            {
                case OverlayMarkerKind.Ellipse:
                {
                    var semiMajPx = (float)((double)shape.MajorAxis / 2.0 * arcminToPixels);
                    var semiMinPx = (float)((double)shape.MinorAxis / 2.0 * arcminToPixels);

                    // Skip tiny ellipses (< 3 pixels). continue targets the foreach.
                    if (semiMajPx < 3f)
                    {
                        continue;
                    }

                    var paScreen = ComputeScreenPA(wcs, obj.RA, obj.Dec, shape.PositionAngle);
                    marker = OverlayMarker.Ellipse(semiMajPx, semiMinPx, paScreen);
                    break;
                }
                case OverlayMarkerKind.Cross:
                {
                    var arm = 6f * layout.DpiScale;
                    marker = OverlayMarker.Cross(arm);
                    break;
                }
                default:
                {
                    var markerRadius = 8f * layout.DpiScale;
                    marker = OverlayMarker.Circle(markerRadius);
                    break;
                }
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
                StableSortKey = (ulong)idx,
            });
        }

        return result;
    }

    /// <summary>
    /// Computes the arcmin-to-screen-pixels scale factor for a given viewport height
    /// and FOV. Factored out so candidate gather and per-frame projection agree on
    /// how big an object of a given angular size should appear.
    /// </summary>
    public static float GetArcminToPixels(float viewportHeightPx, double fovDeg)
    {
        var ppr = SkyMapProjection.PixelsPerRadian(viewportHeightPx, fovDeg);
        return (float)(ppr * Math.PI / (180.0 * 60.0));
    }

    /// <summary>
    /// Field of view at or above which the candidate walk sweeps the WHOLE sphere, so the
    /// gathered set stops depending on where the view is pointed.
    ///
    /// <para>One constant rather than the three literal 90s it replaced, because the cache keys
    /// that drop the centre (and now the FOV) above this threshold are only correct if they use
    /// the same number the scan switches on. Two of the three lived in hand-maintained mirrors of
    /// each other, which is exactly the pair that silently drifts.</para>
    /// </summary>
    public const double WideFovDeg = 90.0;

    /// <summary>
    /// Minimum on-screen size, in pixels, for a dark nebula's label and outline to be worth
    /// drawing. Below this it is illegible clutter. Continuous in pixel space rather than the
    /// old binary FOV cut, so appearance fades smoothly with zoom instead of flickering a pile
    /// of labels in and out as a touch zoom crosses a boundary.
    /// </summary>
    public const float DarkNebulaMinOnScreenPx = 6f;

    /// <summary>
    /// Phase A: walk the spatial grid, filter, dedupe, and build label lines. Produces
    /// a view-matrix-independent list of <see cref="OverlayCandidate"/>s.
    /// </summary>
    /// <remarks>
    /// At wide FOV (>= 90 deg) the RA/Dec scan bounds are the whole sphere regardless
    /// of pan angle, so the gathered candidates are a pure function of FOV + rect +
    /// dpi + pins + DB. The GUI tab caches this list and re-projects every frame,
    /// which removes the per-pan grid walk and all per-rebuild List/HashSet allocs
    /// that previously made wide-FOV panning sluggish.
    /// </remarks>
    public static void GatherSkyMapOverlayCandidates(
        in Matrix4x4 viewMatrix,
        double fieldOfViewDeg,
        RectF32 contentRect,
        float dpiScale,
        ICelestialObjectDB db,
        IReadOnlySet<CatalogIndex>? pinnedCatalogIndices,
        List<OverlayCandidate> output)
    {
        // NOTE: this walk is the heavy Phase A pass (60-170ms in dense regions / pole-in-view).
        // It takes the view matrix + FOV by value rather than reading a live SkyMapState so it
        // can run on a background thread (see VkSkyMapTab's async gather) without tearing on the
        // render thread's view updates. It only reads the (immutable-after-init) catalog DB.
        output.Clear();

        if (contentRect.Width <= 0 || contentRect.Height <= 0)
        {
            return;
        }

        var ppr = SkyMapProjection.PixelsPerRadian(contentRect.Height, fieldOfViewDeg);
        var cxView = contentRect.X + contentRect.Width * 0.5f;
        var cyView = contentRect.Y + contentRect.Height * 0.5f;

        // FOV-driven magnitude cutoffs share the viewer's heuristics; FOV is in arcminutes.
        var fovArcmin = fieldOfViewDeg * 60.0;
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
            return;
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
        // sample's RA bounds are meaningless: every RA projects through the pole.
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

        // Scan margin beyond the sampled bounds. Sized to the Phase A cache
        // quantization in VkSkyMapTab.RenderObjectOverlay: the candidate cache is
        // keyed on the view centre quantized to FOV/8 cells, so the gathered set
        // must stay valid while the centre drifts anywhere inside a cell
        // (max step/2 * sqrt(2) ~= 0.09 * FOV). 0.15 * FOV covers that with slack;
        // the 1 deg floor keeps the old near-edge behaviour at narrow FOVs.
        var marginDeg = Math.Max(1.0, fieldOfViewDeg * 0.15);

        if (fieldOfViewDeg >= WideFovDeg)
        {
            // Whole sphere, and this branch is tested FIRST -- ahead of the pole case below --
            // because above the threshold the gathered set MUST NOT depend on the field of view.
            // That is what entitles the consumers' cache keys to drop the FOV (SkyMapTab.
            // BuildOverlayKey and its VkSkyMapTab mirror), and it is not a free-standing claim:
            // the magnitude cutoffs are already flat past 5 degrees and the dark-nebula pre-filter
            // is clamped to the threshold, so the scan bounds were the last FOV-dependent input.
            //
            // Ordered the other way round it silently was not. A pole in view took the branch
            // below, whose Dec bounds come from the corner sample and so move with the FOV -- and
            // at these fields of view a wide-angle projection folds those corners into a narrower
            // strip rather than a wider one. Measured on a real catalog at centre 18h -25 deg, the
            // gathered set differed by 959 objects between 90 and 120 degrees, ALL of them present
            // at 90 and missing at 120. Pinned by
            // SkyMapDarkNebulaScreenFilterTests.TheGatheredSetIsIdenticalAcrossTheWholeWideRange,
            // which is what caught it.
            minRA = 0.0;
            maxRA = 24.0;
            minDec = -90.0;
            maxDec = 90.0;
            raWrapped = false;
        }
        else if (poleInView)
        {
            // Every RA projects through the pole, so the corner sample's RA bounds
            // are meaningless: sweep the full 24 h. But the Dec bounds from the
            // 5x5 sample are still valid (the farthest-from-pole corners give the
            // visible declination edge), so we only scan the visible strip instead
            // of the whole sky. This cuts pole-in-view scan cost by ~5-10x at
            // moderate FOVs: a fix for pre-existing jerky pan performance.
            minRA = 0.0;
            maxRA = 24.0;
            raWrapped = false;
            minDec = Math.Max(-90.0, minDec - Math.Max(2.0, marginDeg));
            maxDec = Math.Min(90.0, maxDec + Math.Max(2.0, marginDeg));
        }
        else
        {
            // Expand to catch near-edge objects AND to keep the cached candidate set
            // valid while the quantized-centre cache key holds (see above). The RA
            // margin widens by 1/cos(dec) (clamped) so it covers the same on-sky
            // distance at high declinations.
            var maxAbsDec = Math.Min(Math.Max(Math.Abs(minDec), Math.Abs(maxDec)), 89.0);
            var cosDec = Math.Max(Math.Cos(maxAbsDec * Math.PI / 180.0), 0.05);
            minRA -= marginDeg / 15.0 / cosDec;
            maxRA += marginDeg / 15.0 / cosDec;
            minDec = Math.Max(-90.0, minDec - marginDeg);
            maxDec = Math.Min(90.0, maxDec + marginDeg);
        }

        var grid = db.DeepSkyCoordinateGrid;
        var seen = new HashSet<CatalogIndex>();

        // Arcmin -> pixels for the dark-nebula on-screen-size PRE-filter, deliberately computed
        // at the most permissive field of view this gather's cache key can be reused across --
        // NOT at the caller's actual FOV.
        //
        // Above WideFovDeg the scan already sweeps the whole sphere and both magnitude cutoffs
        // are flat, so the consumer's cache key drops the FOV and one gathered set has to serve
        // every FOV in [WideFovDeg, 180]. Admittance grows as the FOV narrows, so the union over
        // that range is exactly the set admitted AT the threshold. Clamping here is what makes
        // "the FOV is not in the key" true rather than nearly true.
        //
        // It stays a pre-filter, not the filter: this admits a superset, and
        // ProjectSkyMapCandidatesInto applies the exact test for the CURRENT view every frame.
        // Deleting the pre-filter outright would also be correct and is what a first attempt
        // reached for, but it costs far too much: only 190 of 4,827 shaped dark nebulae survive
        // at 180 degrees, so dropping it would inflate the cached set (and its label building) by
        // ~4,600 entries. Clamping to the threshold admits 1,655 instead.
        var filterPpr = SkyMapProjection.PixelsPerRadian(
            contentRect.Height, Math.Min(fieldOfViewDeg, WideFovDeg));
        var darkNebFilterArcminToPixels = (float)(filterPpr * Math.PI / (180.0 * 60.0));

        var decStep = 1.0;
        var raStep = 1.0 / 15.0;

        // Zoom-equivalent knob for label verbosity. At narrow FOV (zoomed in) we show more
        // cross-index detail; the viewer's BuildOverlayLabel uses an image-zoom scalar with
        // the same 0.5/1.0 breakpoints, so map FOV to an equivalent zoom value.
        var labelZoom = (float)Math.Clamp(10.0 / Math.Max(fieldOfViewDeg, 0.5), 0.25, 2.0);

        // Scratch list used so the magnitude sort happens before label/priority
        // construction -- keeps output in brightest-first order without sorting
        // OverlayCandidate (which owns a big reference-typed payload).
        var scratch = new List<(CatalogIndex CatIdx, CelestialObject Obj, bool IsPinned)>();

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

                    // Pin recognition has to cover obj.Index (canonical), catIdx (the spatial-grid
                    // key we happened to enter on), AND any cross-refs -- otherwise a pinned target
                    // indexed under a different catalog variant than the saved one would be missed.
                    // Computed UP FRONT so pinned planner targets bypass not only the magnitude and
                    // dark-nebula filters but also the object-TYPE gate below: otherwise a pinned
                    // target of a type the overlay doesn't normally draw (e.g. a Star Forming Region /
                    // molecular cloud, which is not an "extended" type) would be dropped here and
                    // never render, even though the user explicitly pinned it.
                    // CHEAP GATES FIRST. Everything below this point is only reachable by an object
                    // that will actually be kept, or that might be pinned. The two questions here cost
                    // a field read and a comparison; the cross-index closure below is, by this file's
                    // own account, the single most expensive question asked per candidate, and it used
                    // to be asked of EVERY object in every scanned cell with the magnitude test coming
                    // last. That is backwards precisely where it hurts most: past 90 degrees the walk
                    // sweeps the whole sphere (65,160 cells, the entire DSO catalog) while the cutoff
                    // is at its tightest -- GetExtendedMagCutoff returns 8.0, "Messier-class only" --
                    // so nearly every object paid for a cross-index lookup and was then dropped on
                    // magnitude.
                    var effectiveMagCutoffEarly = isStar ? starMagCutoff : magCutoff;
                    var magPasses = Half.IsNaN(obj.V_Mag) || (double)obj.V_Mag <= effectiveMagCutoffEarly;
                    var typePasses = isExtended || isStar;

                    // A pinned target bypasses both gates, so it can only be skipped here when there
                    // are no pins at all. With pins present the object still has to go the long way,
                    // because recognising one needs the cross-refs; the pinned set is tiny, so this
                    // costs nothing on the common path and stays exactly as correct on the rare one.
                    if (!(magPasses && typePasses) && pinnedCatalogIndices is null)
                    {
                        continue;
                    }

                    // ONE cross-index closure per object. The pinned check and the duplicate check
                    // below both need it, and it is the single most expensive question asked per
                    // candidate, so asking it twice doubled the cost of the whole walk.
                    var hasCrossIndices = db.TryGetCrossIndices(catIdx, out var crossIndices);

                    var isPinnedEarly = false;
                    if (pinnedCatalogIndices is not null)
                    {
                        if ((obj.Index != default && pinnedCatalogIndices.Contains(obj.Index))
                            || pinnedCatalogIndices.Contains(catIdx))
                        {
                            isPinnedEarly = true;
                        }
                        else if (hasCrossIndices)
                        {
                            foreach (var x in crossIndices)
                            {
                                if (pinnedCatalogIndices.Contains(x)) { isPinnedEarly = true; break; }
                            }
                        }
                    }

                    // Only extended objects (galaxies / nebulae / clusters) and stars are drawn --
                    // unless the object is pinned, in which case the user wants to see it regardless
                    // of its type.
                    if (!isExtended && !isStar && !isPinnedEarly)
                    {
                        continue;
                    }

                    // Screen-size PRE-filter for DarkNeb, at the clamped FOV (see above): admit
                    // anything that could be legible anywhere in this cache key's FOV range, and
                    // let the projection decide per frame. Pinned planner targets bypass it, as
                    // they bypass every other filter here. Entries without shape data (e.g. Simbad
                    // NAME-only, no VizieR match) are hidden entirely -- they'd otherwise clutter
                    // wide views with placeholder circles -- and THAT half is a property of the
                    // catalog rather than of the view, so it stays in the gather.
                    if (obj.ObjectType == ObjectType.DarkNeb && !isPinnedEarly)
                    {
                        if (!db.TryGetShape(catIdx, out var dnShape) || Half.IsNaN(dnShape.MajorAxis))
                        {
                            continue;
                        }
                        var dnScreenPx = (float)((double)dnShape.MajorAxis * darkNebFilterArcminToPixels);
                        if (dnScreenPx < DarkNebulaMinOnScreenPx)
                        {
                            continue;
                        }
                    }

                    // Cross-catalog dedupe (e.g. HIP/HD/HR for the same star)
                    if (hasCrossIndices)
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

                    // Pinned planner targets bypass the magnitude cutoff (and the type / dark-nebula
                    // filters above) so the user always sees their planned targets on the map. Reuse
                    // the full recognition from isPinnedEarly (obj.Index + catIdx + cross-refs) rather
                    // than an obj.Index-only check, so a target pinned under any catalog variant both
                    // survives the filters AND renders as the orange landmark.
                    var isPinned = isPinnedEarly;

                    if (!isPinned && !magPasses)
                    {
                        continue;
                    }

                    // Note: no per-candidate projection / off-screen cull here anymore.
                    // Projection is deferred to ProjectSkyMapCandidatesInto so a drag-pan
                    // just re-projects cached candidates instead of re-walking the grid.
                    scratch.Add((catIdx, obj, isPinned));
                }
            }
        }

        if (scratch.Count == 0)
        {
            return;
        }

        // Sort by magnitude (brightest first) so collision avoidance downstream
        // favours bright labels, and tie-break on catalog index so the order is
        // stable across rebuilds (label slot placement depends on this).
        scratch.Sort((a, b) =>
        {
            var aMag = Half.IsNaN(a.Obj.V_Mag) ? 99.0 : (double)a.Obj.V_Mag;
            var bMag = Half.IsNaN(b.Obj.V_Mag) ? 99.0 : (double)b.Obj.V_Mag;
            var c = aMag.CompareTo(bMag);
            return c != 0 ? c : ((ulong)a.CatIdx).CompareTo((ulong)b.CatIdx);
        });

        foreach (var (catIdx, obj, isPinned) in scratch)
        {
            OverlayCandidateMarker marker;
            var shapeKnown = db.TryGetShape(catIdx, out var shape);
            var hasShape = shapeKnown
                && !Half.IsNaN(shape.MajorAxis) && !Half.IsNaN(shape.MinorAxis);

            // Carried so the projection can re-apply the on-screen-size test at the actual FOV.
            // Read from the shape rather than off the marker: a dark nebula with a major axis but
            // no minor axis draws as a Circle, which has no angular size at all, and that is
            // precisely the entry a marker-derived size would silently stop filtering.
            var sizeFilterArcmin = obj.ObjectType == ObjectType.DarkNeb
                && shapeKnown && !Half.IsNaN(shape.MajorAxis)
                    ? (float)shape.MajorAxis
                    : float.NaN;

            switch (ChooseMarkerKind(obj.ObjectType, hasShape))
            {
                case OverlayMarkerKind.Ellipse:
                    marker = new OverlayCandidateMarker.Ellipse(
                        (float)((double)shape.MajorAxis / 2.0),
                        (float)((double)shape.MinorAxis / 2.0),
                        shape.PositionAngle);
                    break;
                case OverlayMarkerKind.Cross:
                    marker = new OverlayCandidateMarker.Cross(6f);
                    break;
                default:
                    marker = new OverlayCandidateMarker.Circle(8f);
                    break;
            }

            var color = GetOverlayColor(obj.ObjectType);
            var lines = BuildOverlayLabel(obj, catIdx, db, labelZoom);

            // Pinned items get a large priority boost so their labels are never
            // dropped by collision avoidance. The +100 puts them well above any
            // natural ComputeLabelPriority score (~20 max for a bright named DSO).
            var priority = ComputeLabelPriority(obj, catIdx, db);
            if (isPinned) priority += 100f;

            var (ux, uy, uz) = SkyMapState.RaDecToUnitVec(obj.RA, obj.Dec);
            output.Add(new OverlayCandidate
            {
                CatalogIndex = catIdx,
                ObjectType = obj.ObjectType,
                RA = obj.RA,
                Dec = obj.Dec,
                UnitVec = new Vector3((float)ux, (float)uy, (float)uz),
                Color = color,
                Marker = marker,
                LabelLines = lines,
                IsPinned = isPinned,
                LabelPriority = priority,
                LabelSlotHint = (int)((ulong)catIdx & 3),
                ScreenSizeFilterArcmin = sizeFilterArcmin,
            });
        }
    }

    /// <summary>
    /// Phase B: project cached <see cref="OverlayCandidate"/>s into <see cref="OverlayItem"/>s
    /// using the current view matrix + dpi. Cheap (no grid walk, no allocation of label lines
    /// or priority scores) -- intended to run every frame even during active drag-pan.
    /// </summary>
    public static void ProjectSkyMapCandidatesInto(
        IReadOnlyList<OverlayCandidate> candidates,
        SkyMapState state,
        RectF32 contentRect,
        float dpiScale,
        List<OverlayItem> output)
    {
        output.Clear();

        if (candidates.Count == 0 || contentRect.Width <= 0 || contentRect.Height <= 0)
        {
            return;
        }

        var viewMatrix = state.CurrentViewMatrix;
        var ppr = SkyMapProjection.PixelsPerRadian(contentRect.Height, state.FieldOfViewDeg);
        var cxView = contentRect.X + contentRect.Width * 0.5f;
        var cyView = contentRect.Y + contentRect.Height * 0.5f;
        var arcminToPixels = (float)(ppr * Math.PI / (180.0 * 60.0));

        for (var candIndex = 0; candIndex < candidates.Count; candIndex++)
        {
            var cand = candidates[candIndex];

            // The exact half of the dark-nebula on-screen-size rule. The gather admits a superset
            // (see OverlayCandidate.ScreenSizeFilterArcmin), so the decision for THIS field of
            // view is made here, per frame, where the real ppr is known. Pinned targets bypass it,
            // matching every other filter in the gather. NaN means the object has no size test.
            if (!float.IsNaN(cand.ScreenSizeFilterArcmin) && !cand.IsPinned
                && cand.ScreenSizeFilterArcmin * arcminToPixels < DarkNebulaMinOnScreenPx)
            {
                continue;
            }

            if (!SkyMapProjection.ProjectWithMatrix(cand.RA, cand.Dec, viewMatrix, ppr,
                    cxView, cyView, out var screenX, out var screenY))
            {
                continue;
            }

            // Off-screen cull with generous margin -- for large shapes the centre
            // may be far outside the viewport while the body still overlaps, so
            // extend the margin by the on-screen semi-major axis.
            var margin = 100f;
            if (cand.Marker is OverlayCandidateMarker.Ellipse ellipseCand)
            {
                var shapeScreenPx = ellipseCand.SemiMajArcmin * arcminToPixels + 50f;
                if (shapeScreenPx > margin) margin = shapeScreenPx;
            }
            if (screenX < contentRect.X - margin || screenX > contentRect.X + contentRect.Width + margin ||
                screenY < contentRect.Y - margin || screenY > contentRect.Y + contentRect.Height + margin)
            {
                continue;
            }

            OverlayMarker marker;
            switch (cand.Marker)
            {
                case OverlayCandidateMarker.Ellipse e:
                {
                    // Size is kept in pixels on the OverlayItem for any consumer that still
                    // uses the CPU draw path. The sky-map GPU pipeline reads arcmin + PA
                    // directly off the candidate, so AngleRad stays 0 and the per-candidate
                    // CPU screen-PA computation is skipped.
                    var semiMajPx = e.SemiMajArcmin * arcminToPixels;
                    var semiMinPx = e.SemiMinArcmin * arcminToPixels;
                    marker = OverlayMarker.Ellipse(MathF.Max(semiMajPx, 1f), MathF.Max(semiMinPx, 0.5f), 0f);
                    break;
                }
                case OverlayCandidateMarker.Cross c:
                    marker = OverlayMarker.Cross(c.ArmPxAtDpi1 * dpiScale);
                    break;
                case OverlayCandidateMarker.Circle c:
                    marker = OverlayMarker.Circle(c.RadiusPxAtDpi1 * dpiScale);
                    break;
                default:
                    continue;
            }

            output.Add(new OverlayItem
            {
                ScreenX = screenX,
                ScreenY = screenY,
                RA = cand.RA,
                Dec = cand.Dec,
                Color = cand.Color,
                Marker = marker,
                LabelLines = cand.LabelLines,
                IsPinned = cand.IsPinned,
                LabelPriority = cand.LabelPriority,
                LabelSlotHint = cand.LabelSlotHint,
                StableSortKey = (ulong)cand.CatalogIndex,
                CandidateIndex = candIndex,
            });
        }
    }

    /// <summary>
    /// Label order: higher <see cref="OverlayItem.LabelPriority"/> first, ties broken on the raw
    /// catalog-index bits. Negative means <paramref name="a"/> labels first.
    ///
    /// <para>The tiebreak is load-bearing rather than tidy: priority is a function of the object
    /// alone, so without it two equal-priority stars fought over the same slot and one flickered
    /// away each frame.</para>
    /// </summary>
    private static int CompareLabelOrder(OverlayItem a, OverlayItem b)
    {
        var c = b.LabelPriority.CompareTo(a.LabelPriority);
        return c != 0 ? c : a.StableSortKey.CompareTo(b.StableSortKey);
    }

    /// <summary>
    /// Yields overlay items in label order while doing only the work the caller actually consumes.
    ///
    /// <para><b>Why not just sort.</b> Both placement routines stop once <c>maxLabels</c> labels are
    /// down -- 80 by default -- but they used to copy the whole item list and sort it completely to
    /// find those 80. At a full-sky zoom that is ~7,800 items copied and sorted every frame to pick
    /// 80 of them, measured at 1.84 ms per frame on desktop .NET and paid again on every repaint
    /// (the browser has no render loop, so a gesture repaints per event). Heapify is O(n) and each
    /// pop is O(log n), so the same 80 cost O(n + 80 log n).</para>
    ///
    /// <para>It is LAZY rather than a bounded top-80 select, because the collision variant may walk
    /// well past 80 items: a label that cannot find a free slot is dropped and the next one gets a
    /// chance. Truncating the input would silently change which labels appear. Popping preserves
    /// the previous order exactly, element for element.</para>
    ///
    /// <para>Backed by <see cref="ArrayPool{T}"/>, so the steady state allocates nothing at all;
    /// the old form allocated a fresh <see cref="List{T}"/> of every item per frame. Callers must
    /// <see cref="Dispose"/> it -- via <c>try/finally</c>, since the draw callbacks can throw.</para>
    /// </summary>
    private struct LabelOrder
    {
        private OverlayItem[]? _heap;
        private int _count;

        public static LabelOrder Build(IReadOnlyList<OverlayItem> items)
        {
            var n = items.Count;
            var heap = ArrayPool<OverlayItem>.Shared.Rent(Math.Max(n, 1));
            for (var i = 0; i < n; i++)
            {
                heap[i] = items[i];
            }
            // Floyd's construction: sift down every internal node, bottom up. O(n), not O(n log n).
            for (var i = (n >> 1) - 1; i >= 0; i--)
            {
                SiftDown(heap, n, i);
            }
            return new LabelOrder { _heap = heap, _count = n };
        }

        public bool TryPop(out OverlayItem item)
        {
            var heap = _heap;
            if (heap is null || _count == 0)
            {
                item = null!;
                return false;
            }

            item = heap[0];
            _count--;
            if (_count > 0)
            {
                heap[0] = heap[_count];
                SiftDown(heap, _count, 0);
            }
            return true;
        }

        public void Dispose()
        {
            if (_heap is { } heap)
            {
                _heap = null;
                // Cleared on return: OverlayItem is a class holding label strings, so a pooled
                // array would otherwise keep a frame's worth of them reachable indefinitely.
                ArrayPool<OverlayItem>.Shared.Return(heap, clearArray: true);
            }
        }

        private static void SiftDown(OverlayItem[] heap, int count, int index)
        {
            while (true)
            {
                var left = 2 * index + 1;
                if (left >= count)
                {
                    return;
                }

                var best = left;
                var right = left + 1;
                if (right < count && CompareLabelOrder(heap[right], heap[left]) < 0)
                {
                    best = right;
                }
                if (CompareLabelOrder(heap[best], heap[index]) >= 0)
                {
                    return;
                }

                (heap[index], heap[best]) = (heap[best], heap[index]);
                index = best;
            }
        }
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
    /// <param name="reservedRegions">Screen-space boxes (x, y, w, h) that are already
    /// occupied by something the engine doesn't own, e.g. the live mount-reticle label,
    /// drawn later in a separate pass. Catalog labels treat them as pre-placed and stack
    /// around them, so the mount label is never overlapped by an object name.</param>
    public static void PlaceLabels(
        IReadOnlyList<OverlayItem> items,
        float labelSize,
        float labelPad,
        Func<string, float, float> measureText,
        Action<OverlayItem, float, float> drawLabelLines,
        int maxLabels = MaxOverlayLabels,
        IReadOnlyList<(float X, float Y, float W, float H)>? reservedRegions = null)
    {
        // Iterate in priority order (high -> low) so bright / named / large
        // objects claim their preferred slot first; lower-priority labels drop
        // silently when they collide. This produces stable placement under
        // panning -- priority is a function of the object alone, not of
        // neighbours, so the relative order never flips frame-to-frame.
        // The order (and its StableSortKey tiebreak) lives in LabelOrder, which yields it
        // lazily: this loop usually consumes ~80 of several thousand items, so the copy +
        // full sort this replaced was doing work for items it would never look at.
        // Seed the occupied set with any externally-owned boxes (the mount reticle label)
        // so the first catalog label that would land there is forced to another slot.
        var placedLabels = new List<(float X, float Y, float W, float H)>();
        if (reservedRegions is { Count: > 0 })
        {
            placedLabels.AddRange(reservedRegions);
        }
        var labelCount = 0;

        var order = LabelOrder.Build(items);
        try
        {
            while (order.TryPop(out var item))
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
                // the same label side across frames: otherwise panning causes labels to
                // fight for position 0 and reshuffle every frame.
                var startSlot = item.LabelSlotHint & 3;

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
                        labelCount++;
                        break;
                    }
                }

                // If all 4 rotations collided, the label is dropped (no force fallback).
                // Because the pop order is priority-ordered, the dropped labels are always
                // the least important ones in a dense region: stable and principled. It is
                // also why the order has to stay LAZY rather than a bounded top-N select --
                // a drop here lets the next item through, so this loop can walk well past
                // maxLabels before it is done.
            }
        }
        finally
        {
            order.Dispose();
        }
    }

    /// <summary>
    /// Stellarium-style best-effort label placement: each item is drawn at the
    /// slot dictated by its <see cref="OverlayItem.LabelSlotHint"/> (a deterministic
    /// function of the catalog index), with no inter-label collision check. Labels
    /// that happen to overlap simply overlap, which is what Stellarium does. The
    /// advantage is O(N) time and rock-stable placement under panning (the slot is
    /// a function of the item alone, so it never reshuffles frame-to-frame).
    /// </summary>
    /// <remarks>
    /// The FITS viewer keeps <see cref="PlaceLabels"/> because far fewer objects
    /// are in frame and overlapping labels materially hurt readability there; on
    /// the sky map, a full-FOV scan can return hundreds of candidates and the
    /// O(N^2) collision scan dominates the overlay cost.
    /// </remarks>
    public static void PlaceLabelsBestEffort(
        IReadOnlyList<OverlayItem> items,
        float labelSize,
        float labelPad,
        Func<string, float, float> measureText,
        Action<OverlayItem, float, float> drawLabelLines,
        int maxLabels = MaxOverlayLabels,
        IReadOnlyList<(float X, float Y, float W, float H)>? reservedRegions = null)
    {
        var labelCount = 0;
        var order = LabelOrder.Build(items);
        try
        {
            while (order.TryPop(out var item))
            {
                if (labelCount >= maxLabels || item.LabelLines.Count == 0)
                {
                    break;
                }

                var maxLineW = 0f;
                foreach (var line in item.LabelLines)
                {
                    var w = measureText(line, labelSize);
                    if (w > maxLineW) maxLineW = w;
                }
                var lineH = labelSize * 1.2f;
                var totalH = lineH * item.LabelLines.Count;

                // Slot is a pure function of the catalog index (see OverlayItem.LabelSlotHint),
                // so a given object always labels on the same side across frames.
                var slot = item.LabelSlotHint & 3;
                float lx, ly;
                switch (slot)
                {
                    case 1: // left
                        lx = item.ScreenX - maxLineW - labelPad - 6f;
                        ly = item.ScreenY - totalH / 2f;
                        break;
                    case 2: // above
                        lx = item.ScreenX - maxLineW / 2f;
                        ly = item.ScreenY - totalH - labelPad - 6f;
                        break;
                    case 3: // below
                        lx = item.ScreenX - maxLineW / 2f;
                        ly = item.ScreenY + labelPad + 6f;
                        break;
                    default: // 0 = right
                        lx = item.ScreenX + labelPad + 6f;
                        ly = item.ScreenY - totalH / 2f;
                        break;
                }

                // Best-effort placement has no inter-label collision check (overlapping labels
                // are accepted, Stellarium-style), but a reserved box belongs to something more
                // important than a catalog name (the mount reticle label); drop the few labels
                // that would land on it rather than letting them bury it.
                if (reservedRegions is { Count: > 0 })
                {
                    var blocked = false;
                    foreach (var (px, py, pw, ph) in reservedRegions)
                    {
                        if (lx < px + pw && lx + maxLineW > px && ly < py + ph && ly + totalH > py)
                        {
                            blocked = true;
                            break;
                        }
                    }
                    if (blocked)
                    {
                        continue;
                    }
                }

                drawLabelLines(item, lx, ly);
                labelCount++;
            }
        }
        finally
        {
            order.Dispose();
        }
    }
}
