using System;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// What this map offers a HOST that paints it behind its own content and owns the selection
    /// itself: the FITS viewer's sky backdrop, whose frame overlay stops five degrees from the frame
    /// while the map's markers run to the edge of the pane. Two things, both through the map's own
    /// machinery so a host cannot disagree with it: which object a tap names, and where that object's
    /// selection ellipse lies on the map's projection.
    /// </summary>
    public partial class SkyMapTab<TSurface>
    {
        /// <summary>
        /// Where a "select at this screen point" goes when a HOST owns the selection. Every way the
        /// map asks for a selection (a tap that did not become a drag, a click on an object label, a
        /// click on a planet or comet label) emits through <see cref="EmitSelectAt"/>; with this set,
        /// the point reaches the host, otherwise it is posted as <see cref="SkyMapClickSelectSignal"/>
        /// for the atlas's own handler. The FITS viewer sets it, because its backdrop map's label
        /// regions win the hit test over the viewer's own press and used to consume a tap on a label
        /// into a signal nothing there subscribed to: the label could be seen and not clicked.
        /// </summary>
        public Action<float, float>? HostSelectAt { get; set; }

        /// <summary>
        /// The one exit for a selection request; see <see cref="HostSelectAt"/>. The point is the
        /// OBJECT's screen position for a label click (the bridges re-synthesise it there), so the
        /// host resolves it exactly as it would a tap on the marker.
        /// </summary>
        protected void EmitSelectAt(float screenX, float screenY, InputModifier modifiers)
        {
            if (HostSelectAt is { } host)
            {
                host(screenX, screenY);
                return;
            }

            PostSignal(new SkyMapClickSelectSignal(screenX, screenY, modifiers));
        }

        /// <summary>
        /// The catalogue object a tap at (<paramref name="x"/>, <paramref name="y"/>) resolves to on
        /// this map, or null.
        /// </summary>
        /// <remarks>
        /// The same call, with the same instant and the same pinned set, that this tab's own hover
        /// and click go through (<see cref="SkyMapSearchActions.ResolveHoverAtScreenPoint"/>), so a
        /// host's tap lands on what the wash under the pointer said it would. A planet or a comet is
        /// declined: they are ephemeris positions with no catalogue entry for a host's panel to be
        /// built from, and a host that wants them takes the map's own panel instead.
        /// </remarks>
        internal CatalogIndex? ResolveCatalogObjectAt(float x, float y)
        {
            if (_plannerState is not { ObjectDb: { } db } plannerState)
            {
                return null;
            }

            var hit = SkyMapSearchActions.ResolveHoverAtScreenPoint(
                State, db, _lastViewingTime, x, y, PinnedCatalogIndices(plannerState), plannerState.Comets);

            return hit is { IsEphemeris: false } target ? target.Index : null;
        }

        /// <summary>
        /// Where a host's selection lies on THIS map: the object's screen position under the map's
        /// projection and, for a shaped object, its selection ellipse's semi-axis vectors from the
        /// same solver this tab's own selection marker draws with. False when the object does not
        /// project (behind the view, or no content rect yet); a shapeless object projects with both
        /// axes left <c>default</c>, and the host draws its own shapeless fallback there.
        /// </summary>
        /// <remarks>
        /// A host that placed its ring through its own frame's WCS instead put NGC 7320's ring 330 px
        /// from where this map had drawn the galaxy, thirty degrees off an M31 frame: a gnomonic
        /// solution fitted to one field says nothing about a point that far from it, which is the
        /// very reason the host's catalogue search stops five degrees out and hands the tap to the map.
        /// </remarks>
        internal bool TryPlaceSelectionForHost(in SkyMapInfoPanelData info,
            out float screenX, out float screenY, out (float X, float Y) semiAxisU, out (float X, float Y) semiAxisV)
        {
            screenX = 0f;
            screenY = 0f;
            semiAxisU = default;
            semiAxisV = default;

            var rect = State.LastContentRect;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return false;
            }

            var pixelsPerRadian = SkyMapProjection.PixelsPerRadian(rect.Height, State.FieldOfViewDeg);
            var cx = rect.X + (rect.Width * 0.5f);
            var cy = rect.Y + (rect.Height * 0.5f);
            if (!SkyMapProjection.ProjectWithMatrix(info.RA, info.Dec, State.CurrentViewMatrix, pixelsPerRadian, cx, cy,
                    out screenX, out screenY))
            {
                return false;
            }

            // False leaves both axes default, which is the shapeless answer, not a failure.
            TrySolveShapeEllipse(in info, pixelsPerRadian, screenX, screenY, out semiAxisU, out semiAxisV);
            return true;
        }
    }
}
