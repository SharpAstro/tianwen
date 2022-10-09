namespace Astap.Lib.Astrometry;

public enum ObjectType : ulong
{
    Star = '*',
    DoubleStar = '*' << 7 | '*',
    Pulsar = 'P' << 14 | 'S' << 7 | 'R',
    AssociationOfStars = '*' << 21 | 'A' << 14 | 's' << 7 | 's',
    OpenCluster = 'O' << 14 | 'C' << 7 | 'l',
    GlobularCluster = 'G' << 14 | 'C' << 7 | 'l',
    ClusterAndNebula = 'C' << 21 | 'l' << 14 | '+' << 7 | 'N',
    Galaxy = 'G',
    GalaxyPair = ((ulong)'G' << 28) | 'P' << 21 | 'a' << 14 | 'i' << 7 | 'r',
    GalaxyTriplet = ((ulong)'G' << 28) | 'T' << 21 | 'r' << 14 | 'p' << 7 | 'l',
    GroupOfGalaxies = ((ulong)'G' << 35) | ((ulong)'G' << 28) | 'r' << 21 | 'o' << 14 | 'u' << 7 | 'p',
    PlanetaryNebula = 'P' << 7 | 'N',
    HIIRegion = 'H' << 14 | 'I' << 7 | 'I',
    DarkNebula = 'D' << 21 | 'r' << 14 | 'k' << 7 | 'N',
    EmissionNebula = 'E' << 14 | 'm' << 7 | 'N',
    Nebula = 'N' << 14 | 'e' << 7 | 'b',
    ReflectionNebula = 'R' << 14 | 'f' << 7 | 'N',
    SupernovaRemnant = 'S' << 14 | 'N'  << 7 | 'R',
    NovaStar = 'N' << 21 | 'o' << 14 | 'v' << 7 | 'a',
    NonExistent = ((ulong)'N' << 28) | 'o' << 21 | 'n' << 14 | 'v' << 7 | 'a',
    Duplicate = 'D' << 14 | 'u' << 7 | 'p',
    Other = ((ulong)'O' << 28) | 't' << 21 | 'h' << 14 | 'e' << 7 | 'r'
}
