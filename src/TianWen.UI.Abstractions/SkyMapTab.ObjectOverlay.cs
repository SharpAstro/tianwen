using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// CPU / primitive object-overlay drawing ([O] catalog markers + labels, [D] dark nebulae, and
    /// always-on pinned planner-target landmarks) for renderers WITHOUT a GPU instanced-ellipse
    /// pipeline -- i.e. the browser sky map. It shares the candidate gather / projection / label
    /// placement with the desktop GPU path (all in <see cref="Overlays.OverlayEngine"/>); only the
    /// final rasterisation differs, and it is a virtual seam (<see cref="DrawOverlayMarkers"/>)
    /// rather than a parallel method -- ellipses/crosses/circles trace with the surface-agnostic
    /// <c>DrawLine</c>/<c>DrawCircle</c> primitives by default, while a surface with an instanced
    /// overlay pipeline overrides it and submits one draw.
    /// </summary>
    public partial class SkyMapTab<TSurface>
    {
        // Cached candidate list + the key it was gathered for. The gather (Phase A grid walk) is the
        // heavy part; caching on a quantized view/rect/layer/pins key means panning within a cell only
        // re-projects (Phase B, cheap). Synchronous -- single-threaded WASM has no background thread,
        // so unlike VkSkyMapTab's async gather this walks inline, but only on a meaningful view change.
        private readonly List<OverlayCandidate> _primOverlayCandidates = [];
        private readonly List<OverlayItem> _primOverlayItems = [];
        private bool _primOverlayHasKey;
        private PrimOverlayKey _primOverlayKey;

        // The pinned set is asked for twice per frame (the comet layer and the overlay pass) and
        // GetPinnedCatalogIndices builds a fresh HashSet each time. Proposals is an ImmutableArray,
        // so identity of the underlying array is a sound cache key: a pin change replaces it.
        private ImmutableArray<ProposedObservation> _pinnedSetFor;
        private IReadOnlySet<CatalogIndex>? _pinnedSet;

        private IReadOnlySet<CatalogIndex>? PinnedCatalogIndices(PlannerState plannerState)
        {
            var proposals = plannerState.Proposals;
            if (!_pinnedSetFor.Equals(proposals))
            {
                _pinnedSetFor = proposals;
                _pinnedSet = PlannerActions.GetPinnedCatalogIndices(proposals);
            }

            return _pinnedSet;
        }

        /// <summary>
        /// Whether any pinned target is a comet, which is what entitles the comet layer to draw with
        /// its own toggle off. Comets are the one pinnable body with no entry in the object DB, so
        /// the overlay pass cannot render them and this layer has to honour the pin itself.
        /// </summary>
        private static bool HasPinnedComet(IReadOnlySet<CatalogIndex>? pinned)
        {
            if (pinned is null)
            {
                return false;
            }

            foreach (var idx in pinned)
            {
                if (idx.ToCatalog() == Catalog.Comet)
                {
                    return true;
                }
            }

            return false;
        }

        // Whether the view is still moving, compared on the RAW field of view and centre. Not on the
        // quantized cache key: a smooth zoom crosses a 10% FOV bucket only every ~9 frames, so a
        // key-based test reads a gesture as a series of unrelated discrete changes and fires on
        // almost none of its frames.
        private double _primLastFov = double.NaN;
        private double _primLastCentreRa = double.NaN;
        private double _primLastCentreDec = double.NaN;

        /// <summary>
        /// True when labels were suppressed because the view moved on this frame, so the host owes a
        /// repaint once the motion stops.
        ///
        /// <para><b>Labels are the overlay's dominant cost and the one part a moving view cannot
        /// use.</b> Measured over a 40-frame dense zoom in the browser: placement plus text was
        /// 3,275 ms against 1,253 for the gather, 605 for the markers and 306 for the projection --
        /// about half of every frame, spent on text sliding past too fast to read. Markers keep
        /// drawing throughout, so the sky itself never goes blank.</para>
        ///
        /// <para>The host must DEBOUNCE this, not answer it with an immediate frame. The browser has
        /// no render loop, so a repaint requested straight away simply runs the suppression again and
        /// asks once more; measured that way a 40-event gesture painted 159 frames instead of 40 and
        /// spent more total time than it saved, even though each frame was 2.5x cheaper. One repaint
        /// after the motion stops is the whole requirement.</para>
        /// </summary>
        internal bool OverlayLabelsPending { get; private set; }

        /// <summary>
        /// How many times the candidate gather has actually run, the counterpart of
        /// <see cref="SkyMapState.PlanetCacheRebuilds"/> and load-bearing for the same reason: the cost
        /// of this cache is invisible from its output (a stale-keyed rebuild draws the identical frame,
        /// just slower), so only a count can tell a cache that holds from one that misses every event.
        /// Bumped by <see cref="BuildOverlayKeyForTest"/>'s production path, not by the test seam.
        /// </summary>
        internal int PrimOverlayGathers { get; private set; }

        /// <summary>
        /// How many candidates the last gather produced. The observable for "the gather did only the
        /// work this configuration needs": with both layers off and two targets pinned it must be 2,
        /// and it was 1,260 at a 30 degree field (the whole sphere's worth at a wide one), every one of
        /// them label-built and sorted so the caller could throw all but two away. Nothing downstream
        /// can see the difference -- the same two markers are drawn either way.
        /// </summary>
        internal int PrimOverlayCandidateCount => _primOverlayCandidates.Count;

        /// <summary>
        /// Cumulative wall time inside the candidate gather. Paired with
        /// <see cref="PrimOverlayGathers"/> it separates "how often" from "how expensive", which a
        /// frame-duration trace cannot: the browser runs the gather INSIDE the animation-frame
        /// callback, so a slow frame and a slow gather are the same sample until they are timed apart.
        /// </summary>
        internal double PrimOverlayGatherMs { get; private set; }

        /// <summary>
        /// Cumulative wall time in the per-frame overlay phases: projecting the cached candidates,
        /// rasterising their markers, and placing plus drawing the labels.
        ///
        /// <para>Split out because the totals are misleading in the direction that costs work. The
        /// gather is the eye-catching number -- 14 to 55 ms whenever it runs -- but on a dense zoom it
        /// runs about four times while these three are paid on all forty frames, so a fix aimed at the
        /// gather can be measured, correct, and worth almost nothing. That is exactly the wrong turn
        /// these were added to prevent.</para>
        /// </summary>
        internal double PrimOverlayProjectMs { get; private set; }

        /// <inheritdoc cref="PrimOverlayProjectMs"/>
        internal double PrimOverlayMarkerMs { get; private set; }

        /// <inheritdoc cref="PrimOverlayProjectMs"/>
        internal double PrimOverlayLabelMs { get; private set; }

        /// <summary>
        /// The cache key for a given view, for tests that need to count key changes across a gesture
        /// without driving a whole render (which needs a renderer, a font and a populated catalog).
        /// Returns an opaque value -- only its equality across successive calls is meaningful.
        /// </summary>
        internal object BuildOverlayKeyForTest(
            RectF32 contentRect, double fov, float cxView, float cyView, double ppr, PlannerState plannerState,
            bool showDark = false)
            => BuildOverlayKey(contentRect, fov, cxView, cyView, ppr, showAllOverlays: true, showDark, plannerState);

        private readonly record struct PrimOverlayKey(
            double QuantRa, double QuantDec, double QuantFov,
            int RectW, int RectH, bool ShowAll, bool ShowDark, int PinHash);

        // Wide FOV: the gather sweeps the whole sphere, so the view centre (and now the FOV) drops
        // out of the cache key. Forwards to the engine's constant so this and VkSkyMapTab's copy
        // cannot drift from the branch they both have to agree with.
        private const double PrimOverlayWideFovDeg = OverlayEngine.WideFovDeg;

        /// <summary>
        /// Draws the object overlay using CPU primitives. A subclass whose renderer has no instanced
        /// overlay pipeline overrides <see cref="RenderObjectOverlay"/> to call this. Parameters mirror
        /// <see cref="RenderObjectOverlay"/>. <paramref name="showAllOverlays"/> is the [O] toggle; the
        /// [D] dark-nebula toggle is read from <see cref="SkyMapState.ShowDarkNebulae"/>. Pinned planner
        /// targets survive both gates and render as orange landmarks with a halo.
        /// </summary>
        protected void RenderObjectOverlayPrimitive(
            ICelestialObjectDB db, RectF32 contentRect,
            float baseFontSize, SiteContext site, bool dimBelowHorizon, PlannerState plannerState,
            bool showAllOverlays)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            if (contentRect.Width <= 0 || contentRect.Height <= 0)
            {
                OverlayLabelsPending = false;
                return;
            }

            var showDark = State.ShowDarkNebulae;
            var pinned = PinnedCatalogIndices(plannerState);

            // Both layers off and nothing pinned: nothing to draw (mirrors VkSkyMapTab's early-out).
            if (!showAllOverlays && !showDark && pinned is null)
            {
                _primOverlayCandidates.Clear();
                _primOverlayHasKey = false;
                OverlayLabelsPending = false;
                return;
            }

            // Both layers off but something IS pinned: the pass runs only to draw landmarks, so the
            // gather looks the pins up directly instead of walking the grid to build a set it would
            // then throw all but two entries of. See GatherSkyMapOverlayCandidates' pinnedOnly.
            var pinnedOnly = !showAllOverlays && !showDark;

            var fov = State.FieldOfViewDeg;
            var cxView = contentRect.X + contentRect.Width * 0.5f;
            var cyView = contentRect.Y + contentRect.Height * 0.5f;
            var ppr = SkyMapProjection.PixelsPerRadian(contentRect.Height, fov);

            // The FIRST frame has no previous sample and therefore no motion. Deriving that from the
            // NaN seeds instead (NaN != anything) made the first render report "moved" and suppress
            // its labels -- invisible on a host that debounces a repaint, permanent on one that does
            // not paint again by itself, which is how the offline renderer sees it.
            var hasPreviousView = !double.IsNaN(_primLastFov);
            var viewMoved = hasPreviousView
                && (fov != _primLastFov
                    || State.CenterRA != _primLastCentreRa
                    || State.CenterDec != _primLastCentreDec);
            _primLastFov = fov;
            _primLastCentreRa = State.CenterRA;
            _primLastCentreDec = State.CenterDec;
            var key = BuildOverlayKey(contentRect, fov, cxView, cyView, ppr, showAllOverlays, showDark, plannerState);
            if (!_primOverlayHasKey || !_primOverlayKey.Equals(key))
            {
                PrimOverlayGathers++;
                var gatherStart = System.Diagnostics.Stopwatch.GetTimestamp();
                OverlayEngine.GatherSkyMapOverlayCandidates(
                    State.CurrentViewMatrix, fov, contentRect, dpiScale, db, pinned, _primOverlayCandidates,
                    pinnedOnly);
                PrimOverlayGatherMs += System.Diagnostics.Stopwatch.GetElapsedTime(gatherStart).TotalMilliseconds;

                // Per-layer visibility (same rule as VkSkyMapTab): dark nebulae follow [D], every other
                // catalog object follows [O]; pinned targets bypass both so they stay visible. The
                // pinned-only gather already returns exactly that set, so there is nothing to remove.
                if (!pinnedOnly && (!showAllOverlays || !showDark))
                {
                    _primOverlayCandidates.RemoveAll(c => !c.IsPinned
                        && (c.ObjectType == ObjectType.DarkNeb ? !showDark : !showAllOverlays));
                }

                _primOverlayKey = key;
                _primOverlayHasKey = true;
            }

            if (_primOverlayCandidates.Count == 0)
            {
                OverlayLabelsPending = false;
                return;
            }

            var arcminToPixels = (float)(ppr * Math.PI / (180.0 * 60.0));
            // Overlay fade at wide FOV (matches VkSkyMapTab): non-pinned markers dim toward a 0.55 floor
            // between 120 and 180 deg so a zoomed-out view stays readable; pinned targets stay full.
            var fovAlpha = MathF.Max(MathF.Min(120f / (float)fov, 1f), 0.55f);

            // ONE projection per frame, feeding BOTH passes. Markers still read their geometry off
            // the CANDIDATE (it carries arcmin extent + position angle, which the projected item
            // drops because the GPU path reads them off the candidate instead), so the item's
            // CandidateIndex is the link back. This pass used to project every candidate itself and
            // then ProjectSkyMapCandidatesInto projected the identical set again a few lines below:
            // pure duplication, measured at ~2.1 ms per frame each at a full-sky zoom, and paid on
            // every repaint since the browser has no render loop.
            //
            // Using the projection's own cull also fixes a discrepancy rather than inheriting one:
            // the margin here was 100 + arcminToPixels, which adds a SCALE FACTOR to a pixel margin
            // and so is ~100 px whatever the shape's size, while the projection extends its margin
            // by the shape's actual on-screen semi-major axis. A large nebula centred just off the
            // viewport got a label but no marker.
            var projectStart = System.Diagnostics.Stopwatch.GetTimestamp();
            OverlayEngine.ProjectSkyMapCandidatesInto(_primOverlayCandidates, State, contentRect, dpiScale, _primOverlayItems);
            PrimOverlayProjectMs += System.Diagnostics.Stopwatch.GetElapsedTime(projectStart).TotalMilliseconds;
            if (_primOverlayItems.Count == 0)
            {
                OverlayLabelsPending = false;
                return;
            }

            var markerStart = System.Diagnostics.Stopwatch.GetTimestamp();
            DrawOverlayMarkers(_primOverlayCandidates, _primOverlayItems, PrimOverlayGathers,
                arcminToPixels, ppr, cxView, cyView, dpiScale, fovAlpha, dimBelowHorizon, site);
            PrimOverlayMarkerMs += System.Diagnostics.Stopwatch.GetElapsedTime(markerStart).TotalMilliseconds;

            // Pass 2: labels via the shared best-effort placement (stable slots) + DrawText, over the
            // items projected once above -- skipped entirely while the view is moving, which is where
            // roughly half of a gesture's frame time was going.
            OverlayLabelsPending = viewMoved;
            if (OverlayLabelsPending)
            {
                return;
            }

            var labelSize = baseFontSize * dpiScale * 0.85f;
            var lineH = labelSize * 1.2f;
            var measureText = (string text, float size) => Renderer.MeasureText(text.AsSpan(), fontPath, size).Width;
            var labelStart = System.Diagnostics.Stopwatch.GetTimestamp();
            OverlayEngine.PlaceLabelsBestEffort(_primOverlayItems, labelSize, 4f, measureText,
                (item, lx, ly) =>
                {
                    var a = OverlayEngine.MarkerAlpha(
                        item.IsPinned, item.RA, item.Dec, dimBelowHorizon, site, fovAlpha);
                    var (r, g, b) = item.Color;
                    var col = item.IsPinned
                        ? new RGBAColor32(0xFF, 0x90, 0x50, (byte)(a * 255f))
                        : RGBAColor32.FromFloat(r, g, b, a);
                    var maxLineW = 0f;
                    for (var i = 0; i < item.LabelLines.Count; i++)
                    {
                        DrawText(item.LabelLines[i].AsSpan(), fontPath,
                            lx, ly + i * lineH, 220f, lineH,
                            labelSize, col, TextAlign.Near, TextAlign.Near);
                        var w = measureText(item.LabelLines[i], labelSize);
                        if (w > maxLineW) { maxLineW = w; }
                    }

                    // Make the LABEL itself clickable -> selects the same object its marker would (desktop
                    // parity: VkSkyMapTab.RenderObjectOverlay registers the identical bridge on the GPU path;
                    // the shared base already does it for planet/comet labels). Object selection is a
                    // GEOMETRIC nearest-object search at the click point, and the label is drawn OFFSET from
                    // the marker, so without a bridge a label click lands too far from the marker's screen
                    // position to hit -- "clicking the label doesn't select" (web-only, since only the CPU
                    // primitive path lacked it). Re-synthesize the click at the object's own screen position
                    // so the existing resolver (SkyMapClickSelectSignal -> SelectObjectByClick) runs
                    // unchanged. Skip nearly-faded labels so there are no phantom hit targets.
                    if (a > 0.15f && maxLineW > 0f && item.LabelLines.Count > 0)
                    {
                        var labelH = item.LabelLines.Count * lineH;
                        var objX = item.ScreenX;
                        var objY = item.ScreenY;
                        RegisterClickable(lx, ly, maxLineW, labelH,
                            new HitResult.ButtonHit($"SkyMapObjectLabel:{item.LabelLines[0]}"),
                            _ => PostSignal(new SkyMapClickSelectSignal(objX, objY, InputModifier.None)));
                    }
                });
            PrimOverlayLabelMs += System.Diagnostics.Stopwatch.GetElapsedTime(labelStart).TotalMilliseconds;
        }

        /// <summary>
        /// Rasterises the overlay markers for one frame. The default traces each one with the
        /// surface-agnostic line primitives; a surface with an instanced overlay pipeline overrides
        /// this and submits a single draw instead. Everything around it -- the cached gather, the one
        /// projection, label placement -- is shared either way, which is the point of putting the seam
        /// here rather than at <see cref="RenderObjectOverlayPrimitive"/>: only the rasterisation ever
        /// differed between the two, and having them as whole parallel methods is what let the pinned
        /// halo drift into an ellipse on one and a circle on the other.
        /// </summary>
        /// <param name="candidates">The cached, view-independent candidate list.</param>
        /// <param name="items">The same candidates projected for this frame; a marker reads its
        /// screen position here and its geometry from <paramref name="candidates"/> via
        /// <see cref="OverlayItem.CandidateIndex"/>.</param>
        /// <param name="candidateVersion">Bumped whenever <paramref name="candidates"/> was
        /// re-gathered. An override that caches a GPU buffer needs it: the candidate COUNT is not a
        /// version, so a re-gather that happens to return the same number of objects would otherwise
        /// keep drawing the previous set.</param>
        protected virtual void DrawOverlayMarkers(
            IReadOnlyList<OverlayCandidate> candidates, IReadOnlyList<OverlayItem> items,
            int candidateVersion,
            float arcminToPixels, double ppr, float cxView, float cyView,
            float dpiScale, float fovAlpha, bool dimBelowHorizon, SiteContext site)
        {
            foreach (var item in items)
            {
                if ((uint)item.CandidateIndex >= (uint)candidates.Count)
                {
                    continue;
                }
                var cand = candidates[item.CandidateIndex];
                var sx = item.ScreenX;
                var sy = item.ScreenY;

                var alpha = OverlayEngine.MarkerAlpha(
                    cand.IsPinned, cand.RA, cand.Dec, dimBelowHorizon, site, fovAlpha);

                var (cr, cg, cb) = cand.Color;
                var color = cand.IsPinned
                    ? OverlayEngine.PinnedMarkerColor with { Alpha = (byte)(alpha * 255f) }
                    : RGBAColor32.FromFloat(cr, cg, cb, alpha);

                // Pinned halo behind the marker (geometry shared with the GPU path via
                // OverlayEngine.PinnedHalo*), so a planned target is spottable at any zoom. An
                // ELLIPSE marker gets an ellipse halo: one uniform scale on both semi-axes, keeping
                // the object's axis ratio and position angle. It used to size a CIRCLE from the major
                // axis alone, so a pinned edge-on galaxy wore a halo far wider than itself; that is
                // the same ellipse-reads-as-a-circle defect the search selection marker had.
                if (cand.IsPinned)
                {
                    var haloColor = OverlayEngine.PinnedHaloColor with { Alpha = (byte)(OverlayEngine.PinnedHaloColor.Alpha * fovAlpha) };
                    var haloFloorPx = OverlayEngine.PinnedHaloMinSemiMajorPx * dpiScale;
                    if (cand.Marker is OverlayCandidateMarker.Ellipse he)
                    {
                        var haloScale = OverlayEngine.EllipseLegibilityScale(
                            he.SemiMajArcmin * arcminToPixels, haloFloorPx, OverlayEngine.PinnedHaloScale);
                        DrawOverlayEllipse(cand.RA, cand.Dec, he, arcminToPixels, ppr, cxView, cyView,
                            sx, sy, haloColor, haloScale, OverlayEngine.PinnedHaloStrokePx);
                    }
                    else
                    {
                        var haloPx = cand.Marker is OverlayCandidateMarker.Circle hc
                            ? MathF.Max(hc.RadiusPxAtDpi1 * dpiScale * OverlayEngine.PinnedHaloScale, haloFloorPx)
                            : haloFloorPx;
                        DrawCircle(sx, sy, haloPx, haloColor, OverlayEngine.PinnedHaloStrokePx);
                    }
                }

                switch (cand.Marker)
                {
                    case OverlayCandidateMarker.Ellipse e:
                        DrawOverlayEllipse(cand.RA, cand.Dec, e, arcminToPixels, ppr, cxView, cyView, sx, sy, color);
                        break;
                    case OverlayCandidateMarker.Cross c:
                        DrawOverlayCross(sx, sy, c.ArmPxAtDpi1 * dpiScale, color);
                        break;
                    case OverlayCandidateMarker.Circle c:
                        DrawCircle(sx, sy, c.RadiusPxAtDpi1 * dpiScale, color, 1.5f);
                        break;
                }
            }
        }

        private PrimOverlayKey BuildOverlayKey(
            RectF32 contentRect, double fov, float cxView, float cyView, double ppr,
            bool showAllOverlays, bool showDark, PlannerState plannerState)
        {
            // ~10% logarithmic FOV buckets so zoom re-gathers a few times per 2x range, not per tick.
            var quantFov = Math.Pow(1.1, Math.Round(Math.Log(Math.Max(fov, 0.1)) / Math.Log(1.1)));

            var wideFov = fov >= PrimOverlayWideFovDeg;

            // Above the wide threshold the gather cannot depend on the field of view AT ALL, so the FOV
            // drops out of the key exactly as the centre already has. Three facts make that exact rather
            // than approximate: the scan sweeps the whole sphere past 90 degrees, and BOTH magnitude
            // cutoffs are already flat by then (GetExtendedMagCutoff and GetStarMagCutoff both switch
            // for the last time at 5 degrees, to 8.0 and 1.0). The last FOV dependence was the
            // dark-nebula on-screen-size filter; the gather now admits a superset valid across the
            // whole wide range and the projection applies the exact test per frame, so [D] no longer
            // has to hold the FOV in the key.
            //
            // Worth it because the wide gather is the expensive one: a full-sky sweep measured at 121 ms
            // in the browser, and zooming out from 90 to 180 degrees crosses ~7 of the 10% buckets. With
            // dark nebulae ON that gate kept costing a gather per bucket above the threshold: measured on
            // the deployed build over a 60-to-180 degree zoom-out, 6 gathers and 632 ms of gathering
            // against 3 and 193 ms with them off -- so the users who most wanted the overlay were the
            // ones getting none of the fix. The fixed build costs 3 either way.
            //
            // Measure this from a FRESH page. The attribution probe runs three sweeps in sequence and the
            // third inherits wherever the second left the zoom (0.5 degrees), so nearly all of its steps
            // are legitimately below the threshold; read that way it reports 30 gathers and attributes
            // them here, which is how this was first written down as "30 against 3".
            if (wideFov)
            {
                quantFov = double.PositiveInfinity;
            }

            double quantRa = 0.0, quantDec = 0.0;
            if (!wideFov)
            {
                var (centreRa, centreDec) = SkyMapProjection.UnprojectWithMatrix(
                    cxView, cyView, State.CurrentViewMatrix, ppr, cxView, cyView);
                if (!double.IsNaN(centreRa) && !double.IsNaN(centreDec))
                {
                    // Quantize the centre to FOV/8 cells (RA step widens by 1/cos(dec) so cells stay
                    // roughly square) -- matches the gather's scan margin so the cached set stays valid
                    // while the centre drifts inside a cell.
                    //
                    // The step comes from the BUCKETED fov, never the raw one. Deriving it from the raw
                    // value rescales the grid continuously, so during a zoom the rounded centre moves on
                    // every event even when the centre is perfectly still -- the cache then misses per
                    // tick and re-gathers the whole candidate set, which is the opposite of what the
                    // quantFov bucketing above is for. Measured over a 60->30 degree pinch with a fixed
                    // centre: 69 gathers from the raw fov against 8 from the bucketed one. It made a
                    // pinch the most expensive gesture in the app (touchmove p95 91 ms, max 246 ms)
                    // while a pan of 1.4h of RA cost 3 gathers.
                    var stepDeg = quantFov / 8.0;
                    quantDec = Math.Round(centreDec / stepDeg) * stepDeg;
                    var cosDec = Math.Max(Math.Abs(Math.Cos(quantDec * Math.PI / 180.0)), 0.05);
                    var stepRaH = stepDeg / 15.0 / cosDec;
                    quantRa = Math.Round(centreRa / stepRaH) * stepRaH;
                }
            }

            // Fold pin identity into the key so pinning/unpinning re-gathers (pinned objects bypass the
            // magnitude/type/dark-nebula filters, so the candidate set depends on them). Proposals are few.
            var pinHash = 17;
            foreach (var p in plannerState.Proposals)
            {
                pinHash = pinHash * 31 + p.Target.GetHashCode();
            }

            return new PrimOverlayKey(
                quantRa, quantDec, quantFov,
                (int)contentRect.Width, (int)contentRect.Height,
                showAllOverlays, showDark, pinHash);
        }

        // Trace a rotated ellipse for an extended catalog object, oriented by the object's true sky
        // position angle -- same construction as TryDrawShapeMarker (the selection ellipse) and the GPU
        // overlay shader, via the shared OverlayEngine.ComputeEllipseScreenAxes.
        // scale grows both semi-axes by the SAME factor (so the traced shape keeps the object's axis
        // ratio); the pinned halo passes OverlayEngine.EllipseLegibilityScale, the marker itself 1.
        private void DrawOverlayEllipse(
            double raHours, double decDeg, OverlayCandidateMarker.Ellipse e,
            float arcminToPixels, double ppr, float cxView, float cyView,
            float centerX, float centerY, RGBAColor32 color,
            float scale = 1f, float strokeWidth = 1f)
        {
            var semiMajorPx = MathF.Max(e.SemiMajArcmin * arcminToPixels * scale, 1f);
            var semiMinorPx = MathF.Max(e.SemiMinArcmin * arcminToPixels * scale, 0.5f);

            // Screen-space direction of celestial north at the object (project a point 1' north and
            // subtract), so the ellipse stays correctly oriented under view rotation + stereographic
            // distortion. Fall back to a circle-ish trace along screen axes if north can't be sampled.
            float dnx = 0f, dny = -1f;
            if (SkyMapProjection.ProjectWithMatrix(raHours, decDeg + 1.0 / 60.0, State.CurrentViewMatrix,
                    ppr, cxView, cyView, out var nx, out var ny))
            {
                dnx = nx - centerX;
                dny = ny - centerY;
            }

            var paRad = Half.IsNaN(e.PositionAngle) ? 0f : (float)((double)e.PositionAngle * Math.PI / 180.0);
            var (majorX, majorY, minorX, minorY) = OverlayEngine.ComputeEllipseScreenAxes(dnx, dny, paRad);

            // Adaptive tessellation: a small marker looks round with far fewer than 32 segments, so scale
            // the count with on-screen radius -- clamp(radiusPx/2, 8, 32). The whole ring is then ONE
            // batched Renderer.DrawPolyline (a single GPU draw on the Vk/WebGL backends) instead of
            // `segments` separate DrawLine calls; the wide-FOV [O] overlay traces hundreds of these per
            // frame, so this is the dominant browser draw-call win. Called on Renderer directly -- there is
            // no DrawPolyline forwarder on PixelWidgetBase and adding one would force a DIR.Lib release; the
            // batched override lives on the GPU renderers, the CPU RgbaImageRenderer keeps the base loop.
            var screenRadiusPx = MathF.Max(semiMajorPx, semiMinorPx);
            var segments = Math.Clamp((int)(screenRadiusPx * 0.5f), 8, 32);
            Span<(float X, float Y)> ring = stackalloc (float X, float Y)[segments + 1];
            for (var i = 0; i <= segments; i++)
            {
                var theta = i * (2.0 * Math.PI / segments);
                var (sinT, cosT) = Math.SinCos(theta);
                var ex = (float)(semiMajorPx * cosT);
                var ey = (float)(semiMinorPx * sinT);
                ring[i] = (centerX + ex * majorX + ey * minorX, centerY + ex * majorY + ey * minorY);
            }
            Renderer.DrawPolyline(ring, color, (int)MathF.Max(1f, MathF.Round(strokeWidth)));
        }

        // A star cross: two short arms. Mirrors VkOverlayShapes.DrawCross on the GPU side.
        private void DrawOverlayCross(float x, float y, float armPx, RGBAColor32 color)
        {
            DrawLine(x - armPx, y, x + armPx, y, color);
            DrawLine(x, y - armPx, x, y + armPx, color);
        }
    }
}
