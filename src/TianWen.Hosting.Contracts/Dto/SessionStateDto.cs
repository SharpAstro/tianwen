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
    /// The session's outstanding user prompt, or null.
    /// <para>
    /// It rides on the polled state, not only on the <c>PROMPT-REQUESTED</c> push, because polling is
    /// the authoritative channel: a client that attaches after the prompt fired -- or whose socket
    /// dropped while it was open -- would otherwise have no way to learn a prompt is blocking the run,
    /// and no way to answer it.
    /// </para>
    /// </summary>
    public PendingPromptDto? PendingPrompt { get; init; }

    /// <summary>Cooling ramp, all cameras interleaved (each sample carries its <c>CameraIndex</c>).</summary>
    public required ImmutableArray<CoolingSampleDto> CoolingSamples { get; init; }

    /// <summary>Completed auto-focus runs, each with its full V-curve.</summary>
    public required ImmutableArray<FocusRunDto> FocusHistory { get; init; }

    /// <summary>
    /// In-progress V-curve samples, empty when not focusing. Separate from <see cref="FocusHistory"/>
    /// because a run only lands there once it completes -- without this a client watching an autofocus
    /// sees nothing until it finishes.
    /// </summary>
    public required ImmutableArray<FocusSampleDto> ActiveFocusSamples { get; init; }

    /// <summary>
    /// Every frame written this session.
    /// <para>
    /// This is the <b>backfill</b> for <c>FRAME-WRITTEN</c>, which by nature only announces frames
    /// written while the client was listening. A client attaching mid-night would otherwise show an
    /// empty frame list beside a non-zero <see cref="TotalFramesWritten"/>.
    /// </para>
    /// </summary>
    public required ImmutableArray<ExposureLogDto> ExposureLog { get; init; }

    /// <summary>
    /// Projects a session onto the wire. Takes <see cref="ISessionTelemetry"/>, not
    /// <see cref="ISession"/>: this reads observation state only, and typing it at the narrower
    /// interface is what lets the same projection run over a mirrored (remote) session, which in turn
    /// makes a DTO -> mirror -> DTO round-trip testable without a real run.
    /// </summary>
    /// <param name="pendingPrompt">Outstanding prompt, held by the host rather than the session itself
    /// (the session hands it out as an event and then awaits the answer), so the caller supplies it.</param>
    public static SessionStateDto FromSession(ISessionTelemetry session, PendingPromptDto? pendingPrompt = null)
    {
        var displays = session.TelescopeDisplays;
        var coolingSamples = session.CoolingSamples;
        var cameraStates = ImmutableArray.CreateBuilder<OtaCameraStateDto>(session.CameraStates.Length);
        for (var i = 0; i < session.CameraStates.Length; i++)
        {
            cameraStates.Add(OtaCameraStateDto.FromState(i, session.CameraStates[i],
                i < session.LastFrameMetrics.Length ? session.LastFrameMetrics[i] : default,
                // TelescopeDisplays is per-telescope and CameraStates is per-camera, one each, so the
                // indices line up -- but tolerate a short/default array rather than throwing on the
                // wire path (a session polled before its first display build would take the whole
                // response down).
                !displays.IsDefaultOrEmpty && i < displays.Length ? displays[i] : default,
                NewestCoolingFor(coolingSamples, i)));
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

        var cooling = ImmutableArray.CreateBuilder<CoolingSampleDto>(coolingSamples.IsDefaultOrEmpty ? 0 : coolingSamples.Length);
        if (!coolingSamples.IsDefaultOrEmpty)
        {
            foreach (var cs in coolingSamples)
            {
                cooling.Add(new CoolingSampleDto
                {
                    Timestamp = cs.Timestamp,
                    CameraIndex = cs.CameraIndex,
                    TemperatureC = JsonNumber.ForWire(cs.TemperatureC),
                    SetpointTemperatureC = JsonNumber.ForWire(cs.SetpointTempC),
                    CoolerPowerPercent = JsonNumber.ForWire(cs.CoolerPowerPercent),
                });
            }
        }

        var focusHistory = session.FocusHistory;
        var focusRuns = ImmutableArray.CreateBuilder<FocusRunDto>(focusHistory.IsDefaultOrEmpty ? 0 : focusHistory.Length);
        if (!focusHistory.IsDefaultOrEmpty)
        {
            foreach (var run in focusHistory)
            {
                focusRuns.Add(FocusRunDto.FromRecord(run));
            }
        }

        var activeSamples = session.ActiveFocusSamples;
        var active = ImmutableArray.CreateBuilder<FocusSampleDto>(activeSamples.IsDefaultOrEmpty ? 0 : activeSamples.Length);
        if (!activeSamples.IsDefaultOrEmpty)
        {
            foreach (var (position, hfd) in activeSamples)
            {
                active.Add(new FocusSampleDto { Position = position, Hfd = JsonNumber.ForWire(hfd) });
            }
        }

        var log = session.ExposureLog;
        var exposures = ImmutableArray.CreateBuilder<ExposureLogDto>(log.IsDefaultOrEmpty ? 0 : log.Length);
        if (!log.IsDefaultOrEmpty)
        {
            foreach (var entry in log)
            {
                exposures.Add(new ExposureLogDto
                {
                    Timestamp = entry.Timestamp,
                    TargetName = entry.TargetName,
                    FilterName = entry.FilterName,
                    ExposureSeconds = entry.Exposure.TotalSeconds,
                    FrameNumber = entry.FrameNumber,
                    MedianHfd = JsonNumber.ForWire(entry.MedianHfd),
                    StarCount = entry.StarCount,
                });
            }
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
            PendingPrompt = pendingPrompt,
            CoolingSamples = cooling.MoveToImmutable(),
            FocusHistory = focusRuns.MoveToImmutable(),
            ActiveFocusSamples = active.MoveToImmutable(),
            ExposureLog = exposures.MoveToImmutable(),
        };
    }

    /// <summary>
    /// Newest cooling sample for a camera, or null. The ramp is append-ordered and interleaves cameras,
    /// so this walks backwards to the first matching index rather than filtering the whole array.
    /// </summary>
    private static CoolingSample? NewestCoolingFor(ImmutableArray<CoolingSample> samples, int cameraIndex)
    {
        if (samples.IsDefaultOrEmpty)
        {
            return null;
        }

        for (var i = samples.Length - 1; i >= 0; i--)
        {
            if (samples[i].CameraIndex == cameraIndex)
            {
                return samples[i];
            }
        }

        return null;
    }
}

