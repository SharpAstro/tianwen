using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

public sealed class MountStateDto
{
    public double? RightAscension { get; init; }
    public double? Declination { get; init; }
    public double? HourAngle { get; init; }
    public required string PierSide { get; init; }
    public required bool IsSlewing { get; init; }
    public required bool IsTracking { get; init; }

    /// <summary>
    /// The pointing in J2000, which the sky map draws the reticle in (the native epoch is about 22' off it), the
    /// altitude, and the mechanical RA-axis angle a mount that has one reports (P5b part 3). Null where not known.
    /// </summary>
    public double? RaJ2000 { get; init; }

    /// <inheritdoc cref="RaJ2000"/>
    public double? DecJ2000 { get; init; }

    /// <inheritdoc cref="RaJ2000"/>
    public double? Altitude { get; init; }

    /// <inheritdoc cref="RaJ2000"/>
    public double? PrimaryAxisAngleDeg { get; init; }

    public static MountStateDto FromState(MountState state) => new()
    {
        RaJ2000 = JsonNumber.OrNull(state.RaJ2000),
        DecJ2000 = JsonNumber.OrNull(state.DecJ2000),
        Altitude = JsonNumber.OrNull(state.Altitude),
        PrimaryAxisAngleDeg = JsonNumber.OrNull(state.PrimaryAxisAngleDeg),
        // Before the session's first device poll MountState is all-NaN ("unknown"), which crosses as null: as 0 it
        // read as a mount pointing at RA 0, Dec 0, and a mirror snapped the reticle there.
        RightAscension = JsonNumber.OrNull(state.RightAscension),
        Declination = JsonNumber.OrNull(state.Declination),
        HourAngle = JsonNumber.OrNull(state.HourAngle),
        PierSide = state.PierSide.ToString(),
        IsSlewing = state.IsSlewing,
        IsTracking = state.IsTracking,
    };
}

/// <summary>
/// Wire form of <see cref="MountLimitVerdict"/>. The enums travel as numbers like every other enum on this
/// contract; <see cref="ExceededBy"/> is null when it is not known (NaN), since a non-finite double is a
/// bodiless 500 for the WHOLE state endpoint and 0 would read as a real margin.
/// </summary>
public sealed class MountLimitDto
{
    public required MountLimitKind Kind { get; init; }
    public required MountLimitResponse Response { get; init; }
    public double? ExceededBy { get; init; }
    public required MountLimitBasis Basis { get; init; }
    /// <summary>Not <c>required</c>: an older node never writes it, and "not latched" is the right reading of its absence.</summary>
    public bool Latched { get; init; }

    public static MountLimitDto FromVerdict(MountLimitVerdict verdict) => new()
    {
        Kind = verdict.Kind,
        Response = verdict.Response,
        ExceededBy = JsonNumber.OrNull(verdict.ExceededBy),
        Basis = verdict.Basis,
        Latched = verdict.Latched,
    };

    public MountLimitVerdict ToVerdict() => new(Kind, Response, JsonNumber.FromWire(ExceededBy), Basis, Latched);
}
