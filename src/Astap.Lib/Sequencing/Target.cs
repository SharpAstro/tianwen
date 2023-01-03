namespace Astap.Lib.Sequencing;

using Astap.Lib.Astrometry.Catalogs;
using System.Globalization;
using static Astap.Lib.Astrometry.CoordinateUtils;

public record Target(double RA, double Dec, string Name, CatalogIndex? CatalogIndex)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, stackalloc char[64], $"({Name}; {HoursToHMS(RA)}, {DegreesToDMS(Dec)})");
}
