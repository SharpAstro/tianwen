namespace Astap.Lib.Astrometry;

public record CelestialObject(CatalogIndex Index, ObjectType ObjectType, double RA, double Dec, Constellation Constellation);