using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
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

    // Horizon, meridian, and Alt/Az grid are all pure f(site). LST is cached
    // to 1 s granularity via SkyMapTab._cachedLiveTime, so 59 out of 60 frames
    // per second have an identical LST; rebuild only when the LST actually
    // moves or a visibility toggle changes what we need to emit. -1.0 means
    // "invalid or not yet built" and never collides with a real LST in [0, 24).
    private (double Lst, bool Horizon, bool AltAz) _lastStaticGeomKey = (-1.0, false, false);

    // DSO overlay is split in two passes:
    //
    //   Phase A (heavy): OverlayEngine.GatherSkyMapOverlayCandidates walks the DSO
    //   spatial grid, filters by magnitude / dark-neb screen size, dedupes cross-
    //   catalog entries, and builds label lines + priority. Output is a list of
    //   OverlayCandidate structs that do NOT depend on the current view matrix.
    //
    //   Phase B (per-frame): OverlayEngine.ProjectSkyMapCandidatesInto projects each
    //   candidate through the current view matrix, culls off-screen, and emits
    //   OverlayItems. Cheap enough to run every frame during active pan.
    //
    // The cache key below only invalidates Phase A. At wide FOV (>= 90 deg) the
    // grid walk's RA/Dec bounds snap to the whole sphere regardless of pan, so
    // viewMatrix is explicitly dropped from the key -- that's where the sluggish-
    // at-110-deg frametime came from: the previous single-phase cache rebuilt the
    // whole list (grid walk + hundreds of List/HashSet allocs) on every mouse-move.
    // At narrow FOV we keep viewMatrix in the key because the scan bounds depend
    // on it; at narrow FOV the scan is bounded and rebuild cost is already small.
    private Matrix4x4 _overlayViewKey;
    private double _overlayFovKey = -1.0;
    private int _overlayRectWKey, _overlayRectHKey;
    private float _overlayDpiKey;
    private bool _overlayShowAllKey;
    private ImmutableArray<ProposedObservation> _overlayProposalsKey;
    private ICelestialObjectDB? _overlayDbKey;
    private readonly List<OverlayCandidate> _overlayCandidates = [];
    private readonly List<OverlayItem> _overlayItems = [];
    private readonly List<(OverlayItem Item, float X, float Y)> _overlayPlacedLabels = [];

    // Per-frame instance buffer for the overlay ellipse pipeline -- 11 floats per
    // instance (see OverlayEllipseVertexSource for layout: vec3 unit vector, vec2
    // arcmin size, float PA-from-north, float thickness, vec4 rgba). Reused across
    // frames to stay allocation-free during active pan. Only Ellipse / Circle
    // markers go in here; Cross markers (stars, low count at wide FOV) still use
    // the per-call DrawCross path.
    private const int OverlayEllipseFloatsPerInstance = 11;
    private readonly List<float> _overlayEllipseInstances = new(1024);

    /// <summary>FOV threshold at which the scan bounds become view-matrix independent
    /// (full-sky sweep). Keep in sync with the branch in <c>GatherSkyMapOverlayCandidates</c>.</summary>
    private const double WideFovThresholdDeg = 90.0;

    // Sticky "collision vs best-effort" label placement mode. The two modes
    // disagree about dropped labels and slot overrides, so flipping between
    // them as items.Count crosses a single threshold causes visible flicker
    // during touch zoom. Hysteresis: enter collision only below the low-water
    // mark, leave only above the high-water mark.
    private bool _useCollisionPlacement;

    protected override void RenderSkyMap(
        ICelestialObjectDB db, RectF32 contentRect, string fontPath,
        DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site)
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

        // Try loading the Milky Way texture from disk (once, after pipeline is ready)
        if (!_milkyWayLoadAttempted)
        {
            _milkyWayLoadAttempted = true;
            TryLoadMilkyWayTexture();
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

        // Update UBO with current view + site for horizon clipping. Pass the current
        // frame-in-flight index so each swapchain image gets its own UBO copy and the
        // GPU never reads from a buffer the CPU is currently overwriting (the root cause
        // of the 1-frame label-vs-stars desync during fast pans).
        _pipeline.UpdateUbo(State, mapW, mapH, contentRect.X, contentRect.Y, site,
            renderer.Context.CurrentFrame);

        _fovFloats.Clear();

        // Horizon (120 verts), meridian (200 verts), and Alt/Az grid (~1560 verts)
        // are all pure f(site). LST is quantized to 1 s via _cachedLiveTime, so
        // rebuilding 60x/sec wastes ~1880 trig calls per second. Invalidate only
        // on LST change, site validity change, or visibility toggle.
        var showHorizon = State.ShowHorizon && site.IsValid;
        var showAltAz = State.ShowAltAzGrid && site.IsValid;
        var staticKey = (site.IsValid ? site.LST : -1.0, showHorizon, showAltAz);
        if (!staticKey.Equals(_lastStaticGeomKey))
        {
            _horizonFloats.Clear();
            _meridianFloats.Clear();
            _altAzGridFloats.Clear();
            if (showHorizon)
            {
                VkSkyMapPipeline.BuildHorizonLine(site, _horizonFloats);
            }
            if (site.IsValid)
            {
                VkSkyMapPipeline.BuildMeridianLine(site.LST, _meridianFloats);
            }
            if (showAltAz)
            {
                VkSkyMapPipeline.BuildAltAzGrid(site, _altAzGridFloats);
            }
            _lastStaticGeomKey = staticKey;
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

        // Milky Way: fades with sun altitude. Fully visible below -18 deg (astro night),
        // zero at -6 deg (civil twilight). Also dims at wide FOV to avoid overpowering.
        float milkyWayAlpha = State.ShowMilkyWay && State.MilkyWayAvailable
            ? MathF.Max(MathF.Min((float)(-sunAltDeg - 6.0) / 12f, 1f), 0f)
              * MathF.Max(MathF.Min(40f / (float)State.FieldOfViewDeg, 1f), 0.3f)
            : 0f;

        // Draw all sky map layers
        _pipeline.Draw(cmd, State, mapW, mapH, contentRect.X, contentRect.Y,
            milkyWayAlpha, horizonInfo, meridianInfo, altAzGridInfo, fovInfo);

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
    /// Reuses <see cref="OverlayEngine.GatherSkyMapOverlayCandidates"/> (same catalog
    /// filtering / FOV-aware magnitude cutoff as the FITS viewer) +
    /// <see cref="OverlayEngine.ProjectSkyMapCandidatesInto"/> (per-frame projection),
    /// <see cref="VkOverlayShapes"/>
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
        var proposals = plannerState.Proposals;
        var viewMatrix = State.CurrentViewMatrix;
        var fov = State.FieldOfViewDeg;
        var rectW = (int)contentRect.Width;
        var rectH = (int)contentRect.Height;

        // Phase A cache hit: when FOV >= 90 deg the scan bounds are the whole sphere
        // so the candidate list is independent of viewMatrix. Below that threshold the
        // scan bounds are derived from the current view matrix, so it stays in the key.
        var wideFov = fov >= WideFovThresholdDeg;
        var cacheHit = ReferenceEquals(db, _overlayDbKey)
            && fov == _overlayFovKey
            && rectW == _overlayRectWKey
            && rectH == _overlayRectHKey
            && dpiScale == _overlayDpiKey
            && showAllOverlays == _overlayShowAllKey
            && proposals == _overlayProposalsKey
            && (wideFov || viewMatrix.Equals(_overlayViewKey));

        if (!cacheHit)
        {
            RebuildOverlayCandidates(db, contentRect, dpiScale, proposals, showAllOverlays);
            _overlayDbKey = db;
            _overlayViewKey = viewMatrix;
            _overlayFovKey = fov;
            _overlayRectWKey = rectW;
            _overlayRectHKey = rectH;
            _overlayDpiKey = dpiScale;
            _overlayShowAllKey = showAllOverlays;
            _overlayProposalsKey = proposals;
        }

        // Phase B: project cached candidates into screen items every frame. This is
        // the cheap pass (no grid walk, no label building) so pan stays smooth even
        // when the candidate list has hundreds of entries.
        _overlayItems.Clear();
        _overlayPlacedLabels.Clear();
        if (_overlayCandidates.Count == 0)
        {
            return;
        }
        OverlayEngine.ProjectSkyMapCandidatesInto(_overlayCandidates, State, contentRect,
            dpiScale, _overlayItems);

        if (_overlayItems.Count == 0)
        {
            return;
        }

        // Hybrid label-placement strategy keyed on candidate count -- same two modes
        // as before, but now evaluated against the per-frame projected count. The
        // sticky band between the low and high water marks avoids mode flip-flopping
        // during touch zoom when items.Count hovers near the threshold.
        const int collisionEnter = 60; // switch TO collision once count falls here
        const int collisionExit = 100; // switch FROM collision once count rises here
        _useCollisionPlacement = _useCollisionPlacement
            ? _overlayItems.Count <= collisionExit
            : _overlayItems.Count <= collisionEnter;

        var placementLabelSize = baseFontSize * dpiScale * 0.85f;
        var measureText = (string text, float size) => Renderer.MeasureText(text.AsSpan(), fontPath, size).Width;
        Action<OverlayItem, float, float> record = (item, lx, ly) => _overlayPlacedLabels.Add((item, lx, ly));
        if (_useCollisionPlacement)
        {
            OverlayEngine.PlaceLabels(_overlayItems, placementLabelSize, 4f, measureText, record);
        }
        else
        {
            OverlayEngine.PlaceLabelsBestEffort(_overlayItems, placementLabelSize, 4f, measureText, record);
        }

        // Overlay fade at wide FOV -- keep overlays readable when zoomed out.
        // At FOV <= 120 there is no fade; between 120 and 180 deg the alpha
        // falls towards a 0.55 floor. Only affects non-pinned items; pinned
        // planner targets stay full brightness regardless of zoom.
        var fovAlpha = MathF.Max(MathF.Min(120f / (float)fov, 1f), 0.55f);
        var pinnedHaloColor = new RGBAColor32(0xFF, 0x60, 0x20, (byte)(0x50 * fovAlpha));

        // Build the per-frame ellipse / circle instance buffer directly from the cached
        // candidate list. The vertex shader stereographic-projects each unit vector and
        // computes screen-space PA via a finite-difference, so we don't do any CPU
        // projection or atan2 screen-angle math here. Cross markers are drawn on the
        // per-item CPU path below (rectangle primitive, few stars at wide FOV).
        _overlayEllipseInstances.Clear();
        var pinnedHaloR = pinnedHaloColor.Red / 255f;
        var pinnedHaloG = pinnedHaloColor.Green / 255f;
        var pinnedHaloB = pinnedHaloColor.Blue / 255f;
        var pinnedHaloA = pinnedHaloColor.Alpha / 255f;

        // The shader scales arcmin -> px using the UBO's pixelsPerRadian, so any
        // marker whose size was defined in screen pixels (circles, pinned halos) has
        // to be converted to arcmin here using the current ppr. The round-trip is
        // exact since CPU and GPU share the same derivation.
        var ppr = SkyMapProjection.PixelsPerRadian(contentRect.Height, fov);
        var arcminToPx = (float)(ppr * Math.PI / (180.0 * 60.0));
        var pxToArcmin = 1f / arcminToPx;

        foreach (var cand in _overlayCandidates)
        {
            var (r, g, b) = cand.Color;
            var alpha = dimBelowHorizon && !site.IsAboveHorizon(cand.RA, cand.Dec) ? 0.35f : 1.0f;
            if (!cand.IsPinned) alpha *= fovAlpha;

            // Pinned halo (emitted first so it's behind the marker). 1.5x marker size
            // with a 16-px floor so a pinned planner target is visible at any zoom.
            if (cand.IsPinned)
            {
                var haloPx = 16f * dpiScale;
                switch (cand.Marker)
                {
                    case OverlayCandidateMarker.Ellipse e:
                        haloPx = MathF.Max(e.SemiMajArcmin * arcminToPx * 1.5f, haloPx);
                        break;
                    case OverlayCandidateMarker.Circle c:
                        haloPx = MathF.Max(c.RadiusPxAtDpi1 * dpiScale * 1.5f, haloPx);
                        break;
                }
                var haloArcmin = haloPx * pxToArcmin;
                AppendEllipseInstance(cand.UnitVec,
                    haloArcmin, haloArcmin, 0f, 3f,
                    pinnedHaloR, pinnedHaloG, pinnedHaloB, pinnedHaloA);
            }

            float mainR, mainG, mainB, mainA;
            if (cand.IsPinned)
            {
                mainR = 1f; mainG = 0x70 / 255f; mainB = 0x30 / 255f; mainA = alpha;
            }
            else
            {
                mainR = r; mainG = g; mainB = b; mainA = alpha;
            }

            switch (cand.Marker)
            {
                case OverlayCandidateMarker.Ellipse e:
                    // 1 px / 0.5 px floors keep tiny galaxies legible at wide FOV.
                    var semiMajArcmin = MathF.Max(e.SemiMajArcmin, pxToArcmin);
                    var semiMinArcmin = MathF.Max(e.SemiMinArcmin, 0.5f * pxToArcmin);
                    var paFromNorthRad = Half.IsNaN(e.PositionAngle)
                        ? 0f
                        : (float)((double)e.PositionAngle * Math.PI / 180.0);
                    AppendEllipseInstance(cand.UnitVec,
                        semiMajArcmin, semiMinArcmin, paFromNorthRad, 1.5f,
                        mainR, mainG, mainB, mainA);
                    break;
                case OverlayCandidateMarker.Circle c:
                    var circleArcmin = c.RadiusPxAtDpi1 * dpiScale * pxToArcmin;
                    AppendEllipseInstance(cand.UnitVec,
                        circleArcmin, circleArcmin, 0f, 1.5f,
                        mainR, mainG, mainB, mainA);
                    break;
                    // Cross: handled below via the _overlayItems loop, where we have the
                    // CPU-projected screen coords needed by DrawCross.
            }
        }

        // Crosses (stars): per-item CPU path. Uses _overlayItems (only candidates that
        // passed CPU projection + off-screen cull), so off-screen stars don't draw.
        foreach (var item in _overlayItems)
        {
            if (item.Marker.Kind != OverlayMarkerKind.Cross) continue;

            var (r, g, b) = item.Color;
            var alpha = dimBelowHorizon && !site.IsAboveHorizon(item.RA, item.Dec) ? 0.35f : 1.0f;
            if (!item.IsPinned) alpha *= fovAlpha;

            var crossColor = item.IsPinned
                ? new RGBAColor32(0xFF, 0x70, 0x30, (byte)(alpha * 255))
                : RGBAColor32.FromFloat(r, g, b, alpha);
            VkOverlayShapes.DrawCross(renderer, dpiScale,
                item.ScreenX, item.ScreenY, item.Marker.ArmPx, crossColor);
        }

        // Single instanced draw for all ellipse + circle markers (including pinned
        // halos). Replaces one vkCmdDraw per marker -- at wide FOV that's hundreds.
        if (_overlayEllipseInstances.Count > 0 && _pipeline is { } pipeline)
        {
            var ctx = renderer.Context;
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_overlayEllipseInstances);
            var instByteOffset = ctx.WriteVertices(span);
            if (instByteOffset != uint.MaxValue)
            {
                var instanceCount = (uint)(_overlayEllipseInstances.Count / OverlayEllipseFloatsPerInstance);
                pipeline.DrawOverlayEllipses(
                    renderer.CurrentCommandBuffer,
                    contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                    ctx.VertexBuffer, instByteOffset, instanceCount);

                // Restore full-window viewport/scissor so subsequent text labels (which
                // use window-absolute coords via Renderer.DrawText) render correctly.
                var api = ctx.DeviceApi;
                var cmd = renderer.CurrentCommandBuffer;
                Vortice.Vulkan.VkViewport fullVp = new()
                {
                    x = 0, y = 0,
                    width = ctx.SwapchainWidth, height = ctx.SwapchainHeight,
                    minDepth = 0f, maxDepth = 1f
                };
                Vortice.Vulkan.VkRect2D fullScissor = new(0, 0, ctx.SwapchainWidth, ctx.SwapchainHeight);
                api.vkCmdSetViewport(cmd, 0, 1, &fullVp);
                api.vkCmdSetScissor(cmd, 0, fullScissor);
            }
        }

        // Labels: redraw at cached positions every frame so horizon dimming stays
        // live. PlaceLabels (O(N^2) collision scan) only runs on cache miss.
        var labelSize = baseFontSize * dpiScale * 0.85f;
        var lineH = labelSize * 1.2f;
        foreach (var (item, lx, ly) in _overlayPlacedLabels)
        {
            var (r, g, b) = item.IsPinned ? (1f, 0.44f, 0.19f) : item.Color;
            var labelAlpha = dimBelowHorizon && !site.IsAboveHorizon(item.RA, item.Dec) ? 0.35f : 1.0f;
            if (!item.IsPinned) labelAlpha *= fovAlpha;
            for (var li = 0; li < item.LabelLines.Count; li++)
            {
                var lineAlpha = (li == 0 ? 1.0f : 0.7f) * labelAlpha;
                var color = RGBAColor32.FromFloat(r, g, b, lineAlpha);
                Renderer.DrawText(item.LabelLines[li].AsSpan(), fontPath, labelSize, color,
                    new RectInt(
                        new PointInt((int)(lx + 200), (int)(ly + (li + 1) * lineH)),
                        new PointInt((int)lx, (int)(ly + li * lineH))),
                    TextAlign.Near, TextAlign.Center);
            }
        }
    }

    /// <summary>
    /// Rebuilds <see cref="_overlayCandidates"/> via the view-matrix-independent gather
    /// pass. Called only on cache miss -- per-frame projection and label placement
    /// happen in <see cref="RenderObjectOverlay"/>, not here.
    /// </summary>
    private void RebuildOverlayCandidates(
        ICelestialObjectDB db, RectF32 contentRect, float dpiScale,
        ImmutableArray<ProposedObservation> proposals, bool showAllOverlays)
    {
        _overlayCandidates.Clear();

        // Build pinned catalog-index set from planner proposals. Targets that have
        // a CatalogIndex can be matched against the overlay engine's spatial scan;
        // non-catalog targets (manually typed) would need a separate RA/Dec match
        // (deferred to a future pass).
        HashSet<CatalogIndex>? pinnedIndices = null;
        if (proposals.Length > 0)
        {
            pinnedIndices = [];
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
        // no pinned targets to show -- avoids iterating ~100k spatial-index cells for
        // nothing when the user just wants a clean sky map.
        if (!showAllOverlays && pinnedIndices is null)
        {
            return;
        }

        OverlayEngine.GatherSkyMapOverlayCandidates(
            State, contentRect, dpiScale, db, pinnedIndices, _overlayCandidates);

        // When the full overlay is off, strip non-pinned candidates so only the
        // user's planned targets remain visible as landmarks.
        if (!showAllOverlays)
        {
            _overlayCandidates.RemoveAll(c => !c.IsPinned);
        }
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

    /// <summary>
    /// Appends one DSO overlay ellipse instance to <see cref="_overlayEllipseInstances"/>.
    /// Layout must match <c>OverlayEllipseVertexSource</c>'s vertex input (11 floats,
    /// stride 44 bytes: unit vector, size arcmin, PA from north, thickness, rgba).
    /// </summary>
    private void AppendEllipseInstance(
        Vector3 unitVec,
        float semiMajArcmin, float semiMinArcmin, float paFromNorthRad, float thickness,
        float r, float g, float b, float a)
    {
        _overlayEllipseInstances.Add(unitVec.X);
        _overlayEllipseInstances.Add(unitVec.Y);
        _overlayEllipseInstances.Add(unitVec.Z);
        _overlayEllipseInstances.Add(semiMajArcmin);
        _overlayEllipseInstances.Add(semiMinArcmin);
        _overlayEllipseInstances.Add(paFromNorthRad);
        _overlayEllipseInstances.Add(thickness);
        _overlayEllipseInstances.Add(r);
        _overlayEllipseInstances.Add(g);
        _overlayEllipseInstances.Add(b);
        _overlayEllipseInstances.Add(a);
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

    protected override void OnMilkyWayLoaded(ReadOnlySpan<byte> bgraData, int width, int height)
    {
        _pipeline?.LoadMilkyWayTexture(bgraData, width, height);
        State.MilkyWayAvailable = _pipeline?.HasMilkyWayTexture ?? false;
    }
}
