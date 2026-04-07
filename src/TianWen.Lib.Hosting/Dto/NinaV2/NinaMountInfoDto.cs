using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Hosting.Dto.NinaV2;

/// <summary>
/// Mount info DTO matching ninaAPI v2 <c>/v2/api/equipment/mount/info</c> response shape.
/// </summary>
public sealed class NinaMountInfoDto
{
    public required bool Connected { get; init; }
    public required string Name { get; init; }
    public required double RightAscension { get; init; }
    public required double Declination { get; init; }
    public required string SideOfPier { get; init; }
    public required bool Tracking { get; init; }
    public required string TrackingMode { get; init; }
    public required bool Slewing { get; init; }
    public required bool AtPark { get; init; }
    public required bool AtHome { get; init; }

    public static async Task<NinaMountInfoDto> FromDriverAsync(IMountDriver driver, MountState polledState, CancellationToken ct)
    {
        return new NinaMountInfoDto
        {
            Connected = driver.Connected,
            Name = driver.Name,
            RightAscension = polledState.RightAscension,
            Declination = polledState.Declination,
            SideOfPier = polledState.PierSide.ToString(),
            Tracking = polledState.IsTracking,
            TrackingMode = (await driver.GetTrackingSpeedAsync(ct)).ToString(),
            Slewing = polledState.IsSlewing,
            AtPark = await driver.AtParkAsync(ct),
            AtHome = await driver.AtHomeAsync(ct),
        };
    }

    public static NinaMountInfoDto Disconnected { get; } = new NinaMountInfoDto
    {
        Connected = false, Name = "", RightAscension = 0, Declination = 0,
        SideOfPier = "Unknown", Tracking = false, TrackingMode = "None",
        Slewing = false, AtPark = false, AtHome = false,
    };
}
