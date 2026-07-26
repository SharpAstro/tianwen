using System;
using System.Collections.Immutable;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

/// <summary>
/// JSON-serializable projection of the live session state.
/// Excludes binary data (images) and device handles — those are served via separate endpoints.
/// <para>
/// <b>Never mark a nullable wire property <c>required</c>.</b> <see cref="HostingJsonContext"/>
/// serializes with <c>DefaultIgnoreCondition = WhenWritingNull</c>, so a null field is <i>omitted</i>
/// from the JSON -- while C# <c>required</c> makes System.Text.Json <i>demand</i> the property on read.
/// The two together make the server's own output undeserializable: the moment anything actually reads a
/// response (a mirroring client, a round-trip test) every payload with an unset optional throws
/// "missing required properties". This bit exactly once, on <c>FailureReason</c> of an ordinary healthy
/// session, and stayed invisible for as long as the server only ever wrote. <c>required</c> is right for
/// non-nullable fields (it keeps the projection below honest); optional means optional on the wire too.
/// </para>
/// </summary>
public sealed class SessionStateDto
{
    public required SessionPhase Phase { get; init; }
    public string? CurrentActivity { get; init; }
    /// <summary>User-facing reason when <see cref="Phase"/> is Failed (which device / what to check); null otherwise.</summary>
    public string? FailureReason { get; init; }
    public required int TotalFramesWritten { get; init; }
    public required double TotalExposureTimeSeconds { get; init; }
    public required int CurrentObservationIndex { get; init; }
    public string? ActiveTargetName { get; init; }
    public string? LastFramePath { get; init; }

    public MountStateDto? Mount { get; init; }

    /// <summary>Mount display label (<see cref="ISessionTelemetry.MountDisplayName"/>). Travels with the
    /// pointing it describes, so a mirror's mount panel reads the same as a local one.</summary>
    public required string MountDisplayName { get; init; }

    public GuiderStateDto? Guider { get; init; }
    public required ImmutableArray<OtaCameraStateDto> Cameras { get; init; }
    public required ImmutableArray<ObservationDto> Observations { get; init; }
    public required ImmutableArray<PhaseTimestampDto> PhaseTimeline { get; init; }

    /// <summary>
    /// Projects a session onto the wire. Takes <see cref="ISessionTelemetry"/>, not
    /// <see cref="ISession"/>: this reads observation state only, and typing it at the narrower
    /// interface is what lets the same projection run over a mirrored (remote) session, which in turn
    /// makes a DTO -> mirror -> DTO round-trip testable without a real run.
    /// </summary>
    public static SessionStateDto FromSession(ISessionTelemetry session)
    {
        var displays = session.TelescopeDisplays;
        var cameraStates = ImmutableArray.CreateBuilder<OtaCameraStateDto>(session.CameraStates.Length);
        for (var i = 0; i < session.CameraStates.Length; i++)
        {
            cameraStates.Add(OtaCameraStateDto.FromState(i, session.CameraStates[i],
                i < session.LastFrameMetrics.Length ? session.LastFrameMetrics[i] : default,
                // TelescopeDisplays is per-telescope and CameraStates is per-camera, one each, so the
                // indices line up -- but tolerate a short/default array rather than throwing on the
                // wire path (a session polled before its first display build would take the whole
                // response down).
                !displays.IsDefaultOrEmpty && i < displays.Length ? displays[i] : default));
        }

        var observations = ImmutableArray.CreateBuilder<ObservationDto>();
        if (session.Observations is { } obs)
        {
            for (var i = 0; i < obs.Count; i++)
            {
                observations.Add(ObservationDto.FromScheduled(obs[i]));
            }
        }

        var timeline = ImmutableArray.CreateBuilder<PhaseTimestampDto>(session.PhaseTimeline.Length);
        foreach (var pt in session.PhaseTimeline)
        {
            timeline.Add(new PhaseTimestampDto { Phase = pt.Phase, StartTime = pt.StartTime });
        }

        return new SessionStateDto
        {
            Phase = session.Phase,
            CurrentActivity = session.CurrentActivity,
            FailureReason = session.FailureReason,
            TotalFramesWritten = session.TotalFramesWritten,
            TotalExposureTimeSeconds = session.TotalExposureTime.TotalSeconds,
            CurrentObservationIndex = session.CurrentObservationIndex,
            ActiveTargetName = session.ActiveObservation?.Target.Name,
            LastFramePath = session.LastFramePath,
            Mount = MountStateDto.FromState(session.MountState),
            MountDisplayName = session.MountDisplayName,
            Guider = GuiderStateDto.FromSession(session),
            Cameras = cameraStates.MoveToImmutable(),
            Observations = observations.ToImmutable(),
            PhaseTimeline = timeline.MoveToImmutable(),
        };
    }
}

public sealed class PhaseTimestampDto
{
    public required SessionPhase Phase { get; init; }
    public required DateTimeOffset StartTime { get; init; }
}

public sealed class ObservationDto
{
    public required string TargetName { get; init; }
    public required double TargetRA { get; init; }
    public required double TargetDec { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required double DurationMinutes { get; init; }
    public required bool AcrossMeridian { get; init; }

    public static ObservationDto FromScheduled(ScheduledObservation obs) => new()
    {
        TargetName = obs.Target.Name,
        // A synthesized target (name known, coordinates not) carries NaN.
        TargetRA = JsonNumber.ForWire(obs.Target.RA),
        TargetDec = JsonNumber.ForWire(obs.Target.Dec),
        Start = obs.Start,
        DurationMinutes = obs.Duration.TotalMinutes,
        AcrossMeridian = obs.AcrossMeridian,
    };
}
