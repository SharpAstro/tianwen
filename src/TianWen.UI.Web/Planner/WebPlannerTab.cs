using System;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using TianWen.UI.Web.SkyMap;
using WebGl.Renderer;

namespace TianWen.UI.Web.Planner
{
    /// <summary>
    /// The browser planner: <see cref="PlannerTab{TSurface}"/> with the details strip's picture and the large view
    /// drawn through <see cref="WebGlObjectPictures"/>, the same drawer the web atlas (<see cref="WebSkyMapTab"/>) uses,
    /// so the planner and the atlas show one picture.
    /// </summary>
    internal sealed class WebPlannerTab(WebGlRenderer renderer) : PlannerTab<WebGlContext>(renderer), IDisposable
    {
        private WebGlObjectPictures? _pictures;

        /// <summary>
        /// Asks the page for a frame. The browser paints per event, so a picture that finished loading while
        /// nothing happened would otherwise wait for the next pointer move to appear.
        /// </summary>
        public Action? RequestRepaint { get; set; }

        protected override void DrawObjectPicture(in ObjectArticleImage image, RectF32 rect)
        {
            _pictures ??= new WebGlObjectPictures(renderer, () =>
            {
                RequestRedraw();
                RequestRepaint?.Invoke();
            }, Logger);
            _pictures.Draw(in image, rect);
        }

        public void Dispose() => _pictures?.Dispose();
    }
}
