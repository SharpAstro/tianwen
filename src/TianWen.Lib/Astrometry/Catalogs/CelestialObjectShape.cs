using System;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Shape information for extended objects (galaxies, nebulae, clusters).
/// All angular sizes are in arcminutes at the 25 B-mag/arcsec² isophote (RC3 system).
/// Compatible with both OpenNGC and LEDA/PGC catalogs.
/// </summary>
/// <param name="MajorAxis">Major axis diameter in arcminutes at 25 B-mag/arcsec² isophote</param>
/// <param name="MinorAxis">Minor axis diameter in arcminutes at 25 B-mag/arcsec² isophote</param>
/// <param name="PositionAngle">Position angle in degrees, measured from North through East (0–180), NaN if undefined</param>
public readonly record struct CelestialObjectShape(
    Half MajorAxis,
    Half MinorAxis,
    Half PositionAngle
);
