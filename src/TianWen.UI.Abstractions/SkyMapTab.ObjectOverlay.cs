using System;
using System.Collections.Generic;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// CPU / primitive object-overlay drawing ([O] catalog markers + labels, [D] dark nebulae, and
    /// always-on pinned planner-target landmarks) for renderers WITHOUT a GPU instanced-ellipse
    /// pipeline -- i.e. the browser sky map. It shares the candidate gather / projection / label
    /// placement with the desktop GPU path (all in <see cref="Overlays.OverlayEngine"/>); only the
    /// final rasterisation differs -- ellipses/crosses/circles are traced with the surface-agnostic
    /// <c>DrawLine</c>/<c>DrawCircle</c>/<c>DrawText</c> primitives here, versus the instanced GPU
    /// draw in <c>VkSkyMapTab.RenderObjectOverlay</c>. The two are hand-maintained mirrors, exactly
    /// like <see cref="TryDrawShapeMarker"/> mirrors the GPU selection ellipse.
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

        /// <summary>
        /// How many times the candidate gather has actually run, the counterpart of
        /// <see cref="SkyMapState.PlanetCacheRebuilds"/> and load-bearing for the same reason: the cost
        /// of this cache is invisible from its output (a stale-keyed rebuild draws the identical frame,
        /// just slower), so only a count can tell a cache that holds from one that misses every event.
        /// Bumped by <see cref="BuildOverlayKeyForTest"/>'s production path, not by the test seam.
        /// </summary>
        internal int PrimOverlayGathers { get; private set; }

        /// <summary>
        /// Cumulative wall time inside the candidate gather. Paired with
        /// <see cref="PrimOverlayGathers"/> it separates "how often" from "how expensive", which a
        /// frame-duration trace cannot: the browser runs the gather INSIDE the animation-frame
        /// callback, so a slow frame and a slow gather are the same sample until they are timed apart.
        /// </summary>
        internal double PrimOverlayGatherMs { get; private set; }

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
                return;
            }

            var showDark = State.ShowDarkNebulae;
            var pinned = PlannerActions.GetPinnedCatalogIndices(plannerState.Proposals);

            // Both layers off and nothing pinned: nothing to draw (mirrors VkSkyMapTab's early-out).
            if (!showAllOverlays && !showDark && pinned is null)
            {
                _primOverlayCandidates.Clear();
                _primOverlayHasKey = false;
                return;
            }

            var fov = State.FieldOfViewDeg;
            var cxView = contentRect.X + contentRect.Width * 0.5f;
            var cyView = contentRect.Y + contentRect.Height * 0.5f;
            var ppr = SkyMapProjection.PixelsPerRadian(contentRect.Height, fov);

            var key = BuildOverlayKey(contentRect, fov, cxView, cyView, ppr, showAllOverlays, showDark, plannerState);
            if (!_primOverlayHasKey || !_primOverlayKey.Equals(key))
            {
                PrimOverlayGathers++;
                var gatherStart = System.Diagnostics.Stopwatch.GetTimestamp();
                OverlayEngine.GatherSkyMapOverlayCandidates(
                    State.CurrentViewMatrix, fov, contentRect, dpiScale, db, pinned, _primOverlayCandidates);
                PrimOverlayGatherMs += System.Diagnostics.Stopwatch.GetElapsedTime(gatherStart).TotalMilliseconds;

                // Per-layer visibility (same rule as VkSkyMapTab): dark nebulae follow [D], every other
                // catalog object follows [O]; pinned targets bypass both so they stay visible.
                if (!showAllOverlays || !showDark)
                {
                    _primOverlayCandidates.RemoveAll(c => !c.IsPinned
                        && (c.ObjectType == ObjectType.DarkNeb ? !showDark : !showAllOverlays));
                }

                _primOverlayKey = key;
                _primOverlayHasKey = true;
            }

            if (_primOverlayCandidates.Count == 0)
            {
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
            OverlayEngine.ProjectSkyMapCandidatesInto(_primOverlayCandidates, State, contentRect, dpiScale, _primOverlayItems);
            if (_primOverlayItems.Count == 0)
            {
                return;
            }

            // Pass 1: markers.
            foreach (var item in _primOverlayItems)
            {
                if ((uint)item.CandidateIndex >= (uint)_primOverlayCandidates.Count)
                {
                    continue;
                }
                var cand = _primOverlayCandidates[item.CandidateIndex];
                var sx = item.ScreenX;
                var sy = item.ScreenY;

                var below = dimBelowHorizon && !site.IsAboveHorizon(cand.RA, cand.Dec);
                var alpha = below ? 0.35f : 1f;
                if (!cand.IsPinned)
                {
                    alpha *= fovAlpha;
                }

                var (cr, cg, cb) = cand.Color;
                var color = cand.IsPinned
                    ? new RGBAColor32(0xFF, 0x70, 0x30, (byte)(alpha * 255f))
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

            // Pass 2: labels via the shared best-effort placement (stable slots) + DrawText, over the
            // items projected once above.
            var labelSize = baseFontSize * dpiScale * 0.85f;
            var lineH = labelSize * 1.2f;
            var measureText = (string text, float size) => Renderer.MeasureText(text.AsSpan(), fontPath, size).Width;
            OverlayEngine.PlaceLabelsBestEffort(_primOverlayItems, labelSize, 4f, measureText,
                (item, lx, ly) =>
                {
                    var below = dimBelowHorizon && !site.IsAboveHorizon(item.RA, item.Dec);
                    var a = below ? 0.35f : 1f;
                    if (!item.IsPinned)
                    {
                        a *= fovAlpha;
                    }
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
            // dark nebulae ON that gate cost a gather on EVERY step of a zoom-out (30 against 3), which
            // is to say the users who most wanted the overlay were the ones getting none of the fix.
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
