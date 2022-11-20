using System;

namespace Astap.Lib.Astrometry.Catalogs;

internal enum OpenNGCObjectType : ulong
{
    Star = '*',
    DoubleStar = '*' << 7 | '*',
    AssociationOfStars = '*' << 21 | 'A' << 14 | 's' << 7 | 's',
    OpenCluster = 'O' << 14 | 'C' << 7 | 'l',
    GlobularCluster = 'G' << 14 | 'C' << 7 | 'l',
    ClusterAndNebula = 'C' << 21 | 'l' << 14 | '+' << 7 | 'N',
    Galaxy = 'G',
    GalaxyPair = (ulong)'G' << 28 | 'P' << 21 | 'a' << 14 | 'i' << 7 | 'r',
    GalaxyTriplet = (ulong)'G' << 28 | 'T' << 21 | 'r' << 14 | 'p' << 7 | 'l',
    GroupOfGalaxies = (ulong)'G' << 35 | (ulong)'G' << 28 | 'r' << 21 | 'o' << 14 | 'u' << 7 | 'p',
    PlanetaryNebula = 'P' << 7 | 'N',
    HIIRegion = 'H' << 14 | 'I' << 7 | 'I',
    DarkNebula = 'D' << 21 | 'r' << 14 | 'k' << 7 | 'N',
    EmissionNebula = 'E' << 14 | 'm' << 7 | 'N',
    Nebula = 'N' << 14 | 'e' << 7 | 'b',
    ReflectionNebula = 'R' << 14 | 'f' << 7 | 'N',
    SupernovaRemnant = 'S' << 14 | 'N' << 7 | 'R',
    NovaStar = 'N' << 21 | 'o' << 14 | 'v' << 7 | 'a',
    // meta
    NonExistent = (ulong)'N' << 28 | 'o' << 21 | 'n' << 14 | 'E' << 7 | 'x',
    Duplicate = 'D' << 14 | 'u' << 7 | 'p',
    Other = (ulong)'O' << 28 | 't' << 21 | 'h' << 14 | 'e' << 7 | 'r'
}

internal static class OpenNGCObjectTypeEx
{
    internal static ObjectType ToObjectType(this OpenNGCObjectType openNGCObjectType) => openNGCObjectType switch
    {
        OpenNGCObjectType.Star => ObjectType.Star,
        OpenNGCObjectType.DoubleStar => ObjectType.DoubleStar,
        OpenNGCObjectType.AssociationOfStars => ObjectType.Association,
        OpenNGCObjectType.OpenCluster => ObjectType.OpenCluster,
        OpenNGCObjectType.GlobularCluster => ObjectType.GlobCluster,
        OpenNGCObjectType.Galaxy => ObjectType.Galaxy,
        OpenNGCObjectType.NovaStar => ObjectType.Nova,
        OpenNGCObjectType.DarkNebula => ObjectType.DarkNeb,
        OpenNGCObjectType.PlanetaryNebula => ObjectType.PlanetaryNeb,
        OpenNGCObjectType.HIIRegion => ObjectType.HIIReg,
        OpenNGCObjectType.GalaxyPair => ObjectType.PairG,
        OpenNGCObjectType.ReflectionNebula => ObjectType.RefNeb,
        OpenNGCObjectType.Nebula => ObjectType.GalNeb,
        OpenNGCObjectType.EmissionNebula or OpenNGCObjectType.ClusterAndNebula => ObjectType.EmObj,
        OpenNGCObjectType.SupernovaRemnant => ObjectType.SNRemnant,
        OpenNGCObjectType.Other => ObjectType.Unknown,
        OpenNGCObjectType.NonExistent => ObjectType.Inexistent,
        OpenNGCObjectType.Duplicate => ObjectType.Duplicate,
        // there is no 1:1 mapping for galaxy triplet, so map to group of galaxies
        OpenNGCObjectType.GalaxyTriplet or OpenNGCObjectType.GroupOfGalaxies => ObjectType.GroupG,
        _ => throw new ArgumentException($"No translation to SIMBAD {typeof(ObjectType)} exists for {openNGCObjectType}", nameof(openNGCObjectType))
    };
}