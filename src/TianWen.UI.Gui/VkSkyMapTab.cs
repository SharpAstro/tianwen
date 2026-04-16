using System;
using System.Collections.Generic;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;
using TianWen.UI.Shared;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Sky Map tab. Renders stars, constellation lines, grid, and horizon
/// via GPU shaders (<see cref="VkSkyMapPipeline"/>). Stars and static lines are stored
/// as J2000 unit vectors in persistent GPU buffers; projection happens in the vertex shader.
/// Text labels are drawn natively by the base class on top.
/// </summary>
public sealed unsafe class VkSkyMapTab(VkRenderer renderer) : SkyMapTab<VulkanContext>(renderer)
{
    private VkSkyMapPipeline? _pipeline;

    // Reusable lists for dynamic per-frame geometry
    private readonly List<float> _horizonFloats = new(2048);
    private readonly List<float> _meridianFloats = new(4096);
    private readonly List<float> _altAzGridFloats = new(4096);
    private readonly List<float> _fovFloats = new(256);

    protected override void RenderSkyMap(
        ICelestialObjectDB db, RectF32 contentRect, string fontPath,
        DateTimeOffset viewingTime, double siteLat, double siteLon)
    {
        var mapW = contentRect.Width;
        var mapH = contentRect.Height;
        if (mapW <= 0 || mapH <= 0)
        {
            return;
        }

        // Lazy-create the pipeline
        _pipeline ??= new VkSkyMapPipeline(renderer.Context);

        // Build persistent geometry once when catalog is available
        if (!_pipeline.GeometryReady)
        {
            _pipeline.BuildGeometry(db);
        }

        // Fill background with a sky colour driven by the Sun's altitude at the
        // viewing time (matches the planner's twilight zones). VSOP87a is cached
        // for 10s so this stays out of the per-frame hot path.
        double sunAltDeg = State.GetSunAltitudeDegCached(viewingTime, siteLat, siteLon);
        var bg = SkyMapState.SkyBackgroundColorForSunAltitude(sunAltDeg);
        Renderer.FillRectangle(
            new RectInt(
                new PointInt((int)(contentRect.X + mapW), (int)(contentRect.Y + mapH)),
                new PointInt((int)contentRect.X, (int)contentRect.Y)),
            bg);

        // Build site context (needed for UBO horizon clipping and dynamic geometry)
        var site = SiteContext.Create(siteLat, siteLon, viewingTime);

        // Update UBO with current view + site for horizon clipping. Pass the current
        // frame-in-flight index so each swapchain image gets its own UBO copy and the
        // GPU never reads from a buffer the CPU is currently overwriting (the root cause
        // of the 1-frame label-vs-stars desync during fast pans).
        _pipeline.UpdateUbo(State, mapW, mapH, contentRect.X, contentRect.Y, site,
            renderer.Context.CurrentFrame);

        _horizonFloats.Clear();
        _meridianFloats.Clear();
        _altAzGridFloats.Clear();
        _fovFloats.Clear();

        if (State.ShowHorizon && site.IsValid)
        {
            VkSkyMapPipeline.BuildHorizonLine(site, _horizonFloats);
        }

        if (site.IsValid)
        {
            VkSkyMapPipeline.BuildMeridianLine(site.LST, _meridianFloats);
        }

        if (State.ShowAltAzGrid && site.IsValid)
        {
            VkSkyMapPipeline.BuildAltAzGrid(site, _altAzGridFloats);
        }

        // Sensor FOV rectangle + mosaic panel outlines as GPU line geometry
        if (State.ShowMountOverlay && State.MountOverlay is { SensorFovDeg: { WidthDeg: > 0, HeightDeg: > 0 } fov } mountOvl)
        {
            BuildFovLines(mountOvl.RaJ2000, mountOvl.DecJ2000,
                fov.WidthDeg, fov.HeightDeg, _fovFloats);

            foreach (var (ra, dec, _, _, _) in State.MosaicPanels)
            {
                BuildFovLines(ra, dec, fov.WidthDeg, fov.HeightDeg, _fovFloats);
            }
        }

        // Write dynamic geometry to the frame ring buffer
        var ctx = renderer.Context;
        var cmd = renderer.CurrentCommandBuffer;

        var horizonInfo = WriteToRingBuffer(ctx, _horizonFloats);
        var meridianInfo = WriteToRingBuffer(ctx, _meridianFloats);
        var altAzGridInfo = WriteToRingBuffer(ctx, _altAzGridFloats);
        var fovInfo = WriteToRingBuffer(ctx, _fovFloats);

        // Draw all sky map layers
        _pipeline.Draw(cmd, State, mapW, mapH, contentRect.X, contentRect.Y,
            horizonInfo, meridianInfo, altAzGridInfo, fovInfo);

        // Restore the full-window viewport/scissor for text overlay rendering
        // (the pipeline sets a clipped viewport/scissor for the sky map area)
        var ctx2 = renderer.Context;
        var cmd2 = renderer.CurrentCommandBuffer;
        var api = ctx2.DeviceApi;
        Vortice.Vulkan.VkViewport fullVp = new()
        {
            x = 0, y = 0,
            width = ctx2.SwapchainWidth, height = ctx2.SwapchainHeight,
            minDepth = 0f, maxDepth = 1f
        };
        Vortice.Vulkan.VkRect2D fullScissor = new(0, 0, ctx2.SwapchainWidth, ctx2.SwapchainHeight);
        api.vkCmdSetViewport(cmd2, 0, 1, &fullVp);
        api.vkCmdSetScissor(cmd2, 0, fullScissor);
    }

