using System.Collections.Generic;

namespace TianWen.Lib.Astrometry.Catalogs;

public interface IRaDecIndex
{
    IReadOnlyCollection<CatalogIndex> this[double ra, double dec] { get; }
}