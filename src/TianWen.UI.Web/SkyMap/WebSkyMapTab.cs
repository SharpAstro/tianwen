using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using DIR.Lib;
using WebGl.Renderer;

namespace TianWen.UI.Web.SkyMap
{
    /// <summary>
    /// The browser sky map: <see cref="SkyMapTab{TSurface}"/> (all labels, search, info panel,
    /// input, planet/comet markers - drawn via the generic renderer primitives) over
    /// <see cref="WebGlSkyMapPipeline"/> for the GPU star field + line work, mirroring
    /// VkSkyMapTab's split on desktop. v1 scope: stars (HR/HIP bright seed) + lines; the
    /// object-overlay / mount / schedule-marker hooks stay at their no-op base until needed.
    /// </summary>
    internal sealed class WebSkyMapTab(WebGlRenderer renderer) : SkyMapTab<WebGlContext>(renderer)
    {
        private readonly WebGlSkyMapPipeline _pipeline = new(renderer);

        /// <summary>Hands a fetched + decoded full Tycho-2 star buffer to the GPU pipeline; it
        /// swaps over the HR seed on the next render frame. See
        /// <see cref="WebGlSkyMapPipeline.SubmitTycho2Stars"/>.</summary>
        public void SubmitTycho2Stars(float[] verts, int starCount) => _pipeline.SubmitTycho2Stars(verts, starCount);

        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect, string fontPath,
            System.DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site)
        {
            // Sun-altitude-tinted sky background (the base implementation).
            base.RenderSkyMap(db, contentRect, fontPath, viewingTime, siteLat, siteLon, site);

            _pipeline.EnsureGeometry(db);
            // The web map draws full-canvas: viewport == canvas == contentRect (the razor host
            // hands the whole drawing buffer to the active tab).
            _pipeline.UpdateFrame(State, contentRect.Width, contentRect.Height, site);
            _pipeline.Draw(State, site);
        }
    }
}
