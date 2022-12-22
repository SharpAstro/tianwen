namespace Astap.Lib.Sequencing;

using System.Globalization;
using static Astap.Lib.Astrometry.CoordinateUtils;

public record Target(double RA, double Dec, string Name)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, stackalloc char[64], $"({Name}; {HoursToHMS(RA)}, {DegreesToDMS(Dec)})");
}
