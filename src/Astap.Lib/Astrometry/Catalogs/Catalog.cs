namespace Astap.Lib.Astrometry.Catalogs;

using static Astap.Lib.EnumHelper;

public enum Catalog : ulong
{
    Abell = 'A' << 14 | 'C' << 7 | 'O',
    Barnard = 'B',
    BonnerDurchmusterung = 'd', // uses MSB = 1
    Caldwell = 'C',
    Collinder = 'C' << 7 | 'r', // Cl is normalised to Cr
    Ced = 'C' << 14 | 'e' << 7 | 'd', // Cederblad
    CG = 'C' << 7 | 'G', // Cometary Globule
    ESO = 'E',
    GJ = 'G' << 7 | 'J', // Gliese Jahreiß
    GUM = 'G' << 14 | 'U' << 7 | 'M',
    H = 'H', // Harvard open cluster catalog
    HAT_P = (ulong)'H' << 28 | 'A' << 21 | 'T' << 14 | '-' << 7 | 'P', // Hungarian Automated Telescope Network (North)
    HATS = 'H' << 21 | 'A' << 14 | 'T' << 7 | 'S', // Hungarian Automated Telescope Network (South)
    HD = 'H' << 7 | 'D', // Henry Draper
    HIP = 'H' << 7 | 'I', // Hipparcos
    HR = 'H' << 7 | 'R', // Havard revised bright star catalog
    HCG = 'H' << 14 | 'C' << 7 | 'G', // Hickson Compact Group
    IC = 'I',
    LDN = 'L' << 14 | 'D' << 7 | 'N', // Lynds Catalog of Dark Nebulae
    Melotte = 'M' << 14 | 'e' << 7 | 'l',
    Messier = 'M',
    NGC = 'N',
    Pl = 'P' << 7 | 'l', // Major planets, their moons and Sol
    PSR = 'P', // uses MSB = 1
    RCW = 'R' << 14| 'C' << 7 | 'W', //  Rodgers, Campbell & Whiteoak
    Sharpless2 = 'S' << 14 | 'h' << 7 | '2', // Sharpless 2
    TrES = 'T' << 21 | 'r' << 14 | 'E' << 7 | 'S',
    TwoMass = '2', // uses MSB = 1
    TwoMassX = 'x', // uses MSB = 1
    UGC = 'U', // Uppsala General Catalogue
    WASP = 'W' << 21 | 'A' << 14 | 'S' << 7 | 'P',
    WDS = 'W', // uses MSB = 1
    XO = 'X' << 7 | 'O'
}

public static class CatalogEx
{
    /// <summary>
    /// Returns the canonical catalog name, i.e. NGC, M, Barnard, ACO, Sh-2.
    /// Note that the enum values might be truncated so they should not be used for this, in general.
    /// </summary>
    /// <param name="catalog"></param>
    /// <returns></returns>
    public static string ToCanonical(this Catalog catalog)
        => catalog switch
        {
            Catalog.Abell => "ACO",
            Catalog.BonnerDurchmusterung => "BD",
            Catalog.Caldwell => "C",
            Catalog.Collinder => "Cr",
            Catalog.Melotte => "Mel",
            Catalog.Messier => "M",
            Catalog.HAT_P => "HAT-P",
            Catalog.Sharpless2 => "Sh2",
            Catalog.TwoMass => "2MASS",
            Catalog.TwoMassX => "2MASX",
            Catalog.WDS => "WDS",
            _ => catalog.ToString()
        };

    public static string ToAbbreviation(this Catalog catalog) => EnumValueToAbbreviation((ulong)catalog);
}