public sealed class CoolingSampleDto
{
    public required DateTimeOffset Timestamp { get; init; }
    public required int CameraIndex { get; init; }
    public required double TemperatureC { get; init; }
    public required double SetpointTemperatureC { get; init; }
    public required double CoolerPowerPercent { get; init; }
}

/// <summary>One completed auto-focus run, including the V-curve it fitted.</summary>
public sealed class FocusRunDto
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string OtaName { get; init; }
    public required string FilterName { get; init; }
    public required int BestPosition { get; init; }
    public required float BestHfd { get; init; }
    public required ImmutableArray<FocusSampleDto> Curve { get; init; }

    /// <summary>Hyperbola fit coefficients; NaN when the run recorded no fit.</summary>
    public required double FitA { get; init; }

    /// <inheritdoc cref="FitA"/>
    public required double FitB { get; init; }

    public static FocusRunDto FromRecord(FocusRunRecord record)
    {
        var curve = ImmutableArray.CreateBuilder<FocusSampleDto>(record.Curve.IsDefaultOrEmpty ? 0 : record.Curve.Length);
        if (!record.Curve.IsDefaultOrEmpty)
        {
            foreach (var (position, hfd) in record.Curve)
            {
                curve.Add(new FocusSampleDto { Position = position, Hfd = JsonNumber.ForWire(hfd) });
            }
        }

        return new FocusRunDto
        {
            Timestamp = record.Timestamp,
            OtaName = record.OtaName,
            FilterName = record.FilterName,
            BestPosition = record.BestPosition,
            BestHfd = JsonNumber.ForWire(record.BestHfd),
            Curve = curve.MoveToImmutable(),
            FitA = JsonNumber.ForWire(record.FitA),
            FitB = JsonNumber.ForWire(record.FitB),
        };
    }
}

public sealed class FocusSampleDto
{
    public required int Position { get; init; }
    public required float Hfd { get; init; }
}

public sealed class ExposureLogDto
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string TargetName { get; init; }
    public required string FilterName { get; init; }
    public required double ExposureSeconds { get; init; }
    public required int FrameNumber { get; init; }
    public required float MedianHfd { get; init; }
    public required int StarCount { get; init; }
}

/// <summary>
/// A user prompt the session is blocked on (e.g. "switch on the manual flat panel"), with the labels
/// the node wants shown on the two answers.
/// </summary>
public sealed class PendingPromptDto
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required string ContinueLabel { get; init; }
    public required string CancelLabel { get; init; }

    /// <summary>
    /// <c>true</c> when answering requires somebody <b>at the rig</b> (switching on a hand-switched
    /// panel, capping the scope).
    /// <para>
    /// This matters far more remotely than locally, which is why it crosses the wire. A client mirroring
    /// an observatory from somewhere else cannot perform the action, so offering a bare "Continue" invites
    /// the operator to assert a physical fact they cannot see -- the same fabrication as the node
    /// answering by itself, merely performed by a human. A remote UI should warn plainly, and it must not
    /// have to pattern-match the message text to know when. Answering is still permitted: the operator may
    /// be on the phone with someone at the scope, or the panel may be on a smart plug they just toggled.
    /// </para>
    /// </summary>
    public required bool RequiresPhysicalPresence { get; init; }

    /// <summary>
    /// When the node raised this prompt, or <see langword="null"/> from a node too old to send it.
    /// <para>
    /// A prompt holds the run open indefinitely with no timer, so its <i>age</i> is the fact a client
    /// needs -- an unanswered prompt is only visibly a problem once you can see it has been waiting 40
    /// minutes. A client cannot derive this: the moment it first polled says when the client started, not
    /// when the rig stopped.
    /// </para>
    /// <para>
    /// Deliberately not <c>required</c>: it is nullable, and a nullable wire property that is also
    /// required cannot round-trip (the serializer omits it when null, and deserialization then fails on
    /// the missing member).
    /// </para>
    /// </summary>
    public DateTimeOffset? RaisedUtc { get; init; }
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
