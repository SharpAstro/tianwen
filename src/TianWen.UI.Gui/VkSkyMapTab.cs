using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Threading.Tasks;
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
    // Phase A (candidate gather) cache key. The view contribution is the UNPROJECTED
    // view centre QUANTIZED to FOV/8 cells plus the FOV quantized to ~10% steps -- NOT
    // the exact view matrix. A drag/pan or wheel zoom therefore only invalidates the
    // cache when the centre crosses a quantization cell (or the zoom moves ~10%), and
    // the gather's scan margin (see GatherSkyMapOverlayCandidates) guarantees the
    // cached candidate set still covers every view inside the cell. Keying on the raw
    // matrix made the expensive catalog grid scan re-run on EVERY pan frame below the
    // wide-FOV threshold -- the "jank when the SCP comes into view at ~70 deg FOV"
    // (pole-in-view scans a full-RA Dec strip, the worst case).
    // Identity of a Phase A gather: the quantized view key plus the catalog/visibility
    // inputs the walk depends on. Record-struct equality compares ImmutableArray by its
    // underlying-array reference and ICelestialObjectDB by reference (matches the old
    // per-field comparison). When the FOV is wide the centre is dropped from the key
    // (Ra=Dec=0) so the candidate set is centre-independent up there, exactly like before.
    private readonly record struct OverlayGatherKey(
        ICelestialObjectDB? Db,
        ImmutableArray<ProposedObservation> Proposals,
        double Ra, double Dec, double Fov,
        int RectW, int RectH, float Dpi,
        bool ShowAll, bool ShowDark);

    // Result handed back from the background walk: a freshly-built candidate list plus the
    // key it was computed for (so the render thread knows what view it corresponds to).
    private readonly record struct OverlayGatherResult(
        List<OverlayCandidate> Candidates, OverlayGatherKey Key);

    // Async Phase A. The candidate walk is a 60-170ms grid scan; running it inline on the
    // render thread made a fast pan re-trigger it every frame (slow gather -> the view had
    // already moved -> cache miss -> slow gather), which was THE residual sky-map jank.
    // Mirror the star-buffer pattern (StartStarBufferRebuildAsync / TryApplyPendingStarBuild):
    // the walk runs on a Task, the render thread keeps projecting + drawing the last-good
    // candidate list EVERY frame, and the fresh list is swapped in when the task lands. Only
    // one walk is in flight at a time; if the view moved on by the time it lands we apply it
    // anyway (it is still closer than what we have) and the next frame kicks a fresh one.
    private Task<OverlayGatherResult>? _overlayGatherTask;
    private OverlayGatherKey _overlayAppliedKey;
    private bool _overlayHasAppliedKey;
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

    /// <summary>
    /// True while the pipeline is showing the HIP bright-star seed and the full Tycho-2 star
    /// buffer is still building in the background (drives the base class's "Loading stars..."
    /// hint). Once geometry exists but the full buffer hasn't been installed yet, this is true;
    /// it flips false the moment the async Tycho-2 build swaps in.
    /// </summary>
    protected override bool FullStarsLoading => _pipeline is { GeometryReady: true, FullStarsReady: false };

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

        // Lazy-create the pipeline. Wire RequestRedraw so a completed async star build wakes
        // the NeedsRedraw-gated render loop and lets TryApplyPendingStarBuild swap the full
        // Tycho-2 buffer in -- without it the swap frame would not fire while the user sits
        // idle on the tab after the first (HIP-seed) frame paints.
        if (_pipeline is null)
        {
            _pipeline = new VkSkyMapPipeline(renderer.Context);
            _pipeline.RequestRedraw = () => State.NeedsRedraw = true;
        }

        // Build persistent geometry on first frame after catalog is available, and
        // request a Tycho-2 star buffer rebuild with pm propagation whenever
        // viewingTime crosses a month boundary. BuildGeometry kicks off async rebuilds
        // (mirroring ViewerController._loadTask) -- the actual GPU swap happens on
        // this render thread inside TryApplyPendingStarBuild on a later frame, so the
        // user never sees a freeze. Both calls early-return cheaply on the common
        // path where no work is pending.
        _pipeline.BuildGeometry(db, viewingTime);
        _pipeline.TryApplyPendingStarBuild();
        // Apply a completed async Milky Way decode (GPU upload on this render thread).
        TryApplyPendingMilkyWay();

        // Kick the async Milky Way load (once, after pipeline is ready). The decode runs
        // on a background thread; TryApplyPendingMilkyWay above uploads it on a later frame.
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
        ICelestialObjectDB db, RectF32 contentRect, string fontPath,
        float baseFontSize, SiteContext site, bool dimBelowHorizon, PlannerState plannerState,
        bool showAllOverlays)
    {
        var dpiScale = DpiScale;
        var proposals = plannerState.Proposals;
        var viewMatrix = State.CurrentViewMatrix;
        var fov = State.FieldOfViewDeg;
        var rectW = (int)contentRect.Width;
        var rectH = (int)contentRect.Height;

#if DEBUG
        // Slow-frame attribution (pairs with SdlEventLoop's [rdiag] frame.slow
        // begin/render/end split): logs which overlay phase ate the time. The Phase A
        // gather is logged at its call site (it only runs on cache miss and is the usual
        // spike); the per-frame log at the end of this method covers the always-on phases.
        // On a cache-miss frame the 'project' bucket below includes the gather -- the
        // separate skymap.gather line is what attributes it.
        var diagStart = System.Diagnostics.Stopwatch.GetTimestamp();
        long diagProjectDone = 0, diagLabelsDone = 0;
#endif

        // Phase A: the candidate set is keyed on a QUANTIZED view centre + FOV (not the raw
        // view matrix) so a pan only invalidates it when the centre crosses a FOV/8 cell;
        // when FOV >= 90 deg the scan bounds are the whole sphere so the centre drops out of
        // the key entirely. The walk itself is 60-170ms in dense regions, so it runs on a
        // BACKGROUND task (see StartOverlayGatherAsync): the render thread keeps projecting +
        // drawing the last-good list every frame and swaps in the fresh one when it lands.
        var wideFov = fov >= WideFovThresholdDeg;
        var showDarkNebulae = State.ShowDarkNebulae;

        // ~10% logarithmic FOV steps: zoom re-gathers a handful of times across a 2x range
        // instead of once per wheel tick. The magnitude cutoffs the gather derives from FOV
        // drift by at most ~10% before a re-gather picks them up -- visually indistinguishable.
        var quantFov = Math.Pow(1.1, Math.Round(Math.Log(Math.Max(fov, 0.1)) / Math.Log(1.1)));

        var centreX = contentRect.X + contentRect.Width * 0.5f;
        var centreY = contentRect.Y + contentRect.Height * 0.5f;
        var pprKey = SkyMapProjection.PixelsPerRadian(contentRect.Height, fov);
        var (centreRa, centreDec) = SkyMapProjection.UnprojectWithMatrix(
            centreX, centreY, viewMatrix, pprKey, centreX, centreY);
        // Quantize the centre to FOV/8 cells. The RA step widens by 1/cos(dec) (clamped) so
        // cells stay roughly square on the sky; near the pole RA quantization is meaningless
        // anyway -- the gather sweeps the full 24h there.
        var quantStepDeg = fov / 8.0;
        var quantDec = Math.Round(centreDec / quantStepDeg) * quantStepDeg;
        var cosDec = Math.Max(Math.Abs(Math.Cos(quantDec * Math.PI / 180.0)), 0.05);
        var quantStepRaHours = quantStepDeg / 15.0 / cosDec;
        var quantRa = Math.Round(centreRa / quantStepRaHours) * quantStepRaHours;
        var centreValid = !double.IsNaN(quantRa) && !double.IsNaN(quantDec);

        // Wide FOV: drop the centre from the key (whole-sphere scan). Invalid centre (NaN):
        // leave it NaN so the key never matches and we keep trying (matches old behaviour).
        var desiredKey = new OverlayGatherKey(
            db, proposals,
            wideFov ? 0.0 : (centreValid ? quantRa : double.NaN),
            wideFov ? 0.0 : (centreValid ? quantDec : double.NaN),
            quantFov, rectW, rectH, dpiScale, showAllOverlays, showDarkNebulae);

        // 1. Install a completed background gather (swaps _overlayCandidates, records its key).
        TryApplyPendingOverlayGather();

        // 2. If our candidates don't match the view we want, make sure a walk is running for it.
        //    Only one is in flight at a time; when it lands (step 1, a later frame) we apply it
        //    and, if the view has moved on, kick a fresh one. The render thread never blocks.
        if (!(_overlayHasAppliedKey && _overlayAppliedKey == desiredKey) && _overlayGatherTask is null)
        {
            StartOverlayGatherAsync(db, contentRect, dpiScale, proposals,
                showAllOverlays, showDarkNebulae, desiredKey);
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
#if DEBUG
        diagProjectDone = System.Diagnostics.Stopwatch.GetTimestamp();
#endif

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

        // Reserve the mount reticle's label footprint (drawn later, in RenderMountOverlay) so
        // an object name never renders on top of it when the mount sits on a catalogued target.
        var mountLabelReservation = BuildMountLabelReservation(contentRect, dpiScale, fontPath, baseFontSize);
        if (_useCollisionPlacement)
        {
            OverlayEngine.PlaceLabels(_overlayItems, placementLabelSize, 4f, measureText, record,
                reservedRegions: mountLabelReservation);
        }
        else
        {
            OverlayEngine.PlaceLabelsBestEffort(_overlayItems, placementLabelSize, 4f, measureText, record,
                reservedRegions: mountLabelReservation);
        }
#if DEBUG
        diagLabelsDone = System.Diagnostics.Stopwatch.GetTimestamp();
#endif

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
        var pinnedHaloR = pinnedHaloColor.RedF;
        var pinnedHaloG = pinnedHaloColor.GreenF;
        var pinnedHaloB = pinnedHaloColor.BlueF;
        var pinnedHaloA = pinnedHaloColor.AlphaF;

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
            var maxLineW = 0f;
            for (var li = 0; li < item.LabelLines.Count; li++)
            {
                var lineAlpha = (li == 0 ? 1.0f : 0.7f) * labelAlpha;
                var color = RGBAColor32.FromFloat(r, g, b, lineAlpha);
                var line = item.LabelLines[li];
                Renderer.DrawText(line.AsSpan(), fontPath, labelSize, color,
                    new RectInt(
                        new PointInt((int)(lx + 200), (int)(ly + (li + 1) * lineH)),
                        new PointInt((int)lx, (int)(ly + li * lineH))),
                    TextAlign.Near, TextAlign.Center);
                var (lineW, _) = Renderer.MeasureText(line.AsSpan(), fontPath, labelSize);
                if (lineW > maxLineW) maxLineW = lineW;
            }

            // Hit-test the label's bounding box (not the individual glyphs): clicking the
            // text selects the same object a click on its marker would. We synthesize a
            // click-select at the object's own screen position so the existing nearest-object
            // resolver (SkyMapClickSelectSignal -> SelectObjectByClick) runs unchanged -- one
            // path, no duplicate resolution. Skip near-invisible labels (faded out at wide
            // FOV) so there are no phantom hit targets, and require a measured text width.
            if (labelAlpha > 0.15f && maxLineW > 0f && item.LabelLines.Count > 0)
            {
                var labelH = item.LabelLines.Count * lineH;
                var objX = item.ScreenX;
                var objY = item.ScreenY;
                RegisterClickable(lx, ly, maxLineW, labelH,
                    new HitResult.ButtonHit($"SkyMapObjectLabel:{item.LabelLines[0]}"),
                    _ => PostSignal(new SkyMapClickSelectSignal(objX, objY, InputModifier.None)));
            }
        }

#if DEBUG
        var overlayTotalMs = System.Diagnostics.Stopwatch.GetElapsedTime(diagStart).TotalMilliseconds;
        if (overlayTotalMs > 25 && diagProjectDone != 0 && diagLabelsDone != 0)
        {
            var projectMs = System.Diagnostics.Stopwatch.GetElapsedTime(diagStart, diagProjectDone).TotalMilliseconds;
            var labelsMs = System.Diagnostics.Stopwatch.GetElapsedTime(diagProjectDone, diagLabelsDone).TotalMilliseconds;
            var drawMs = System.Diagnostics.Stopwatch.GetElapsedTime(diagLabelsDone).TotalMilliseconds;
            Console.Error.WriteLine(
                $"[rdiag] skymap.overlay {overlayTotalMs:F0}ms (project={projectMs:F0} labels={labelsMs:F0} draw={drawMs:F0}) cands={_overlayCandidates.Count} items={_overlayItems.Count} placed={_overlayPlacedLabels.Count}");
        }
#endif
    }

    /// <summary>
    /// Render-thread half of the async Phase A rebuild: when the background walk has
    /// completed, swap its fresh candidate list into <see cref="_overlayCandidates"/> and
    /// record the key it was computed for. Called every frame before Phase B; cheap when
    /// no walk is in flight. We apply the result unconditionally (even if the view has
    /// moved on since the walk started) -- it is still closer to the current view than
    /// what we hold, the gather's scan margin keeps it valid for nearby views, and the
    /// next frame kicks a fresh walk if it no longer matches.
    /// </summary>
    private void TryApplyPendingOverlayGather()
    {
        if (_overlayGatherTask is not { IsCompleted: true } task)
        {
            return;
        }
        // Clear the slot regardless of outcome so a faulted/cancelled walk can't wedge the
        // pipeline (a non-null task would block every future kick). A fault just means we
        // keep the last-good set and re-gather next frame.
        _overlayGatherTask = null;
        if (!task.IsCompletedSuccessfully)
        {
            Console.Error.WriteLine(
                $"[VkSkyMapTab] overlay gather failed: {task.Exception?.GetBaseException().Message}");
            return;
        }

        var result = task.Result;
        _overlayCandidates.Clear();
        _overlayCandidates.AddRange(result.Candidates);
        _overlayAppliedKey = result.Key;
        _overlayHasAppliedKey = true;
    }

    /// <summary>
    /// Kicks off the background Phase A walk for <paramref name="key"/>. The pinned-target
    /// set and the early-out (both layers off, nothing pinned) are resolved here on the
    /// render thread; the view matrix + FOV are snapshotted by value so the walk sees a
    /// consistent view even as the render thread keeps panning. The walk itself
    /// (<see cref="OverlayEngine.GatherSkyMapOverlayCandidates"/> + the per-layer filter)
    /// is pure CPU over the immutable-after-init catalog DB, so it is safe off-thread --
    /// the same property the async star-buffer build relies on. On completion it sets
    /// <c>State.NeedsRedraw</c> so the loop schedules the frame that applies it.
    /// </summary>
    private void StartOverlayGatherAsync(
        ICelestialObjectDB db, RectF32 contentRect, float dpiScale,
        ImmutableArray<ProposedObservation> proposals, bool showAllOverlays, bool showDarkNebulae,
        OverlayGatherKey key)
    {
        // Pinned catalog-index set from planner proposals (CatalogIndex-bearing targets only;
        // manually-typed RA/Dec targets would need a separate match, deferred). Shared with the
        // click resolver via PlannerActions so "is this object pinned" has one definition.
        var pinnedIndices = PlannerActions.GetPinnedCatalogIndices(proposals);

        // Both layers off and nothing pinned: nothing to gather. Apply an empty set
        // synchronously (instant) so we don't spin up a task or re-kick every frame.
        if (!showAllOverlays && !showDarkNebulae && pinnedIndices is null)
        {
            _overlayCandidates.Clear();
            _overlayAppliedKey = key;
            _overlayHasAppliedKey = true;
            return;
        }

        // Snapshot the view by value -- the background walk must not read the live (mutating)
        // SkyMapState. Matrix4x4 is a struct, so this is a consistent copy. The seed capacity
        // is read HERE on the render thread (reading _overlayCandidates.Count inside the task
        // would race the render thread's Clear/AddRange/iteration of that list).
        var snapViewMatrix = State.CurrentViewMatrix;
        var snapFov = State.FieldOfViewDeg;
        var seedCapacity = _overlayCandidates.Count > 0 ? _overlayCandidates.Count : 256;
#if DEBUG
        var gatherStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        _overlayGatherTask = Task.Run(() =>
        {
            var list = new List<OverlayCandidate>(seedCapacity);
            OverlayEngine.GatherSkyMapOverlayCandidates(
                snapViewMatrix, snapFov, contentRect, dpiScale, db, pinnedIndices, list);

            // Per-layer visibility: dark nebulae follow [D], every other catalog object
            // follows [O]. Pinned planner targets survive both gates so they stay visible
            // as landmarks regardless of layer state.
            if (!showAllOverlays || !showDarkNebulae)
            {
                list.RemoveAll(c =>
                {
                    if (c.IsPinned)
                    {
                        return false;
                    }
                    return c.ObjectType == ObjectType.DarkNeb ? !showDarkNebulae : !showAllOverlays;
                });
            }
#if DEBUG
            var gatherMs = System.Diagnostics.Stopwatch.GetElapsedTime(gatherStart).TotalMilliseconds;
            if (gatherMs > 10)
                Console.Error.WriteLine(
                    $"[rdiag] skymap.gather(async) {gatherMs:F0}ms cands={list.Count} fov={snapFov:F1}");
#endif
            // Schedule the render frame that installs this result (TryApplyPendingOverlayGather).
            State.NeedsRedraw = true;
            return new OverlayGatherResult(list, key);
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
        SkyMapMountOverlay mountOverlay, RectF32 contentRect,
        string fontPath, float baseFontSize, double ppr, float cx, float cy)
    {
        var dpiScale = DpiScale;
        if (!SkyMapProjection.ProjectWithMatrix(
                mountOverlay.RaJ2000, mountOverlay.DecJ2000,
                State.CurrentViewMatrix, ppr, cx, cy,
                out var screenX, out var screenY))
        {
            return;
        }

        // Destination marker + ETA for an in-flight slew. Drawn before the mount's own
        // off-screen early-return so the user still sees where it is heading even when the
        // reticle itself ends up just outside the viewport during a long slew.
        RenderSlewTarget(contentRect, dpiScale, fontPath, baseFontSize, ppr, cx, cy, screenX, screenY);

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
        var (nameText, coordsText) = MountLabelLines(mountOverlay);

        DrawReticleLabel(nameText, fontPath, fontSize, color,
            screenX, screenY + 20f * dpiScale, lineH);
        DrawReticleLabel(coordsText, fontPath, fontSize * 0.9f,
            new RGBAColor32(color.Red, color.Green, color.Blue, (byte)(color.Alpha * 0.8f)),
            screenX, screenY + 20f * dpiScale + lineH, lineH);

        // Clickable region over the reticle: opens the mount info panel (with its
        // Solve & Sync button) - same UX rule as the fixed markers, the click only
        // selects, the button acts. The reported position is the BELIEVED (encoder)
        // pointing; Solve & Sync is how the marker is brought back to the truth.
        var hitSize = 44f * dpiScale;
        var capturedName = mountOverlay.DisplayName;
        var capturedRA = mountOverlay.RaJ2000;
        var capturedDec = mountOverlay.DecJ2000;
        RegisterClickable(screenX - hitSize * 0.5f, screenY - hitSize * 0.5f, hitSize, hitSize,
            new HitResult.ButtonHit("SkyMapMountReticle"),
            _ => PostSignal(new SkyMapShowMountInfoSignal(
                capturedName, capturedRA, capturedDec)));
    }

    /// <summary>
    /// Draws the in-flight slew destination: a connecting line from the mount reticle to
    /// the target, plus a destination ring + "-> name" label and a best-effort ETA. When
    /// the target coincides with an already-rendered scheduled / pinned marker the ring is
    /// skipped (no duplicate) and only the line + ETA augment that existing marker.
    /// </summary>
    private void RenderSlewTarget(
        RectF32 contentRect, float dpiScale, string fontPath, float baseFontSize,
        double ppr, float cx, float cy, float mountScreenX, float mountScreenY)
    {
        if (State.ActiveSlewTarget is not { } target)
        {
            return;
        }

        if (!SkyMapProjection.ProjectWithMatrix(target.RaJ2000, target.DecJ2000,
                State.CurrentViewMatrix, ppr, cx, cy, out var tx, out var ty))
        {
            return;
        }

        const float margin = 100f;
        if (tx < contentRect.X - margin || tx > contentRect.X + contentRect.Width + margin
            || ty < contentRect.Y - margin || ty > contentRect.Y + contentRect.Height + margin)
        {
            return;
        }

        // Amber matches the slewing reticle / active scheduled target.
        var amber = new RGBAColor32(0xFF, 0xB0, 0x40, 0xFF);
        var fontSize = baseFontSize * dpiScale;
        var lineH = fontSize * 1.2f;

        // Slew vector: from the mount's current reticle to the destination.
        DrawLine(mountScreenX, mountScreenY, tx, ty,
            new RGBAColor32(amber.Red, amber.Green, amber.Blue, 0x70));

        // Don't draw a second reticle when the target is already a scheduled / pinned
        // marker - just augment it (the line above + the ETA below).
        var alreadyMarked = IsTargetAlreadyMarked(target);
        var labelTopY = ty + 16f * dpiScale;
        if (!alreadyMarked)
        {
            DrawCircle(tx, ty, 12f * dpiScale, amber, 1.5f);
            DrawReticleLabel(target.Name, fontPath, fontSize, amber, tx, labelTopY, lineH);
            labelTopY += lineH;
        }

        var eta = State.SlewEtaSeconds;
        if (!double.IsNaN(eta))
        {
            DrawReticleLabel(FormatEta(eta), fontPath, fontSize * 0.9f,
                new RGBAColor32(amber.Red, amber.Green, amber.Blue, 0xCC), tx, labelTopY, lineH);
        }
    }

    /// <summary>
    /// True when the slew target coincides (within ~6') with an already-rendered scheduled
    /// observation or pinned overlay object, so the destination shouldn't get a duplicate
    /// marker. Matched by sky position since scheduled targets don't carry a catalog index.
    /// </summary>
    private bool IsTargetAlreadyMarked(SlewTargetInfo target)
    {
        const double tolDeg = 0.1; // ~6 arcmin: same object, not a coincidental neighbour

        foreach (var (ra, dec, _, _) in State.ScheduleTargets)
        {
            if (CoordinateUtils.AngularSeparationDeg(ra, dec, target.RaJ2000, target.DecJ2000) < tolDeg)
            {
                return true;
            }
        }

        foreach (var item in _overlayItems)
        {
            if (item.IsPinned
                && CoordinateUtils.AngularSeparationDeg(item.RA, item.Dec, target.RaJ2000, target.DecJ2000) < tolDeg)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Formats a slew ETA (seconds) as a short "ETA ..." label.</summary>
    private static string FormatEta(double seconds)
    {
        if (seconds < 1.0) return "ETA <1s";
        if (seconds < 60.0) return $"ETA {seconds:F0}s";
        var m = (int)(seconds / 60.0);
        var s = (int)(seconds % 60.0);
        return $"ETA {m}m {s:D2}s";
    }

    /// <summary>
    /// Draws the committed plan's target(s): a small reticle + name label per scheduled
    /// observation. The currently-executing observation is amber (matching the slewing
    /// mount reticle); the rest are pale green. Below-horizon targets are dimmed.
    /// </summary>
    protected override void RenderScheduleTargets(
        RectF32 contentRect, string fontPath, float baseFontSize,
        double ppr, float cx, float cy, SiteContext site, bool dimBelowHorizon)
    {
        var dpiScale = DpiScale;
        var fontSize = baseFontSize * dpiScale;
        var lineH = fontSize * 1.2f;
        const float margin = 100f;

        foreach (var (ra, dec, name, isActive) in State.ScheduleTargets)
        {
            if (!SkyMapProjection.ProjectWithMatrix(ra, dec, State.CurrentViewMatrix,
                    ppr, cx, cy, out var sx, out var sy))
            {
                continue;
            }
            if (sx < contentRect.X - margin || sx > contentRect.X + contentRect.Width + margin
                || sy < contentRect.Y - margin || sy > contentRect.Y + contentRect.Height + margin)
            {
                continue;
            }

            // Dim targets currently below the horizon so they read as "not up yet".
            var alpha = (byte)(dimBelowHorizon && !site.IsAboveHorizon(ra, dec) ? 0x60 : 0xE0);
            var color = isActive
                ? new RGBAColor32(0xFF, 0xB0, 0x40, alpha)  // amber - active observation
                : new RGBAColor32(0x80, 0xE0, 0xA0, alpha); // pale green - scheduled

            VkOverlayShapes.DrawReticle(renderer, dpiScale,
                sx, sy, radius: 9f, armLength: 14f, gap: 4f,
                color: color, thickness: 1.5f);
            DrawReticleLabel(name, fontPath, fontSize * 0.9f, color,
                sx, sy + 14f * dpiScale, lineH);
        }
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
    /// The two lines of the mount reticle label: display name + believed J2000 RA/Dec.
    /// Single source of truth shared by <see cref="RenderMountOverlay"/> (which draws them)
    /// and <see cref="BuildMountLabelReservation"/> (which reserves their footprint so
    /// catalog labels don't overlap), so the rendered text and the reserved box can't drift.
    /// </summary>
    // No '+' for positive Dec — at small font a thin '+' was misread as '-' (and
    // vice versa) on the bug-hunt screenshots. Bare sign-when-negative is unambiguous.
    // Proper DMS punctuation (' for arcmin, " for arcsec) reads cleanly as a
    // sky coordinate vs the default ':' which looked like a time.
    private static (string Name, string Coords) MountLabelLines(SkyMapMountOverlay mountOverlay) => (
        mountOverlay.DisplayName,
        $"RA {CoordinateUtils.HoursToHMS(mountOverlay.RaJ2000, hourSeparator: 'h', withFrac: false, minuteSeparator: 'm', secondSuffix: "s")}"
            + $"  Dec {CoordinateUtils.DegreesToDMS(mountOverlay.DecJ2000, withPlus: false, degreeSign: '°', withFrac: false, arcMinuteSign: '′', arcSecondSign: "″")}");

    /// <summary>
    /// Screen-space box occupied by the mount reticle's two-line label, or <c>null</c> when
    /// no mount overlay is shown or the mount projects off-screen. Passed to
    /// <see cref="OverlayEngine.PlaceLabels"/> as a reserved region so an object name never
    /// renders on top of the mount label when the mount is parked on a catalogued target.
    /// The geometry mirrors <see cref="RenderMountOverlay"/>'s label layout exactly (two
    /// centred lines below the reticle: name at <c>fontSize</c>, coords at <c>fontSize*0.9</c>,
    /// each <see cref="DrawReticleLabel"/> block padded ±4px horizontally).
    /// </summary>
    private IReadOnlyList<(float X, float Y, float W, float H)>? BuildMountLabelReservation(
        RectF32 contentRect, float dpiScale, string fontPath, float baseFontSize)
    {
        if (!State.ShowMountOverlay || State.MountOverlay is not { } mountOverlay)
        {
            return null;
        }

        // Recompute the projection the same way the base render loop does (it doesn't pass
        // ppr/cx/cy into RenderObjectOverlay) so the box lands exactly where RenderMountOverlay
        // will draw the label a few passes later this frame.
        var ppr = SkyMapProjection.PixelsPerRadian(contentRect.Height, State.FieldOfViewDeg);
        var cx = contentRect.X + contentRect.Width * 0.5f;
        var cy = contentRect.Y + contentRect.Height * 0.5f;
        if (!SkyMapProjection.ProjectWithMatrix(mountOverlay.RaJ2000, mountOverlay.DecJ2000,
                State.CurrentViewMatrix, ppr, cx, cy, out var sx, out var sy))
        {
            return null;
        }

        var fontSize = baseFontSize * dpiScale;
        var lineH = fontSize * 1.2f;
        var (nameText, coordsText) = MountLabelLines(mountOverlay);
        var nameW = Renderer.MeasureText(nameText.AsSpan(), fontPath, fontSize).Width + 8f;
        var coordsW = Renderer.MeasureText(coordsText.AsSpan(), fontPath, fontSize * 0.9f).Width + 8f;
        var blockW = MathF.Max(nameW, coordsW);
        var top = sy + 20f * dpiScale;
        return [(sx - blockW * 0.5f, top, blockW, lineH * 2f)];
    }

    // Color palette for fixed-frame markers
    private static readonly RGBAColor32 _poleColor    = new(0x80, 0xC8, 0xFF, 0xE0); // pale cyan  - celestial poles
    private static readonly RGBAColor32 _zenithColor  = new(0xFF, 0xD0, 0x80, 0xE0); // warm amber - local zenith
    private static readonly RGBAColor32 _cardinalColor = new(0xFF, 0xFF, 0xFF, 0xE0); // white     - ordinary cardinals
    private static readonly RGBAColor32 _cardinalNorthColor = new(0xFF, 0xB0, 0xB0, 0xE0); // soft red - north orientation marker

    /// <summary>
    /// Draws clickable reticles for NCP, SCP, and Zenith (each posts a slew signal on
    /// click) plus non-clickable N/S/E/W horizon labels for orientation. Zenith and the
    /// cardinal labels only render in horizon mode with a valid site; the poles are
    /// always shown since they are frame-independent.
    /// </summary>
    protected override void RenderFixedPointMarkers(
        RectF32 contentRect, string fontPath, float baseFontSize,
        double ppr, float cx, float cy, SiteContext site)
    {
        var dpiScale = DpiScale;
        var fontSize = baseFontSize * dpiScale;
        var lineH = fontSize * 1.2f;
        var viewMatrix = State.CurrentViewMatrix;

        // North Celestial Pole (Dec=+90). Use Dec slightly off 90 for slew to dodge
        // mount drivers that reject exactly-polar targets; RA is arbitrary at the pole.
        DrawFixedMarker(contentRect, dpiScale, fontPath, fontSize, lineH, ppr, cx, cy,
            ux: 0.0, uy: 0.0, uz: 1.0, label: "NCP", color: _poleColor,
            slewName: "North Celestial Pole", slewRA: 0.0, slewDec: 89.999, hitTag: "NCP",
            fixedPoint: SkyFixedPoint.NorthCelestialPole);

        // South Celestial Pole (Dec=-90).
        DrawFixedMarker(contentRect, dpiScale, fontPath, fontSize, lineH, ppr, cx, cy,
            ux: 0.0, uy: 0.0, uz: -1.0, label: "SCP", color: _poleColor,
            slewName: "South Celestial Pole", slewRA: 0.0, slewDec: -89.999, hitTag: "SCP",
            fixedPoint: SkyFixedPoint.SouthCelestialPole);

        // Zenith, N/S/E/W are only meaningful with a valid site in horizon mode.
        // In equatorial mode the horizon itself isn't drawn, so cardinal labels
        // would land arbitrarily across the sphere.
        if (!site.IsValid || State.Mode != SkyMapMode.Horizon)
            return;

        // Zenith unit vector = (cosLat*cosLST, cosLat*sinLST, sinLat) - matches the
        // "up" reference used by SkyMapState.ComputeViewMatrix in horizon mode.
        var (sinLST, cosLST) = Math.SinCos(site.LST * (Math.PI / 12.0));
        var zx = site.CosLat * cosLST;
        var zy = site.CosLat * sinLST;
        var zz = site.SinLat;
        // Zenith RA=LST, Dec=latitude (Alt=90 degenerate case collapses to these).
        DrawFixedMarker(contentRect, dpiScale, fontPath, fontSize, lineH, ppr, cx, cy,
            ux: zx, uy: zy, uz: zz, label: "Zenith", color: _zenithColor,
            slewName: "Zenith", slewRA: site.LST, slewDec: double.RadiansToDegrees(Math.Asin(site.SinLat)),
            hitTag: "Zenith", fixedPoint: SkyFixedPoint.Zenith);

        // N/S/E/W horizon labels - orientation only, no slew handler. Skip the reticle
        // circle, just drop a letter at the projected horizon point.
        DrawHorizonCardinalLabel(contentRect, fontPath, fontSize * 1.1f, ppr, cx, cy, site,
            azDeg: 0.0, label: "N", color: _cardinalNorthColor);
        DrawHorizonCardinalLabel(contentRect, fontPath, fontSize * 1.1f, ppr, cx, cy, site,
            azDeg: 90.0, label: "E", color: _cardinalColor);
        DrawHorizonCardinalLabel(contentRect, fontPath, fontSize * 1.1f, ppr, cx, cy, site,
            azDeg: 180.0, label: "S", color: _cardinalColor);
        DrawHorizonCardinalLabel(contentRect, fontPath, fontSize * 1.1f, ppr, cx, cy, site,
            azDeg: 270.0, label: "W", color: _cardinalColor);
    }

    /// <summary>
    /// Projects a unit vector, draws a small reticle + label, and registers a
    /// clickable hit region that posts a <see cref="SkyMapShowFixedPointInfoSignal"/>.
    /// The signal opens the standard sky-map info panel (with its Goto button) for
    /// the marker's coordinates — clicking the marker itself never slews. This
    /// mirrors the catalog click-select behaviour: clicks select, the Goto button
    /// is the only path to a slew.
    /// </summary>
    private void DrawFixedMarker(
        RectF32 contentRect, float dpiScale, string fontPath, float fontSize, float lineH,
        double ppr, float cx, float cy,
        double ux, double uy, double uz,
        string label, RGBAColor32 color,
        string slewName, double slewRA, double slewDec, string hitTag,
        SkyFixedPoint fixedPoint = SkyFixedPoint.None)
    {
        if (!SkyMapProjection.ProjectUnitVec(ux, uy, uz, State.CurrentViewMatrix, ppr, cx, cy,
                out var sx, out var sy))
        {
            return;
        }

        // Off-screen cull with the same margin used by the mount reticle.
        const float margin = 60f;
        if (sx < contentRect.X - margin || sx > contentRect.X + contentRect.Width + margin
            || sy < contentRect.Y - margin || sy > contentRect.Y + contentRect.Height + margin)
        {
            return;
        }

        VkOverlayShapes.DrawReticle(renderer, dpiScale,
            sx, sy, radius: 10f, armLength: 16f, gap: 5f,
            color: color, thickness: 1.5f);

        DrawReticleLabel(label, fontPath, fontSize, color, sx, sy + 14f * dpiScale, lineH);

        // 36x36 clickable box centered on the reticle. Open the info panel rather than
        // slewing directly — same UX rule as the rest of the map (click selects, Goto
        // button slews).
        var hitSize = 36f * dpiScale;
        var capturedName = slewName;
        var capturedRA = slewRA;
        var capturedDec = slewDec;
        var capturedFixedPoint = fixedPoint;
        RegisterClickable(sx - hitSize * 0.5f, sy - hitSize * 0.5f, hitSize, hitSize,
            new HitResult.ButtonHit($"SkyMapFixedMarker:{hitTag}"),
            _ => PostSignal(new SkyMapShowFixedPointInfoSignal(
                capturedName, capturedRA, capturedDec, capturedFixedPoint)));
    }

    /// <summary>
    /// Draws a single horizon cardinal label (N/E/S/W) at Alt=0 for the given azimuth.
    /// Non-clickable - slewing to a point on the horizon isn't a useful goto target.
    /// </summary>
    private void DrawHorizonCardinalLabel(
        RectF32 contentRect, string fontPath, float fontSize,
        double ppr, float cx, float cy, SiteContext site,
        double azDeg, string label, RGBAColor32 color)
    {
        // Convert Alt=0, Az=azDeg to RA/Dec, then to a J2000 unit vector for projection.
        VkSkyMapPipeline.AltAzToRaDec(altDeg: 0.0, azDeg: azDeg, site, out var raHours, out var decDeg);
        var (ux, uy, uz) = SkyMapState.RaDecToUnitVec(raHours, decDeg);

        if (!SkyMapProjection.ProjectUnitVec(ux, uy, uz, State.CurrentViewMatrix, ppr, cx, cy,
                out var sx, out var sy))
        {
            return;
        }

        const float margin = 20f;
        if (sx < contentRect.X - margin || sx > contentRect.X + contentRect.Width + margin
            || sy < contentRect.Y - margin || sy > contentRect.Y + contentRect.Height + margin)
        {
            return;
        }

        var (textW, textH) = Renderer.MeasureText(label.AsSpan(), fontPath, fontSize);
        Renderer.DrawText(label.AsSpan(), fontPath, fontSize, color,
            new RectInt(
                new PointInt((int)(sx + textW * 0.5f + 2), (int)(sy + textH * 0.5f)),
                new PointInt((int)(sx - textW * 0.5f - 2), (int)(sy - textH * 0.5f))),
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
