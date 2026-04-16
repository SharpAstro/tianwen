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

    protected override void RenderSkyMap(
        ICelestialObjectDB db, RectF32 contentRect, string fontPath,
        ITimeProvider timeProvider, double siteLat, double siteLon,
        bool isPlanningTonight)
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

        // Fill background with a sky colour driven by the Sun's current altitude
        // (matches the planner's twilight zones). Only valid when viewing "tonight";
        // any other planning date falls back to night. VSOP87a is cached for 10s so
        // this stays out of the per-frame hot path.
        double sunAltDeg = isPlanningTonight
            ? State.GetSunAltitudeDegCached(timeProvider.GetUtcNow(), siteLat, siteLon)
            : double.NaN;
        var bg = SkyMapState.SkyBackgroundColorForSunAltitude(sunAltDeg);
        Renderer.FillRectangle(
            new RectInt(
                new PointInt((int)(contentRect.X + mapW), (int)(contentRect.Y + mapH)),
                new PointInt((int)contentRect.X, (int)contentRect.Y)),
            bg);

        // Build site context (needed for UBO horizon clipping and dynamic geometry)
        var site = SiteContext.Create(siteLat, siteLon, timeProvider);

        // Update UBO with current view + site for horizon clipping. Pass the current
        // frame-in-flight index so each swapchain image gets its own UBO copy and the
        // GPU never reads from a buffer the CPU is currently overwriting (the root cause
        // of the 1-frame label-vs-stars desync during fast pans).
        _pipeline.UpdateUbo(State, mapW, mapH, contentRect.X, contentRect.Y, site,
            renderer.Context.CurrentFrame);

        _horizonFloats.Clear();
        _meridianFloats.Clear();
        _altAzGridFloats.Clear();

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

        // Write dynamic geometry to the frame ring buffer
        var ctx = renderer.Context;
        var cmd = renderer.CurrentCommandBuffer;

        var horizonInfo = WriteToRingBuffer(ctx, _horizonFloats);
        var meridianInfo = WriteToRingBuffer(ctx, _meridianFloats);
        var altAzGridInfo = WriteToRingBuffer(ctx, _altAzGridFloats);

        // Draw all sky map layers
        _pipeline.Draw(cmd, State, mapW, mapH, contentRect.X, contentRect.Y,
            horizonInfo, meridianInfo, altAzGridInfo);

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

    /// <summary>
    /// Draws mosaic panel outlines for pinned targets that require multiple sensor
    /// pointings to cover. Thin grey semi-transparent rectangles at each panel centre,
    /// sized by the sensor FOV. Gives the user an immediate visual of the mosaic layout
    /// overlaid on the actual sky.
    /// </summary>
    protected override void RenderMosaicPanels(
        RectF32 contentRect, float dpiScale, double ppr, float cx, float cy)
    {
        if (State.MountOverlay is not { SensorFovDeg: { WidthDeg: > 0, HeightDeg: > 0 } fov })
        {
            return;
        }

        var halfW = fov.WidthDeg / 2.0;
        var halfH = fov.HeightDeg / 2.0;
        var panelColor = new RGBAColor32(0xDD, 0x44, 0x44, 0xB0); // red, 69% alpha
        var strokeWidth = Math.Max(1, (int)dpiScale);

        foreach (var (ra, dec, _, _, _) in State.MosaicPanels)
        {
            var cosDec = Math.Max(Math.Cos(double.DegreesToRadians(dec)), 0.01);
            var dRA = halfW / (15.0 * cosDec);

            // Project top-left and bottom-right corners — sufficient for an
            // axis-aligned outline since panels follow the RA/Dec grid.
            if (!SkyMapProjection.ProjectWithMatrix(ra - dRA, dec + halfH,
                    State.CurrentViewMatrix, ppr, cx, cy, out var tlX, out var tlY)
                || !SkyMapProjection.ProjectWithMatrix(ra + dRA, dec - halfH,
                    State.CurrentViewMatrix, ppr, cx, cy, out var brX, out var brY))
            {
                continue;
            }

            renderer.DrawRectangle(new RectInt(
                new PointInt((int)Math.Max(tlX, brX), (int)Math.Max(tlY, brY)),
                new PointInt((int)Math.Min(tlX, brX), (int)Math.Min(tlY, brY))),
                panelColor, strokeWidth);
        }
    }

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

        // Sensor FOV rectangle — project the 4 corners of the camera sensor's field
        // of view at the mount's current J2000 pointing. North-up initially (rotation=0);
        // a future plate-solve result would supply the actual rotation angle.
        if (mountOverlay.SensorFovDeg is { WidthDeg: > 0, HeightDeg: > 0 } fov)
        {
            var halfW = fov.WidthDeg / 2.0;
            var halfH = fov.HeightDeg / 2.0;
            var ra = mountOverlay.RaJ2000;
            var dec = mountOverlay.DecJ2000;
            // RA offset must account for cos(dec) foreshortening
            var cosDec = Math.Max(Math.Cos(double.DegreesToRadians(dec)), 0.01);
            var dRA = halfW / (15.0 * cosDec); // degrees to hours, corrected

            // 4 corners: TL, TR, BR, BL
            Span<(double RA, double Dec)> corners = stackalloc (double, double)[4];
            corners[0] = (ra - dRA, dec + halfH); // top-left
            corners[1] = (ra + dRA, dec + halfH); // top-right
            corners[2] = (ra + dRA, dec - halfH); // bottom-right
            corners[3] = (ra - dRA, dec - halfH); // bottom-left

            // Project all 4 corners
            Span<(float X, float Y)> projected = stackalloc (float, float)[4];
            var allProjected = true;
            for (var i = 0; i < 4; i++)
            {
                if (!SkyMapProjection.ProjectWithMatrix(corners[i].RA, corners[i].Dec,
                    State.CurrentViewMatrix, ppr, cx, cy, out var sx, out var sy))
                {
                    allProjected = false;
                    break;
                }
                projected[i] = (sx, sy);
            }

            if (allProjected)
            {
                // Sensor FOV outline in Stellarium-style red. Use DrawRectangle
                // for clean connected corners (FillRectangle bars leave gaps).
                var fovColor = new RGBAColor32(0xDD, 0x33, 0x33, 0xDD); // red, bright
                var strokeWidth = Math.Max(1, (int)(dpiScale * 1.5f));

                renderer.DrawRectangle(new RectInt(
                    new PointInt((int)Math.Max(projected[0].X, projected[2].X),
                                (int)Math.Max(projected[0].Y, projected[2].Y)),
                    new PointInt((int)Math.Min(projected[0].X, projected[2].X),
                                (int)Math.Min(projected[0].Y, projected[2].Y))),
                    fovColor, strokeWidth);

                // TODO: "up" tick for camera orientation once plate-solve rotation is available.
                // The naive north-up tick looked detached and confusing without rotation data.
            }
        }

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
