using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Astap.Lib.Astrometry
{
    record IAUNamedStarDTO(
        string IAUName,
        string Designation,
        string? ID,
        string Constellation,
        string? WDSComponentId,
        double? Vmag,
        double RA_J2000,
        double Dec_J2000,
        DateTime ApprovalDate
    );

    public record IAUNamedStar(string Name, double? Vmag, CatalogIndex Designation, ObjectType ObjectType, double RA, double Dec, Constellation Constellation)
        : CelestialObject(Designation, ObjectType, RA, Dec, Constellation);

    public class IAUNamedStarReader
    {
        public async Task ReadEmbeddedDataFileAsync()
        {
            var assembly = typeof(IAUNamedStarReader).Assembly;
            var namedStarsJsonFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith("iau-named-stars.json"));

            if (namedStarsJsonFileName is not null && assembly.GetManifestResourceStream(namedStarsJsonFileName) is Stream stream)
            {
                await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<IAUNamedStarDTO>(stream))
                {
                    if (record is not null)
                    {

                    }
                }
            }
        }
    }
}
