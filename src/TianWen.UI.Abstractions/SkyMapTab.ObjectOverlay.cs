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

        private readonly record struct PrimOverlayKey(
            double QuantRa, double QuantDec, double QuantFov,
            int RectW, int RectH, bool ShowAll, bool ShowDark, int PinHash);

        // Wide FOV: the gather sweeps the whole sphere, so the view centre drops out of the cache key.
        private const double PrimOverlayWideFovDeg = 90.0;

        /// <summary>
        /// Draws the object overlay using CPU primitives. A subclass whose renderer has no instanced
        /// overlay pipeline overrides <see cref="RenderObjectOverlay"/> to call this. Parameters mirror
        /// <see cref="RenderObjectOverlay"/>. <paramref name="showAllOverlays"/> is the [O] toggle; the
        /// [D] dark-nebula toggle is read from <see cref="SkyMapState.ShowDarkNebulae"/>. Pinned planner
        /// targets survive both gates and render as orange landmarks with a halo.
        /// </summary>
        protected void RenderObjectOverlayPrimitive(
            ICelestialObjectDB db, RectF32 contentRect, string fontPath,
            float baseFontSize, SiteContext site, bool dimBelowHorizon, PlannerState plannerState,
            bool showAllOverlays)
        {
            var dpiScale = DpiScale;
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
                OverlayEngine.GatherSkyMapOverlayCandidates(
                    State.CurrentViewMatrix, fov, contentRect, dpiScale, db, pinned, _primOverlayCandidates);

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
            var margin = 100f + arcminToPixels; // generous cull slop for large shapes

            // Pass 1: markers, drawn from the CANDIDATES (they carry arcmin + position angle; the
            // projected OverlayItem drops the PA since the GPU reads it off the candidate instead).
            foreach (var cand in _primOverlayCandidates)
            {
                if (!SkyMapProjection.ProjectWithMatrix(cand.RA, cand.Dec, State.CurrentViewMatrix,
                        ppr, cxView, cyView, out var sx, out var sy))
                {
                    continue;
                }
                if (sx < contentRect.X - margin || sx > contentRect.X + contentRect.Width + margin
                    || sy < contentRect.Y - margin || sy > contentRect.Y + contentRect.Height + margin)
                {
                    continue;
                }

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

                // Pinned halo behind the marker: 1.5x the marker size, floored at 16px, so a planned
                // target is spottable at any zoom.
                if (cand.IsPinned)
                {
                    var haloPx = cand.Marker switch
                    {
                        OverlayCandidateMarker.Ellipse e => MathF.Max(e.SemiMajArcmin * arcminToPixels * 1.5f, 16f * dpiScale),
                        OverlayCandidateMarker.Circle c => MathF.Max(c.RadiusPxAtDpi1 * dpiScale * 1.5f, 16f * dpiScale),
                        _ => 16f * dpiScale,
                    };
                    DrawCircle(sx, sy, haloPx, new RGBAColor32(0xFF, 0x60, 0x20, (byte)(0x50 * fovAlpha)), 3f);
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

            // Pass 2: labels via the shared best-effort placement (O(N), stable slots) + DrawText.
            OverlayEngine.ProjectSkyMapCandidatesInto(_primOverlayCandidates, State, contentRect, dpiScale, _primOverlayItems);
            if (_primOverlayItems.Count == 0)
            {
                return;
            }

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
                    var stepDeg = fov / 8.0;
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
        private void DrawOverlayEllipse(
            double raHours, double decDeg, OverlayCandidateMarker.Ellipse e,
            float arcminToPixels, double ppr, float cxView, float cyView,
            float centerX, float centerY, RGBAColor32 color)
        {
            var semiMajorPx = MathF.Max(e.SemiMajArcmin * arcminToPixels, 1f);
            var semiMinorPx = MathF.Max(e.SemiMinArcmin * arcminToPixels, 0.5f);

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
            Renderer.DrawPolyline(ring, color);
        }

        // A star cross: two short arms. Mirrors VkOverlayShapes.DrawCross on the GPU side.
        private void DrawOverlayCross(float x, float y, float armPx, RGBAColor32 color)
        {
            DrawLine(x - armPx, y, x + armPx, y, color);
            DrawLine(x, y - armPx, x, y + armPx, color);
        }
    }
}
