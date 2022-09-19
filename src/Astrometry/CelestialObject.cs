namespace Astap.Lib.Astrometry;

public readonly record struct CelestialObject(CatalogIndex Index, ObjectType ObjectType, double RA, double Dec, Constellation Constellation, double V_Mag);