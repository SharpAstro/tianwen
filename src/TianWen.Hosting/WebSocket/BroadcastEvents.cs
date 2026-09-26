using System.Collections.Generic;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Sequencing;
using GuiderStateChangedEventArgs = TianWen.Lib.Sequencing.GuiderStateChangedEventArgs;

namespace TianWen.Hosting.WebSocket;

/// <summary>
/// Every event the node broadcasts, built in one place so a test can serialise each exactly as it is sent.
/// </summary>
/// <remarks>
/// A payload is a <c>Dictionary&lt;string, object?&gt;</c>, so the source-generated serialiser must hold
/// metadata for the RUNTIME type of every value, and nothing checks that until a value is sent.
/// <c>SCOUT-COMPLETED</c> carried an <c>int[]</c> that neither JSON context knew, so it never went out at all
/// (P0b item 16 of docs/plans/hardware-in-the-server.md, #752). <c>BroadcastEventSerializationTests</c> sends
/// every factory here through both contexts: an event added to the broadcaster is added here and there.
/// </remarks>
internal static class BroadcastEvents
{
    public static WebSocketEventDto GuideStep(GuideErrorSample sample) => new WebSocketEventDto
    {
        Event = "GUIDE-STEP",
        Data = new Dictionary<string, object?>
        {
            ["Timestamp"] = sample.Timestamp,
            ["RaError"] = JsonNumber.ForWire(sample.RaError),
            ["DecError"] = JsonNumber.ForWire(sample.DecError),
            ["RaCorrectionMs"] = JsonNumber.ForWire(sample.RaCorrectionMs),
            ["DecCorrectionMs"] = JsonNumber.ForWire(sample.DecCorrectionMs),
            ["IsDither"] = sample.IsDither,
            ["IsSettling"] = sample.IsSettling
        }
    };

    public static WebSocketEventDto EnhanceProgress(EnhanceProgress e)
    {
        var overall = e.StepCount > 0
            ? (e.StepIndex + System.Math.Clamp(e.StepPercent, 0f, 1f)) / e.StepCount * 100f
            : 0f;
        return new WebSocketEventDto
        {
            Event = "ENHANCE-PROGRESS",
            Data = new Dictionary<string, object?>
            {
                ["StepName"] = e.StepName,
                ["StepIndex"] = e.StepIndex,
                ["StepCount"] = e.StepCount,
                ["StepPercent"] = e.StepPercent,
                ["Percent"] = overall,
                ["EtaSeconds"] = e.EtaSeconds
            }
        };
    }

    public static WebSocketEventDto EnhanceCompleted(EnhanceJobCompletedEventArgs e) => new WebSocketEventDto
    {
        Event = "ENHANCE-COMPLETED",
        Data = new Dictionary<string, object?>
        {
            ["InputPath"] = e.InputPath,
            ["OutputPath"] = e.OutputPath,
            ["Succeeded"] = e.Succeeded,
            ["Error"] = e.Error
        }
    };

    /// <summary>
    /// A device's state changed: read differently, connected or let go, or taken or released by a run. The latency
    /// hint: <c>GET /api/v1/devices/state</c> is authoritative (P2 part 1, #929).
    /// </summary>
    public static WebSocketEventDto DeviceState(DeviceStateDto device) => new WebSocketEventDto
    {
        Event = Api.NodeWire.DeviceStateEvent,
        Data = new Dictionary<string, object?>
        {
            [DeviceStateDto.EventKey] = device
        }
    };

    /// <summary>A job started, moved on or ended. The latency hint: <c>GET /api/v1/jobs/{id}</c> is authoritative.</summary>
    public static WebSocketEventDto JobProgress(JobDto job) => new WebSocketEventDto
    {
        Event = "JOB-PROGRESS",
        Data = new Dictionary<string, object?>
        {
            ["Id"] = job.Id,
            ["Kind"] = job.Kind,
            ["DeviceUri"] = job.DeviceUri,
            ["State"] = job.State.ToString(),
            ["Step"] = job.Step,
            ["Error"] = job.Error
        }
    };

