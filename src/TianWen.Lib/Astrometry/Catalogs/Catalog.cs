namespace TianWen.Lib.Astrometry.Catalogs;

using System.Runtime.CompilerServices;
using static TianWen.Lib.EnumHelper;

public enum CanonicalFormat : byte
{
    Normal = 1,
    /// <summary>
    /// Usually the longer name, e.g. M => Messier, but Barnard => B
    /// </summary>
    Alternative = 2
}

public enum Catalog : ulong
{
    Abell = 'A' << 14 | 'C' << 7 | 'O',
    Barnard = 'B',
    BonnerDurchmusterung = 'd', // uses MSB = 1
    Caldwell = 'C',
    Collinder = 'C' << 7 | 'r', // Cl is normalised to Cr
    Ced = 'C' << 14 | 'e' << 7 | 'd', // Cederblad
    CG = 'C' << 7 | 'G', // Cometary Globule
    DG = 'D' << 7 | 'G',
    Dobashi = 'D' << 7 | 'o',
    ESO = 'E',
    GJ = 'G' << 7 | 'J', // Gliese Jahreiß
    GUM = 'G' << 14 | 'U' << 7 | 'M',
    H = 'H', // Harvard open cluster catalog
    HAT_P = (ulong)'H' << 28 | 'A' << 21 | 'T' << 14 | '-' << 7 | 'P', // Hungarian Automated Telescope Network (North)
    HATS = 'H' << 21 | 'A' << 14 | 'T' << 7 | 'S', // Hungarian Automated Telescope Network (South)
    HD = 'H' << 7 | 'D', // Henry Draper
    HH = 'H' << 7 | 'H', // Herbig-Haro
    HIP = 'H' << 7 | 'I', // Hipparcos
    HR = 'H' << 7 | 'R', // Havard revised bright star catalog
    HCG = 'H' << 14 | 'C' << 7 | 'G', // Hickson Compact Group
    IC = 'I',
    LDN = 'L' << 14 | 'D' << 7 | 'N', // Lynds Catalog of Dark Nebulae
    Melotte = 'M' << 14 | 'e' << 7 | 'l',
    Messier = 'M',
    NGC = 'N',
    Pl = 'P' << 7 | 'l', // Major planets, their moons and Sol (created by author)
    PSR = 'P', // Pulsars, uses MSB = 1
    RCW = 'R' << 14| 'C' << 7 | 'W', //  Rodgers, Campbell & Whiteoak
    Sharpless = 'S' << 14 | 'h' << 7 | '2', // Sharpless
    TrES = 'T' << 21 | 'r' << 14 | 'E' << 7 | 'S',
    TwoMass = '2', // uses MSB = 1
    TwoMassX = 'x', // uses MSB = 1
    Tycho2 = 'y', // uses MSB = 1
    UGC = 'U', // Uppsala General Catalogue
    vdB = 'v' << 14 | 'd' << 7 | 'B', // van den Bergh
    WASP = 'W' << 21 | 'A' << 14 | 'S' << 7 | 'P',
    WDS = 'W', // Washington double star catalogue, uses MSB = 1
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
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static string ToCanonical(this Catalog catalog, CanonicalFormat format = CanonicalFormat.Normal)
        => catalog switch
        {
            Catalog.Abell => "ACO",
            Catalog.Barnard when format is CanonicalFormat.Alternative => "B",
            Catalog.BonnerDurchmusterung => "BD",
            Catalog.Caldwell when format is CanonicalFormat.Normal => "C",
            Catalog.Collinder when format is CanonicalFormat.Normal => "Cr",
            Catalog.Melotte when format is CanonicalFormat.Normal => "Mel",
            Catalog.Messier when format is CanonicalFormat.Normal => "M",
            Catalog.HAT_P => "HAT-P",
            Catalog.Sharpless when format is CanonicalFormat.Normal => "Sh2",
            Catalog.TwoMass => "2MASS",
            Catalog.TwoMassX => "2MASX",
            Catalog.Tycho2 => "TYC",
            Catalog.WDS => "WDS",
            _ => catalog.ToString()
        };

    public static string ToAbbreviation(this Catalog catalog) => EnumValueToAbbreviation((ulong)catalog);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetNumericalIndexSize(this Catalog catalog)
        => catalog switch
        {
            Catalog.Abell => 4,
            Catalog.Barnard => 3,
            Catalog.BonnerDurchmusterung => 6,
            Catalog.Caldwell => 3,
            Catalog.Ced => 4,
            Catalog.CG => 4,
            Catalog.Collinder => 3,
            Catalog.DG => 4,
            Catalog.Dobashi => 5,
            Catalog.ESO => 7,
            Catalog.HCG => 4,
            Catalog.HD => 6,
            Catalog.GJ => 4,
            Catalog.GUM => 3,
            Catalog.H => 2,
            Catalog.HATS => 3,
            Catalog.HAT_P => 3,
            Catalog.HH => 5,
            Catalog.HIP => 6,
            Catalog.HR => 4,
            Catalog.IC => 4,
            Catalog.LDN => 5,
            Catalog.Melotte => 5,
            Catalog.Messier => 3,
            Catalog.Pl => 5,
            Catalog.PSR => 8,
            Catalog.NGC => 4,
            Catalog.RCW => 4,
            Catalog.Sharpless => 4,
            Catalog.TrES => 2,
            Catalog.UGC => 5,
            Catalog.vdB => 4,
            Catalog.WASP => 3,
            Catalog.WDS => 10,
            Catalog.XO => 4,
            Catalog.TwoMass => 15,
            Catalog.TwoMassX => 15,
            Catalog.Tycho2 => 10,
            _ => 0
        };
}