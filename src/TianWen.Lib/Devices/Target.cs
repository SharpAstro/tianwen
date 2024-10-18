namespace TianWen.Lib.Devices;

using TianWen.Lib.Astrometry.Catalogs;
using System.Globalization;
using static TianWen.Lib.Astrometry.CoordinateUtils;

public record Target(double RA, double Dec, string Name, CatalogIndex? CatalogIndex)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, stackalloc char[64], $"({Name}; {HoursToHMS(RA)}, {DegreesToDMS(Dec)})");
}
