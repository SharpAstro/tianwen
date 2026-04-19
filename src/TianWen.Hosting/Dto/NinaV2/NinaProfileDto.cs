using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto.NinaV2;

/// <summary>
/// Active profile DTO matching ninaAPI v2 <c>/v2/api/profile/show?active=true</c>.
/// Only includes fields that TNS actually reads.
/// </summary>
public sealed class NinaProfileDto
{
    public required NinaAstrometrySettingsDto AstrometrySettings { get; init; }

    public static async Task<NinaProfileDto> FromSessionAsync(ISession session, CancellationToken ct)
    {
        var mount = session.Setup.Mount.Driver;
        double latitude = 0, longitude = 0, elevation = 0;
        if (mount.Connected)
        {
            latitude = await mount.GetSiteLatitudeAsync(ct);
            longitude = await mount.GetSiteLongitudeAsync(ct);
            elevation = await mount.GetSiteElevationAsync(ct);
        }

        return new NinaProfileDto
        {
            AstrometrySettings = new NinaAstrometrySettingsDto
            {
                Latitude = latitude,
                Longitude = longitude,
                Elevation = elevation,
            },
        };
    }
}

public sealed class NinaAstrometrySettingsDto
{
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required double Elevation { get; init; }
}
