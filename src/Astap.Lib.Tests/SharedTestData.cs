using Astap.Lib.Astrometry;

namespace Astap.Lib.Tests;

public static class SharedTestData
{
    internal const CatalogIndex NGC7293 = (CatalogIndex)((ulong)'N' << 28 | '7' << 21 | '2' << 14 | '9' << 7 | '3');
    internal const CatalogIndex NGC0056 = (CatalogIndex)((ulong)'N' << 28 | '0' << 21 | '0' << 14 | '5' << 7 | '6');
    internal const CatalogIndex IC0458 = (CatalogIndex)((ulong)'I' << 28 | '0' << 21 | (ulong)'4' << 14 | (ulong)'5' << 7 | '8');
    internal const CatalogIndex IC0715NW = (CatalogIndex)((ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'1' << 28 | '5' << 21 | '_' << 14 | 'N' << 7 | 'W');
    internal const CatalogIndex IC0720_NED02 = (CatalogIndex)((ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'2' << 28 | '0' << 21 | 'N' << 14 | '0' << 7 | '2');
    internal const CatalogIndex IC1000 = (CatalogIndex)((ulong)'I' << 28 | '1' << 21 | '0' << 14 | '0' << 7 | '0');
    internal const CatalogIndex M040 = (CatalogIndex)('M' << 21 | '0' << 14 | '4' << 7 | '0');
    internal const CatalogIndex M102 = (CatalogIndex)('M' << 21 | '1' << 14 | '0' << 7 | '2');
    internal const CatalogIndex ESO056_115 = (CatalogIndex)((ulong)'E' << 49 | (ulong)'0' << 42 | (ulong)'5' << 35 | (ulong)'6' << 28 | '-' << 21 | '1' << 14 | '1' << 7 | '5');
    internal const CatalogIndex PSR_J2144_3933s = (CatalogIndex)((ulong)'P' << 56 | (ulong)'r' << 49 | (ulong)'J' << 42 | (ulong)'B' << 35 | (ulong)'D' << 28 | 'A' << 21 | 'e' << 14 | 'u' << 7 | 'w');
    internal const CatalogIndex PSR_B0633_17n = (CatalogIndex)((ulong)'P' << 56 | (ulong)'r' << 49 | (ulong)'B' << 42 | (ulong)'A' << 35 | (ulong)'T' << 28 | 'y' << 21 | 'A' << 14 | 'I' << 7 | 'g');
    internal const CatalogIndex SH2_6 = (CatalogIndex)((ulong)'S' << 42 | (ulong)'h' << 35 | (ulong)'2' << 28 | '-' << 21 | '0' << 14 | '0' << 7 | '6');
    internal const CatalogIndex TrES_3 = (CatalogIndex)((ulong)'T' << 35 | (ulong)'r' << 28 | 'E' << 21 | 'S' << 14 | '0' << 7 | '3');
}
