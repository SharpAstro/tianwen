using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;
using DIR.Lib;
using WebGl.Renderer;

namespace TianWen.UI.Web.SkyMap
{
    /// <summary>
    /// The browser sky map: <see cref="SkyMapTab{TSurface}"/> (all labels, search, info panel,
    /// input, planet/comet markers - drawn via the generic renderer primitives) over
    /// <see cref="WebGlSkyMapPipeline"/> for the GPU star field + line work, mirroring
    /// VkSkyMapTab's split on desktop. The [O]/[D] object overlay runs the shared
    /// <see cref="SkyMapTab{TSurface}.RenderObjectOverlayPrimitive"/> (gather, projection, labels)
    /// with its markers rasterised by an instanced GPU draw, as on desktop; mount / schedule-marker
    /// hooks stay at their no-op base until needed.
    /// </summary>
    internal sealed class WebSkyMapTab(WebGlRenderer renderer) : SkyMapTab<WebGlContext>(renderer)
    {
        private readonly WebGlSkyMapPipeline _pipeline = new(renderer);

        /// <summary>Hands a fetched + decoded full Tycho-2 star buffer to the GPU pipeline; it
        /// swaps over the HR seed on the next render frame. See
        /// <see cref="WebGlSkyMapPipeline.SubmitTycho2Stars"/>.</summary>
        public void SubmitTycho2Stars(float[] verts, int starCount) => _pipeline.SubmitTycho2Stars(verts, starCount);

        /// <summary>Hands over a buffer already in chunk layout with its table, as
        /// <see cref="StarChunkAccumulator.Pack"/> returns it.</summary>
        public void SubmitTycho2Stars(float[] verts, int starCount, StarChunk[] chunks)
            => _pipeline.SubmitTycho2Stars(verts, starCount, chunks);

        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect,
            System.DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site)
        {
            // Sun-altitude-tinted sky background (the base implementation).
            var mark = System.Diagnostics.Stopwatch.GetTimestamp();
            base.RenderSkyMap(db, contentRect, viewingTime, siteLat, siteLon, site);
            SkyBackgroundMs += Elapsed(ref mark);

            _pipeline.EnsureGeometry(db);
            SkyGeometryMs += Elapsed(ref mark);

            // The web map draws full-canvas: viewport == canvas == contentRect (the razor host
            // hands the whole drawing buffer to the active tab).
            _pipeline.UpdateFrame(State, contentRect.Width, contentRect.Height, site);
            SkyUpdateFrameMs += Elapsed(ref mark);

            _pipeline.Draw(State, site);
            SkyDrawMs += Elapsed(ref mark);
        }

