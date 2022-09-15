namespace Astap.Lib.Astrometry;

public enum Catalog : ulong
{
    Abell = 'A' << 14 | 'C' << 7 | 'O',
    Barnard = 'B',
    Caldwell = 'C',
    Collinder = 'C' << 7 | 'r',
    ESO = 'E',
    GJ = 'G' << 14 | 'J', // Gliese Jahreiß
    GUM = 'G' << 14 | 'U' << 7 | 'M',
    H = 'H', // Harvard open cluster catalog
    HD = 'H' << 7 | 'D', // Henry Draper
    HR = 'H' << 7 | 'R', // Havard revised bright star catalog
    HCG = 'H' << 14 | 'C' << 7 | 'G', // Hickson Compact Group
    IC = 'I',
    Melotte = 'M' << 14 | 'e' << 7 | 'l',
    Messier = 'M',
    NGC = 'N',
    Sharpless = 'S' << 21 | 'h' << 14 | '2' << 7 | '-',
    UGC = 'U' // Uppsala General Catalogue
}
