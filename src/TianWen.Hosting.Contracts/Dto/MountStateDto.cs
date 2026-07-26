using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

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
        // Before the session's first device poll MountState is all-NaN ("unknown"), which JSON
        // cannot represent -- see JsonNumber for why an unguarded one 500s the whole response.
        RightAscension = JsonNumber.Finite(state.RightAscension),
        Declination = JsonNumber.Finite(state.Declination),
        HourAngle = JsonNumber.Finite(state.HourAngle),
        PierSide = state.PierSide.ToString(),
        IsSlewing = state.IsSlewing,
        IsTracking = state.IsTracking,
    };
}
