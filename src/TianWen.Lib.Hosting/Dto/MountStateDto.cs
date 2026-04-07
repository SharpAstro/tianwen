using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Hosting.Dto;

public sealed class MountStateDto
{
    public required double RightAscension { get; init; }
    public required double Declination { get; init; }
    public required double HourAngle { get; init; }
    public required string PierSide { get; init; }
    public required bool IsSlewing { get; init; }
    public required bool IsTracking { get; init; }

    public static MountStateDto FromState(MountState state) => new()
    {
        RightAscension = state.RightAscension,
        Declination = state.Declination,
        HourAngle = state.HourAngle,
        PierSide = state.PierSide.ToString(),
        IsSlewing = state.IsSlewing,
        IsTracking = state.IsTracking,
    };
}
