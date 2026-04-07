using System;
using System.Collections.Immutable;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Hosting.Dto;

/// <summary>
/// JSON-serializable projection of the live session state.
/// Excludes binary data (images) and device handles — those are served via separate endpoints.
/// </summary>
public sealed class SessionStateDto
{
    public required SessionPhase Phase { get; init; }
    public required string? CurrentActivity { get; init; }
    public required int TotalFramesWritten { get; init; }
    public required double TotalExposureTimeSeconds { get; init; }
    public required int CurrentObservationIndex { get; init; }
    public required string? ActiveTargetName { get; init; }
    public required string? LastFramePath { get; init; }

    public required MountStateDto? Mount { get; init; }
    public required GuiderStateDto? Guider { get; init; }
    public required ImmutableArray<OtaCameraStateDto> Cameras { get; init; }
    public required ImmutableArray<ObservationDto> Observations { get; init; }
    public required ImmutableArray<PhaseTimestampDto> PhaseTimeline { get; init; }

    public static SessionStateDto FromSession(ISession session)
    {
        var cameraStates = ImmutableArray.CreateBuilder<OtaCameraStateDto>(session.CameraStates.Length);
        for (var i = 0; i < session.CameraStates.Length; i++)
        {
            cameraStates.Add(OtaCameraStateDto.FromState(i, session.CameraStates[i],
                i < session.LastFrameMetrics.Length ? session.LastFrameMetrics[i] : default));
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
            TotalFramesWritten = session.TotalFramesWritten,
            TotalExposureTimeSeconds = session.TotalExposureTime.TotalSeconds,
            CurrentObservationIndex = session.CurrentObservationIndex,
            ActiveTargetName = session.ActiveObservation?.Target.Name,
            LastFramePath = session.LastFramePath,
            Mount = MountStateDto.FromState(session.MountState),
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
        TargetRA = obs.Target.RA,
        TargetDec = obs.Target.Dec,
        Start = obs.Start,
        DurationMinutes = obs.Duration.TotalMinutes,
        AcrossMeridian = obs.AcrossMeridian,
    };
}
