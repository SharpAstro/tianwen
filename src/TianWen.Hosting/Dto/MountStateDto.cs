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
        // Coerce NaN -> 0: before the session's first device poll MountState is all-NaN
        // ("unknown"), and System.Text.Json (no AllowNamedFloatingPointLiterals configured)
        // throws on NaN. 0 matches the pre-poll wire value clients saw before MountState was
        // NaN-initialised, so this is not a behaviour change for API consumers.
        RightAscension = NanToZero(state.RightAscension),
        Declination = NanToZero(state.Declination),
        HourAngle = NanToZero(state.HourAngle),
        PierSide = state.PierSide.ToString(),
        IsSlewing = state.IsSlewing,
        IsTracking = state.IsTracking,
    };

    private static double NanToZero(double v) => double.IsNaN(v) ? 0.0 : v;
}