    /// <summary>
    /// Draws the catalog object overlay on top of the cached sky-map texture.
    /// Reuses <see cref="OverlayEngine.ComputeSkyMapOverlays"/> (same catalog filtering
    /// / FOV-aware magnitude cutoff as the FITS viewer), <see cref="VkOverlayShapes"/>
    /// (same GPU ellipse / cross primitives as <see cref="VkImageRenderer"/>), and
    /// <see cref="OverlayEngine.PlaceLabels"/> (same 4-position collision-avoidance loop
    /// the viewer uses). Below-horizon objects are rendered with dimmed alpha, matching
    /// the treatment applied to constellation and planet labels.
    /// </summary>
    protected override void RenderObjectOverlay(
        ICelestialObjectDB db, RectF32 contentRect, float dpiScale, string fontPath,
        float baseFontSize, SiteContext site, bool dimBelowHorizon, PlannerState plannerState,
        bool showAllOverlays)
    {
        // Build pinned catalog-index set from planner proposals. Targets that have
        // a CatalogIndex can be matched against the overlay engine's spatial scan;
        // non-catalog targets (manually typed) would need a separate RA/Dec match
        // (deferred to a future pass).
        HashSet<CatalogIndex>? pinnedIndices = null;
        var proposals = plannerState.Proposals;
        if (proposals.Length > 0)
        {
            pinnedIndices = new HashSet<CatalogIndex>();
            foreach (var p in proposals)
            {
                if (p.Target.CatalogIndex is { } idx)
                {
                    pinnedIndices.Add(idx);
                }
            }
            if (pinnedIndices.Count == 0) pinnedIndices = null;
        }

        // Skip the full catalog scan entirely when the overlay is off AND there are
        // no pinned targets to show — avoids iterating ~100k spatial-index cells for
        // nothing when the user just wants a clean sky map.
        if (!showAllOverlays && pinnedIndices is null)
        {
            return;
        }

        var items = OverlayEngine.ComputeSkyMapOverlays(
            State, contentRect, dpiScale, db,
            (text, size) => Renderer.MeasureText(text.AsSpan(), fontPath, size).Width,
            baseFontSize,
            pinnedIndices);

        // When the full overlay is off, strip non-pinned items so only the user's
        // planned targets remain visible as landmarks.
        if (!showAllOverlays)
        {
            items.RemoveAll(item => !item.IsPinned);
        }

        if (items.Count == 0)
        {
            return;
        }

        // Draw markers. Pinned items get a bright orange-red halo ring drawn BEFORE
        // the normal marker so it sits behind as a glow. The halo is 1.5x the normal
        // marker size at 25% alpha -- visible enough to spot from a distance but
        // doesn't overpower the actual shape.
        var pinnedHaloColor = new RGBAColor32(0xFF, 0x60, 0x20, 0x50); // orange-red, 31% alpha

        foreach (var item in items)
        {
            var (r, g, b) = item.Color;
            var alpha = dimBelowHorizon && !site.IsAboveHorizon(item.RA, item.Dec) ? 0.35f : 1.0f;

            // Pinned halo (drawn first so it's behind the marker)
            if (item.IsPinned)
            {
                var haloRadius = 16f * dpiScale;
                switch (item.Marker)
                {
                    case OverlayMarker.Ellipse ellipse:
                        haloRadius = MathF.Max(ellipse.SemiMajorPx * 1.5f, haloRadius);
                        break;
                    case OverlayMarker.Circle circle:
                        haloRadius = MathF.Max(circle.RadiusPx * 1.5f, haloRadius);
                        break;
                }
                VkOverlayShapes.DrawEllipse(renderer, dpiScale,
                    item.ScreenX, item.ScreenY,
                    haloRadius, haloRadius, 0f,
                    pinnedHaloColor, 3f);
            }

            var color = item.IsPinned
                ? new RGBAColor32(0xFF, 0x70, 0x30, (byte)(alpha * 255)) // bright orange-red for pinned
                : new RGBAColor32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), (byte)(alpha * 255));

