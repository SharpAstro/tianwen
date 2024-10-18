using System.Collections.Generic;

namespace Astap.Lib.Astrometry.Catalogs;

public interface IRaDecIndex
{
    IReadOnlyCollection<CatalogIndex> this[double ra, double dec] { get; }
}