    public static WebSocketEventDto PhaseChanged(SessionPhaseChangedEventArgs e) => new WebSocketEventDto
    {
        Event = "SESSION-PHASE-CHANGED",
        Data = new Dictionary<string, object?>
        {
            ["OldPhase"] = e.OldPhase.ToString(),
            ["NewPhase"] = e.NewPhase.ToString()
        }
    };

    public static WebSocketEventDto FrameWritten(ExposureLogEntry entry) => new WebSocketEventDto
    {
        Event = "FRAME-WRITTEN",
        Data = new Dictionary<string, object?>
        {
            ["TargetName"] = entry.TargetName,
            ["FilterName"] = entry.FilterName,
            ["ExposureSeconds"] = entry.Exposure.TotalSeconds,
            ["FrameNumber"] = entry.FrameNumber,
            ["MedianHfd"] = JsonNumber.ForWire(entry.MedianHfd),
            ["StarCount"] = entry.StarCount
        }
    };

    public static WebSocketEventDto PlateSolveCompleted(PlateSolveRecord record) => new WebSocketEventDto
    {
        Event = "PLATE-SOLVE-COMPLETED",
        Data = new Dictionary<string, object?>
        {
            ["Context"] = record.Context.ToString(),
            ["OtaName"] = record.OtaName,
            ["Succeeded"] = record.Succeeded,
            ["SolvedRA"] = record.Solution?.CenterRA,
            ["SolvedDec"] = record.Solution?.CenterDec,
            ["ElapsedMs"] = record.Elapsed.TotalMilliseconds,
            ["DetectedStars"] = record.DetectedStars,
            ["MatchedStars"] = record.MatchedStars
        }
    };

    public static WebSocketEventDto ScoutCompleted(ScoutCompletedEventArgs e) => new WebSocketEventDto
    {
        Event = "SCOUT-COMPLETED",
        Data = new Dictionary<string, object?>
        {
            ["TargetName"] = e.Target.Name,
            ["Classification"] = e.Classification.ToString(),
            ["Outcome"] = e.Outcome.ToString(),
            ["EstimatedClearInSeconds"] = e.EstimatedClearIn?.TotalSeconds,
            ["StarCountsPerOTA"] = e.StarCountsPerOTA
        }
    };

    public static WebSocketEventDto GuiderStateChanged(GuiderStateChangedEventArgs e) => new WebSocketEventDto
    {
        Event = "GUIDER-STATE-CHANGED",
        Data = new Dictionary<string, object?>
        {
            ["OldState"] = e.OldState,
            ["NewState"] = e.NewState
        }
    };

    public static WebSocketEventDto PromptRequested(SessionPromptEventArgs e) => new WebSocketEventDto
    {
        Event = "PROMPT-REQUESTED",
        Data = new Dictionary<string, object?>
        {
            ["Title"] = e.Title,
            ["Message"] = e.Message,
            ["ContinueLabel"] = e.ContinueLabel,
            ["CancelLabel"] = e.CancelLabel,
            ["RequiresPhysicalPresence"] = e.RequiresPhysicalPresence,
            // Carried here as well as on /session/state so the two paths agree. A client that learns
            // of a prompt from the broadcast would otherwise know less about it than one that polled,
            // for no reason -- and the age is the part worth knowing. Null when the session did not
            // stamp it; a boxed DateTimeOffset already crosses on this dictionary (GUIDE-STEP).
            ["RaisedUtc"] = e.RaisedUtc
        }
    };

    public static WebSocketEventDto Notification(NotificationDto dto) => new WebSocketEventDto
    {
        Event = "NOTIFICATION",
        Data = new Dictionary<string, object?>
        {
            ["Severity"] = dto.Severity,
            ["Message"] = dto.Message,
            ["TimestampUtc"] = dto.TimestampUtc
        }
    };
}
