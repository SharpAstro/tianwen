namespace Astap.Lib.Astrometry;

public readonly record struct CelestialObject(
    CatalogIndex Index,
    ObjectType ObjectType,
    double RA, // RA in degrees, 0..24
    double Dec, // Dec in degrees, -90..90 (+ is north)
    Constellation Constellation,
    double V_Mag, // Visual magnitude or V-mag (in UVB), NaN if not defined
    double SurfaceBrightness // Surface brightness for galaxies in mag/arcsec^2, NaN if not defined
);