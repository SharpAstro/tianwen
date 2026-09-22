using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions
{
    public partial class SkyMapTab<TSurface>
    {
        /// <summary>
        /// The catalogue object a tap at (<paramref name="x"/>, <paramref name="y"/>) resolves to on
        /// this map, or null, for a host that paints the map BEHIND its own content and owns the
        /// selection itself: the FITS viewer's sky backdrop, whose frame overlay stops five degrees
        /// from the frame while the map's markers run to the edge of the pane.
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
    }
}
