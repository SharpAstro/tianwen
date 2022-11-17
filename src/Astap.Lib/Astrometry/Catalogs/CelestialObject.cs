using System.Collections.Generic;

namespace Astap.Lib.Astrometry.Catalogs;

/// <summary>
/// Represents a single or collective object (e.g. a star cluster or a star within a cluster).
/// Coordinates are in J2000.
/// </summary>
/// <param name="Index">Encoded object index</param>
/// <param name="ObjectType">Object type (Star, HII region, ...)</param>
/// <param name="RA">RA in degrees, 0..24</param>
/// <param name="Dec">Dec in degrees, -90..90 (+ is north)</param>
/// <param name="Constellation">Main constellation this object resides in.</param>
/// <param name="V_Mag">Visual magnitude or V-mag (in UVB), NaN if not defined</param>
/// <param name="SurfaceBrightness">Surface brightness for galaxies in mag/arcsec^2, NaN if not defined</param>
/// <param name="CommonNames">A set of common names referring to this object</param>
public readonly record struct CelestialObject(
    CatalogIndex Index,
    ObjectType ObjectType,
    double RA,
    double Dec,
    Constellation Constellation,
    float V_Mag,
    float SurfaceBrightness,
    IReadOnlySet<string> CommonNames
);