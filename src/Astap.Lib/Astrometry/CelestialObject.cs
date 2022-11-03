using System.Collections.Generic;

namespace Astap.Lib.Astrometry;

public readonly record struct CelestialObject(
    CatalogIndex Index,
    ObjectType ObjectType,
    double RA, // RA in degrees, 0..24
    double Dec, // Dec in degrees, -90..90 (+ is north)
    Constellation Constellation,
    float V_Mag, // Visual magnitude or V-mag (in UVB), NaN if not defined
    float SurfaceBrightness, // Surface brightness for galaxies in mag/arcsec^2, NaN if not defined,
    IReadOnlyList<string> CommonNames, // A list of common names referring to this object
    string? Component // Can be used for double star designations, galaxy clusters, etc.
);