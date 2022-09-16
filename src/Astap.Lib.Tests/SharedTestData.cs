using Astap.Lib.Astrometry;

namespace Astap.Lib.Tests;

public static class SharedTestData
{
    internal const CatalogIndex NGC7293 = (CatalogIndex)((ulong)'N' << 28 | '7' << 21 | '2' << 14 | '9' << 7 | '3');
    internal const CatalogIndex NGC0056 = (CatalogIndex)((ulong)'N' << 28 | '0' << 21 | '0' << 14 | '5' << 7 | '6');
    internal const CatalogIndex IC1000 = (CatalogIndex)((ulong)'I' << 28 | '1' << 21 | '0' << 14 | '0' << 7 | '0');
    internal const CatalogIndex IC0715NW = (CatalogIndex)((ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'1' << 28 | '5' << 21 | '_' << 14 | 'N' << 7 | 'W');
    internal const CatalogIndex IC0720_NED02 = (CatalogIndex)((ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'2' << 28 | '0' << 21 | 'N' << 14 | '0' << 7 | '2');
    internal const CatalogIndex M040 = (CatalogIndex)('M' << 21 | '0' << 14 | '4' << 7 | '0');
    internal const CatalogIndex M102 = (CatalogIndex)('M' << 21 | '1' << 14 | '0' << 7 | '2');
    internal const CatalogIndex ESO056_115 = (CatalogIndex)((ulong)'E' << 49 | (ulong)'0' << 42 | (ulong)'5' << 35 | (ulong)'6' << 28 | '-' << 21 | '1' << 14 | '1' << 7 | '5');
    internal const CatalogIndex PSR_J2144_3933s = (CatalogIndex)((ulong)'P' << 56 | (ulong)'r' << 49 | (ulong)'J' << 42 | (ulong)'A' << 35 | (ulong)'A' << 28 | 'I' << 21 | 'A' << 14 | 'B' << 7 | 'w');

}
