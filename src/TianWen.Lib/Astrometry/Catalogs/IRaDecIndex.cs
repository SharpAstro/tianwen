using System.Collections.Generic;

namespace TianWen.Lib.Astrometry.Catalogs;

public interface IRaDecIndex
{
    IReadOnlyCollection<CatalogIndex> this[double ra, double dec] { get; }

    /// <summary>
    /// The same cell as the indexer, as a struct a <c>foreach</c> walks without allocating: the
    /// deep-sky entries first, then (on the composite grid) the Tycho-2 stars scanned as the caller
    /// advances. The indexer built a <see cref="List{T}"/> per cell for the Tycho-2 half, 27.6 KB
    /// per nine-cell sky-map resolve; this is for the callers that walk cells per frame. The default
    /// materialises the indexer's answer, for an implementation that has no cheaper shape (test
    /// fakes); both real indexes override it.
    /// </summary>
    RaDecCell EnumerateCell(double ra, double dec) => RaDecCell.FromCollection(this[ra, dec]);
}