        private static double Elapsed(ref long mark)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(mark, now).TotalMilliseconds;
            mark = now;
            return ms;
        }

        /// <summary>
        /// Per-phase totals for the map draw, which the host reports alongside its own. The host's
        /// timing puts 96% of a repaint inside this method, and no browser-side tool can go further:
        /// a trace sees the whole WASM module as one frame. These four split it the only way that
        /// matters -- CPU geometry rebuilt per frame (<see cref="SkyUpdateFrameMs"/>) against GPU
        /// submission (<see cref="SkyDrawMs"/>) -- because the fixes for those two are opposite.
        /// </summary>
        internal double SkyBackgroundMs { get; private set; }

        /// <inheritdoc cref="SkyBackgroundMs"/>
        internal double SkyGeometryMs { get; private set; }

        /// <inheritdoc cref="SkyBackgroundMs"/>
        internal double SkyUpdateFrameMs { get; private set; }

        /// <inheritdoc cref="SkyBackgroundMs"/>
        internal double SkyDrawMs { get; private set; }

        /// <summary>Draws the [O] catalog overlay + [D] dark nebulae + pinned-target landmarks through
        /// the shared path: same candidate gather, projection and label placement as desktop, with the
        /// markers rasterised by <see cref="DrawOverlayMarkers"/> below.</summary>
        protected override void RenderObjectOverlay(
            ICelestialObjectDB db, RectF32 contentRect,
            float baseFontSize, SiteContext site, bool dimBelowHorizon, PlannerState plannerState,
            bool showAllOverlays)
            => RenderObjectOverlayPrimitive(db, contentRect, baseFontSize,
                site, dimBelowHorizon, plannerState, showAllOverlays);

        // Inputs the instance stream is a function of. A pan moves NONE of them, which is what makes
        // the buffer worth keying: the gather is already cached across a pan, so re-uploading its
        // projection-independent instances every pointermove would put back a per-event cost the
        // cache exists to remove. The horizon term is the site's own hour angle bucketed to 30 s, the
        // same bucket UpdateFrame uses for the horizon and meridian line sets and for the same reason
        // -- dimming crosses the horizon at real-clock speed, not at input speed.
        private readonly record struct OverlayInstanceKey(
            int CandidateVersion, int CandidateCount, float ArcminToPixels,
            float DpiScale, float FovAlpha, bool DimBelowHorizon, double LstBucket);

        private OverlayInstanceKey _overlayInstanceKey;
        private bool _hasOverlayInstanceKey;
        private readonly List<float> _overlayInstances = new(1024);

        /// <summary>
        /// How many times the instance stream was rebuilt and submitted, the sibling of
        /// <see cref="SkyMapTab{TSurface}.PrimOverlayGathers"/> and load-bearing for the same reason: a
        /// stale-keyed re-upload draws the byte-identical frame, so nothing observable -- pixels
        /// included -- separates a key that holds across a pan from one that misses on every event.
        /// Only a count can.
        /// </summary>
        internal int OverlayInstanceUploads { get; private set; }

        /// <summary>
        /// One instanced GPU draw for every ellipse + circle marker, replacing a tessellated polyline
        /// per marker. Crosses stay on the primitive path: they have no angular extent for the shader
        /// to project, and at a full-sky zoom they are 772 markers against 7,072 here.
        /// </summary>
        protected override void DrawOverlayMarkers(
            IReadOnlyList<OverlayCandidate> candidates,
            IReadOnlyList<OverlayItem> items,
            int candidateVersion,
            float arcminToPixels, double ppr, float cxView, float cyView,
            float dpiScale, float fovAlpha, bool dimBelowHorizon, SiteContext site)
        {
            var key = new OverlayInstanceKey(
                candidateVersion, candidates.Count, arcminToPixels, dpiScale, fovAlpha,
                dimBelowHorizon,
                dimBelowHorizon && site.IsValid ? Math.Round(site.LST * 120.0) / 120.0 : 0.0);
            if (!_hasOverlayInstanceKey || !_overlayInstanceKey.Equals(key))
            {
                // The browser has no theme switcher, so the halo takes the engine's fixed landmark
                // colour rather than the desktop's themed accent.
                OverlayEllipseInstances.Build(
                    candidates, _overlayInstances,
                    arcminToPixels, dpiScale, fovAlpha, dimBelowHorizon, site,
                    OverlayEngine.PinnedMarkerColor,
                    OverlayEngine.PinnedHaloColor with { Alpha = (byte)(OverlayEngine.PinnedHaloColor.Alpha * fovAlpha) });
                _pipeline.SubmitOverlayInstances(CollectionsMarshal.AsSpan(_overlayInstances));
                _overlayInstanceKey = key;
                _hasOverlayInstanceKey = true;
                OverlayInstanceUploads++;
            }

            _pipeline.DrawOverlay();

            // Crosses (stars) from the projected items, so an off-screen star draws nothing.
            foreach (var item in items)
            {
                if (item.Marker.Kind != OverlayMarkerKind.Cross)
                {
                    continue;
                }

                var alpha = OverlayEngine.MarkerAlpha(
                    item.IsPinned, item.RA, item.Dec, dimBelowHorizon, site, fovAlpha);

                var (r, g, b) = item.Color;
                var color = item.IsPinned
                    ? OverlayEngine.PinnedMarkerColor with { Alpha = (byte)(alpha * 255f) }
                    : RGBAColor32.FromFloat(r, g, b, alpha);
                var armPx = item.Marker.ArmPx;
                DrawLine(item.ScreenX - armPx, item.ScreenY, item.ScreenX + armPx, item.ScreenY, color);
                DrawLine(item.ScreenX, item.ScreenY - armPx, item.ScreenX, item.ScreenY + armPx, color);
            }
        }
    }
}
