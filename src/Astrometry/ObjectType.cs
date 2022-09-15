namespace Astap.Lib.Astrometry;

public enum ObjectType : ulong
{
    Star = '*',
    DoubleStar = '*' << 8 | '*',
    Pulsar = 'P' << 16 | 'S' << 8 | 'R',
    AssociationOfStars = '*' << 24 | 'A' << 16 | 's' << 8 | 's',
    OpenCluster = 'O' << 16 | 'C' << 8 | 'l',
    GlobularCluster = 'G' << 16 | 'C' << 8 | 'l',
    ClusterAndNebula = 'C' << 24 | 'l' << 16 | '+' << 8 | 'N',
    Galaxy = 'G',
    GalaxyPair = ((ulong)'G' << 32) | 'P' << 24 | 'a' << 16 | 'i' << 8 | 'r',
    GalaxyTriplet = ((ulong)'G' << 32) | 'T' << 24 | 'r' << 16 | 'p' << 8 | 'l',
    GroupOfGalaxies = ((ulong)'G' << 40) | ((ulong)'G' << 32) | 'r' << 24 | 'o' << 16 | 'u' << 8 | 'p',
    PlanetaryNebula = 'P' << 8 | 'N',
    HIIRegion = 'H' << 16 | 'I' << 8 | 'I',
    DarkNebula = 'D' << 24 | 'r' << 16 | 'k' << 8 | 'N',
    EmissionNebula = 'E' << 16 | 'm' << 8 | 'N',
    Nebula = 'N' << 16 | 'e' << 8 | 'b',
    ReflectionNebula = 'R' << 16 | 'f' << 8 | 'N',
    SupernovaRemnant = 'S' << 16 | 'N'  << 8 | 'R',
    NovaStar = 'N' << 24 | 'o' << 16 | 'v' << 8 | 'a',
    NonExistent = ((ulong)'N' << 32) | 'o' << 24 | 'n' << 16 | 'v' << 8 | 'a',
    Duplicate = 'D' << 16 | 'u' << 8 | 'p',
    Other = ((ulong)'O' << 32) | 't' << 24 | 'h' << 16 | 'e' << 8 | 'r'
}