            switch (item.Marker)
            {
                case OverlayMarker.Ellipse ellipse:
                    VkOverlayShapes.DrawEllipse(renderer, dpiScale,
                        item.ScreenX, item.ScreenY,
                        ellipse.SemiMajorPx, ellipse.SemiMinorPx, ellipse.AngleRad,
                        color, 1.5f);
                    break;
                case OverlayMarker.Cross cross:
                    VkOverlayShapes.DrawCross(renderer, dpiScale,
                        item.ScreenX, item.ScreenY, cross.ArmPx, color);
                    break;
                case OverlayMarker.Circle circle:
                    VkOverlayShapes.DrawEllipse(renderer, dpiScale,
                        item.ScreenX, item.ScreenY,
                        circle.RadiusPx, circle.RadiusPx, 0f,
                        color, 1.5f);
                    break;
            }
        }

        // Labels -- shared collision-avoidance with the FITS viewer. Pinned items
        // already have +100 priority so their labels are placed first and never dropped.
        var labelSize = baseFontSize * dpiScale * 0.85f;
        var lineH = labelSize * 1.2f;
        OverlayEngine.PlaceLabels(items, labelSize, 4f,
            (text, size) => Renderer.MeasureText(text.AsSpan(), fontPath, size).Width,
            (item, lx, ly) =>
            {
                var (r, g, b) = item.IsPinned ? (1f, 0.44f, 0.19f) : item.Color;
                var alpha = dimBelowHorizon && !site.IsAboveHorizon(item.RA, item.Dec) ? 0.35f : 1.0f;
                for (var li = 0; li < item.LabelLines.Count; li++)
                {
                    var lineAlpha = (li == 0 ? 1.0f : 0.7f) * alpha;
                    var color = new RGBAColor32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), (byte)(lineAlpha * 255));
                    Renderer.DrawText(item.LabelLines[li].AsSpan(), fontPath, labelSize, color,
                        new RectInt(
                            new PointInt((int)(lx + 200), (int)(ly + (li + 1) * lineH)),
                            new PointInt((int)lx, (int)(ly + li * lineH))),
                        TextAlign.Near, TextAlign.Center);
                }
            });
    }

    // Mosaic panel outlines are now drawn by the GPU LinePipeline (see BuildFovLines
    // in RenderSkyMap). The base class RenderMosaicPanels is no longer overridden.

    /// <summary>
    /// Draws the Stellarium-style mount reticle at the connected mount's current
    /// J2000 pointing. The reticle colour encodes mount state: bright green while
    /// tracking on target, amber while slewing, grey when parked/idle. Mount name
    /// is labeled below the reticle so multi-setup users can tell at a glance which
    /// mount is tracking where.
    /// </summary>
    protected override void RenderMountOverlay(
        SkyMapMountOverlay mountOverlay, RectF32 contentRect, float dpiScale,
        string fontPath, float baseFontSize, double ppr, float cx, float cy)
    {
        if (!SkyMapProjection.ProjectWithMatrix(
                mountOverlay.RaJ2000, mountOverlay.DecJ2000,
                State.CurrentViewMatrix, ppr, cx, cy,
                out var screenX, out var screenY))
        {
            return;
        }

        // Skip if the mount is projected well off-screen — no point drawing a reticle
        // we can't see, and it keeps the label clutter off the info strip.
        const float margin = 100f;
        if (screenX < contentRect.X - margin || screenX > contentRect.X + contentRect.Width + margin
            || screenY < contentRect.Y - margin || screenY > contentRect.Y + contentRect.Height + margin)
        {
            return;
        }

        // Colour by state: slewing = amber (user attention), tracking = green, idle = grey.
        var color = mountOverlay.IsSlewing
            ? new RGBAColor32(0xFF, 0xB0, 0x40, 0xFF)   // amber
            : mountOverlay.IsTracking
                ? new RGBAColor32(0x40, 0xFF, 0x70, 0xFF) // bright green
                : new RGBAColor32(0xA0, 0xA0, 0xA0, 0xFF); // grey

        // Sensor FOV rectangle is now drawn by the GPU LinePipeline (see BuildFovLines
        // in RenderSkyMap). No CPU drawing needed here.
        // TODO: "up" tick for camera orientation once plate-solve rotation is available.

        VkOverlayShapes.DrawReticle(renderer, dpiScale,
            screenX, screenY,
            radius: 14f, armLength: 22f, gap: 6f,
            color: color, thickness: 2f);

        // Label: mount name + current RA/Dec below the reticle, matched to the reticle
        // colour. The coordinate readout makes "why is the reticle here?" trivial to
        // diagnose — if the mount is pointing at Eta Carinae but the user thinks it
        // should be at the pole, the label tells the truth immediately.
        var fontSize = baseFontSize * dpiScale;
        var lineH = fontSize * 1.2f;
        // No '+' for positive Dec — at small font a thin '+' was misread as '-' (and
        // vice versa) on the bug-hunt screenshots. Bare sign-when-negative is unambiguous.
        // Proper DMS punctuation (' for arcmin, " for arcsec) reads cleanly as a
        // sky coordinate vs the default ':' which looked like a time.
        var coordsText = $"RA {CoordinateUtils.HoursToHMS(mountOverlay.RaJ2000, hourSeparator: 'h', withFrac: false, minuteSeparator: 'm', secondSuffix: "s")}"
            + $"  Dec {CoordinateUtils.DegreesToDMS(mountOverlay.DecJ2000, withPlus: false, degreeSign: '\u00B0', withFrac: false, arcMinuteSign: '\u2032', arcSecondSign: "\u2033")}";

        DrawReticleLabel(mountOverlay.DisplayName, fontPath, fontSize, color,
            screenX, screenY + 20f * dpiScale, lineH);
        DrawReticleLabel(coordsText, fontPath, fontSize * 0.9f,
            new RGBAColor32(color.Red, color.Green, color.Blue, (byte)(color.Alpha * 0.8f)),
            screenX, screenY + 20f * dpiScale + lineH, lineH);
    }

    private void DrawReticleLabel(string text, string fontPath, float fontSize, RGBAColor32 color,
        float centerX, float topY, float lineH)
    {
        var (textW, _) = Renderer.MeasureText(text.AsSpan(), fontPath, fontSize);
        Renderer.DrawText(text.AsSpan(), fontPath, fontSize, color,
            new RectInt(
                new PointInt((int)(centerX + textW * 0.5f + 4), (int)(topY + lineH)),
                new PointInt((int)(centerX - textW * 0.5f - 4), (int)topY)),
            TextAlign.Center, TextAlign.Center);
    }

    /// <summary>
    /// Builds GPU line-list geometry for a sensor FOV rectangle at the given RA/Dec center.
    /// Computes 4 corner unit vectors via tangent-plane offsets (pole-safe, no RA/Dec
    /// singularity) and emits 4 line segments (8 vec3 pairs) into the float list.
    /// The vertex shader handles stereographic projection via the UBO view matrix.
    /// </summary>
    private static void BuildFovLines(
        double centerRA, double centerDec,
        double fovWidthDeg, double fovHeightDeg,
        List<float> floats)
    {
        // Center unit vector on the celestial sphere
        var raRad = centerRA * (Math.PI / 12.0);
        var decRad = centerDec * (Math.PI / 180.0);
        var (sinRA, cosRA) = Math.SinCos(raRad);
        var (sinDec, cosDec) = Math.SinCos(decRad);

        var fwdX = cosDec * cosRA;
        var fwdY = cosDec * sinRA;
        var fwdZ = sinDec;

        // Local east tangent: d(pos)/d(RA) normalized = (-sinRA, cosRA, 0)
        double eastX = -sinRA, eastY = cosRA, eastZ = 0.0;

        // Local north tangent: cross(forward, east)
        var northX = fwdY * eastZ - fwdZ * eastY;
        var northY = fwdZ * eastX - fwdX * eastZ;
        var northZ = fwdX * eastY - fwdY * eastX;

        var halfWRad = fovWidthDeg * 0.5 * (Math.PI / 180.0);
        var halfHRad = fovHeightDeg * 0.5 * (Math.PI / 180.0);

        // 4 corners: TL, TR, BR, BL
        Span<(float X, float Y, float Z)> corners = stackalloc (float, float, float)[4];
        ReadOnlySpan<(double eSign, double nSign)> offsets =
        [
            (-1, +1), // TL (west, north)
            (+1, +1), // TR (east, north)
            (+1, -1), // BR (east, south)
            (-1, -1), // BL (west, south)
        ];

        for (var i = 0; i < 4; i++)
        {
            var (eSign, nSign) = offsets[i];
            var px = fwdX + eastX * (eSign * halfWRad) + northX * (nSign * halfHRad);
            var py = fwdY + eastY * (eSign * halfWRad) + northY * (nSign * halfHRad);
            var pz = fwdZ + eastZ * (eSign * halfWRad) + northZ * (nSign * halfHRad);

            // Normalize back onto the unit sphere
            var pLen = Math.Sqrt(px * px + py * py + pz * pz);
            corners[i] = ((float)(px / pLen), (float)(py / pLen), (float)(pz / pLen));
        }

        // Emit 4 line segments as pairs of vec3 unit vectors (line-list topology)
        for (var i = 0; i < 4; i++)
        {
            var next = (i + 1) % 4;
            floats.Add(corners[i].X); floats.Add(corners[i].Y); floats.Add(corners[i].Z);
            floats.Add(corners[next].X); floats.Add(corners[next].Y); floats.Add(corners[next].Z);
        }
    }

    private static (Vortice.Vulkan.VkBuffer Buffer, uint ByteOffset, uint VertexCount) WriteToRingBuffer(
        VulkanContext ctx, List<float> floats)
    {
        if (floats.Count == 0)
        {
            return (default, 0, 0);
        }

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats);
        var byteOffset = ctx.WriteVertices(span);
        if (byteOffset == uint.MaxValue)
        {
            return (default, 0, 0); // ring buffer full — skip this frame
        }

        return (ctx.VertexBuffer, byteOffset, (uint)(floats.Count / 3));
    }
}
