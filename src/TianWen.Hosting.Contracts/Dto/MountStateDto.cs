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
        RightAscension = JsonNumber.ForWire(state.RightAscension),
        Declination = JsonNumber.ForWire(state.Declination),
        HourAngle = JsonNumber.ForWire(state.HourAngle),
        PierSide = state.PierSide.ToString(),
        IsSlewing = state.IsSlewing,
        IsTracking = state.IsTracking,
    };
}

/// <summary>
/// Wire form of <see cref="MountLimitVerdict"/>. The enums travel as numbers like every other enum on this
/// contract; <see cref="ExceededBy"/> goes through <see cref="JsonNumber.ForWire"/> because a non-finite
/// double is a bodiless 500 for the WHOLE state endpoint.
/// </summary>
public sealed class MountLimitDto
{
    public required MountLimitKind Kind { get; init; }
    public required MountLimitResponse Response { get; init; }
    public required double ExceededBy { get; init; }
    public required MountLimitBasis Basis { get; init; }
    /// <summary>Not <c>required</c>: an older node never writes it, and "not latched" is the right reading of its absence.</summary>
    public bool Latched { get; init; }

    public static MountLimitDto FromVerdict(MountLimitVerdict verdict) => new()
    {
        Kind = verdict.Kind,
        Response = verdict.Response,
        ExceededBy = JsonNumber.ForWire(verdict.ExceededBy),
        Basis = verdict.Basis,
        Latched = verdict.Latched,
    };

    public MountLimitVerdict ToVerdict() => new(Kind, Response, ExceededBy, Basis, Latched);
}
