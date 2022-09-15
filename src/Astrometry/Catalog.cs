namespace Astap.Lib.Astrometry;

public enum Catalog : ulong
{
    Abell = 'A' << 14 | 'C' << 7 | 'O',
    Barnard = 'B',
    Caldwell = 'C',
    Collinder = 'C' << 7 | 'r', // Cl is normalised to Cr
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
    Melotte = 'M' << 14 | 'e' << 7 | 'l',
    Messier = 'M',
    NGC = 'N',
    PSR = 'P' << 14 | 'S' << 7 | 'R', // PSR
    Sharpless = 'S' << 21 | 'h' << 14 | '2' << 7 | '-', // Sharpless, including dash to distinguish 2 in the name from a number
    UGC = 'U', // Uppsala General Catalogue
    WASP = 'W' << 21 | 'A' << 14 | 'S' << 7 | 'P',
    XO = 'X' << 7 | 'O'
}
