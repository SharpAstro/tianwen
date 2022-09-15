namespace Astap.Lib.Astrometry;

public enum Catalog : ulong
{
    Abell = 'A' << 16 | 'C' << 8 | 'O',
    Barnard = 'B',
    Caldwell = 'C',
    Collinder = 'C' << 8 | 'r',
    ESO = 'E',
    GJ = 'G' << 16 | 'J', // Gliese Jahreiß
    GUM = 'G' << 16 | 'U' << 8 | 'M',
    H = 'H', // Harvard open cluster catalog
    HD = 'H' << 8 | 'D', // Henry Draper
    HR = 'H' << 8 | 'R', // Havard revised bright star catalog
    HCG = 'H' << 16 | 'C' << 8 | 'G', // Hickson Compact Group
    IC = 'I',
    Melotte = 'M' << 16 | 'e' << 8 | 'l',
    Messier = 'M',
    NGC = 'N',
    Sharpless = 'S' << 24 | 'h' << 16 | '2' << 8 | '-',
    UGC = 'U' // Uppsala General Catalogue
}
