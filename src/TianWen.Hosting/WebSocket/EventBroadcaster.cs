using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Sequencing;
using GuiderStateChangedEventArgs = TianWen.Lib.Sequencing.GuiderStateChangedEventArgs;

namespace TianWen.Hosting.WebSocket;

/// <summary>
/// Background service that subscribes to <see cref="ISession"/> events (and the
/// <see cref="HostedImageEnhancer"/> job) and broadcasts them to all connected WebSocket clients
/// via <see cref="EventHub"/>.
/// <para>
/// It is also the node's <b>notification recorder</b>: it is the one component already watching every
/// session event, so it writes the same transitions it broadcasts into
/// <see cref="IHostedSession.Notifications"/>. That gives a remote client the feed a local GUI builds
/// from its own signal bus, including for the stretch of the night before the client attached.
/// </para>
/// </summary>
internal sealed class EventBroadcaster(
    HostedSession hostedSession,
    HostedImageEnhancer imageEnhancer,
    EventHub eventHub,
    ITimeProvider timeProvider,
    ILogger<EventBroadcaster> logger
) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private ISession? _subscribedSession;

    /// <summary>
    /// Timestamp of the newest guide sample already pushed, so the poll below emits only new ones.
    /// Guide samples are appended by the guide loop and read here; a timestamp watermark is enough to
    /// diff them without holding a reference to the previous snapshot.
    /// </summary>
    private DateTimeOffset _lastGuideStepPushed = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EventBroadcaster started, waiting for session");

        // The enhancer is a process-lifetime singleton, so subscribe once up front (unlike the
        // session, which comes and goes and is (un)subscribed inside the loop below).
        imageEnhancer.Progressed += OnEnhanceProgress;
        imageEnhancer.Completed += OnEnhanceCompleted;

        while (!stoppingToken.IsCancellationRequested)
        {
            var session = hostedSession.CurrentSession;

            // Subscribe to new session events
            if (session is not null && !ReferenceEquals(session, _subscribedSession))
            {
                if (_subscribedSession is not null)
                {
                    UnsubscribeFromSession(_subscribedSession);
                }
                SubscribeToSession(session);
                _subscribedSession = session;
                // Start the watermark at whatever the ring already holds rather than at MinValue, so
                // attaching does not re-broadcast the existing ~5 minute window one event at a time.
                // Backfill is the snapshot's job (the state DTO carries the ring); the broadcast exists
                // only to announce what is new -- the same division as the exposure log.
                _lastGuideStepPushed = NewestGuideSampleTime(session);
                logger.LogInformation("EventBroadcaster subscribed to session");
            }
            else if (session is null && _subscribedSession is not null)
            {
                UnsubscribeFromSession(_subscribedSession);
                _subscribedSession = null;
            }

            if (session is not null)
            {
                PushNewGuideSteps(session);
            }

            // Liveness bound on an outstanding prompt: see OnPromptRequested for why this replaces a
            // timeout rather than supplementing one.
            ResolveOrphanedPrompt();

            try
            {
                // ITimeProvider, not Task.Delay: the project rule is that every wait resolves the clock
                // from DI so a test can drive it (a FakeTimeProvider hangs forever on a raw Task.Delay).
                await timeProvider.SleepAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        imageEnhancer.Progressed -= OnEnhanceProgress;
        imageEnhancer.Completed -= OnEnhanceCompleted;

        if (_subscribedSession is not null)
        {
            UnsubscribeFromSession(_subscribedSession);
        }
    }

    /// <summary>
    /// Newest guide-sample timestamp currently in the session's ring, or <see cref="DateTimeOffset.MinValue"/>
    /// when it is empty. The ring is oldest-first, so this is the last element.
    /// </summary>
    private static DateTimeOffset NewestGuideSampleTime(ISessionTelemetry session)
    {
        var samples = session.GuideSamples;
        return samples.IsDefaultOrEmpty ? DateTimeOffset.MinValue : samples[^1].Timestamp;
    }

    /// <summary>
    /// Emits a <c>GUIDE-STEP</c> per newly-arrived guide sample.
    /// <para>
    /// Without this a client wanting live guide errors has to re-pull the whole ~5 minute sample ring
    /// out of <c>/session/state</c> every second and diff it locally -- tens of samples re-sent per
    /// poll to learn about the one that changed. Pushing the delta means the state payload's guider
    /// block can eventually shrink to stats-only.
    /// </para>
    /// </summary>
    private void PushNewGuideSteps(ISessionTelemetry session)
    {
        var samples = session.GuideSamples;
        if (samples.IsDefaultOrEmpty)
        {
            return;
        }

        var newest = _lastGuideStepPushed;
        foreach (var sample in samples)
        {
            if (sample.Timestamp <= _lastGuideStepPushed)
            {
                continue;
            }

            if (sample.Timestamp > newest)
            {
                newest = sample.Timestamp;
            }

            _ = BroadcastSafeAsync(new WebSocketEventDto
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
            });
        }

        _lastGuideStepPushed = newest;
    }

    private void OnEnhanceProgress(object? sender, EnhanceProgress e)
    {
        var overall = e.StepCount > 0
            ? (e.StepIndex + System.Math.Clamp(e.StepPercent, 0f, 1f)) / e.StepCount * 100f
            : 0f;
        _ = BroadcastSafeAsync(new WebSocketEventDto
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
        });
    }

    private void OnEnhanceCompleted(object? sender, EnhanceJobCompletedEventArgs e)
    {
        if (!e.Succeeded)
        {
            Notify("Warning", $"Image enhance failed: {e.Error ?? "unknown error"}");
        }

        _ = BroadcastSafeAsync(new WebSocketEventDto
        {
            Event = "ENHANCE-COMPLETED",
            Data = new Dictionary<string, object?>
            {
                ["InputPath"] = e.InputPath,
                ["OutputPath"] = e.OutputPath,
                ["Succeeded"] = e.Succeeded,
                ["Error"] = e.Error
            }
        });
    }

    private void SubscribeToSession(ISession session)
    {
        session.PhaseChanged += OnPhaseChanged;
        session.FrameWritten += OnFrameWritten;
        session.PlateSolveCompleted += OnPlateSolveCompleted;
        session.ScoutCompleted += OnScoutCompleted;
        session.GuiderStateChanged += OnGuiderStateChanged;
        session.PromptRequested += OnPromptRequested;
    }

    private void UnsubscribeFromSession(ISession session)
    {
        session.PhaseChanged -= OnPhaseChanged;
        session.FrameWritten -= OnFrameWritten;
        session.PlateSolveCompleted -= OnPlateSolveCompleted;
        session.ScoutCompleted -= OnScoutCompleted;
        session.GuiderStateChanged -= OnGuiderStateChanged;
        session.PromptRequested -= OnPromptRequested;
    }

    private void OnPhaseChanged(object? sender, SessionPhaseChangedEventArgs e)
    {
        if (e.NewPhase is SessionPhase.Failed)
        {
            Notify("Error", hostedSession.CurrentSession?.FailureReason is { Length: > 0 } reason
                ? $"Session failed: {reason}"
                : "Session failed");
        }
        else
        {
            Notify("Info", $"{e.OldPhase} -> {e.NewPhase}");
        }

        _ = BroadcastSafeAsync(new WebSocketEventDto
        {
            Event = "SESSION-PHASE-CHANGED",
            Data = new Dictionary<string, object?>
            {
                ["OldPhase"] = e.OldPhase.ToString(),
                ["NewPhase"] = e.NewPhase.ToString()
            }
        });
    }

    private void OnFrameWritten(object? sender, FrameWrittenEventArgs e)
    {
        var entry = e.Entry;
        _ = BroadcastSafeAsync(new WebSocketEventDto
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
        });
    }

    private void OnPlateSolveCompleted(object? sender, PlateSolveCompletedEventArgs e)
    {
        var record = e.Record;
        if (!record.Succeeded)
        {
            Notify("Warning", $"Plate solve failed ({record.Context}) on {record.OtaName}: {record.DetectedStars} stars detected");
        }

        _ = BroadcastSafeAsync(new WebSocketEventDto
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
        });
    }

    private void OnScoutCompleted(object? sender, ScoutCompletedEventArgs e)
    {
        if (e.Outcome is not ScoutOutcome.Proceed)
        {
            Notify("Warning", $"Scout on {e.Target.Name}: {e.Classification} -> {e.Outcome}");
        }

        _ = BroadcastSafeAsync(new WebSocketEventDto
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
        });
    }

    private void OnGuiderStateChanged(object? sender, GuiderStateChangedEventArgs e)
    {
        // A transition INTO "Guiding" is the recovery, anything else is a departure from it -- which is
        // the case an operator wants surfaced (star loss, a dither that never settled).
        var severity = string.Equals(e.NewState, "Guiding", StringComparison.OrdinalIgnoreCase) ? "Info" : "Warning";
        Notify(severity, $"Guider: {e.OldState ?? "none"} -> {e.NewState ?? "none"}");

        _ = BroadcastSafeAsync(new WebSocketEventDto
        {
            Event = "GUIDER-STATE-CHANGED",
            Data = new Dictionary<string, object?>
            {
                ["OldState"] = e.OldState,
                ["NewState"] = e.NewState
            }
        });
    }

    /// <summary>
    /// Publishes a user prompt for an HTTP client to answer, and guarantees the run cannot wedge on it.
    /// <para>
    /// <b>Why this needs handling at all.</b> A session answers a prompt itself only while <i>nothing</i>
    /// is subscribed to <c>PromptRequested</c>. The moment this broadcaster subscribes, the server stops
    /// doing that, so a prompt with nobody listening would sit inside <c>RunAsync</c>'s try -- whose
    /// finally is what parks the mount, warms the cameras and closes the covers -- leaving the rig
    /// exposed at dawn. A hang there is not an exception; it simply never returns.
    /// </para>
    /// <para>
    /// <b>While an observer is attached, wait as long as it takes.</b> There is deliberately no timer: an
    /// attached client that ignores <c>PROMPT-REQUESTED</c> is a client bug, and guessing an answer after
    /// some arbitrary interval does not fix it -- it just fabricates a decision faster. The only bound is
    /// <i>liveness</i>: if the last observer goes away while a prompt is outstanding, the poll loop
    /// resolves it (<see cref="ResolveOrphanedPrompt"/>).
    /// </para>
    /// <para>
    /// <b>With nobody attached the session decides, not this class.</b> It simply un-registers itself for
    /// this prompt by responding with the session's own configured
    /// <c>UnattendedPromptResponse</c> -- which defaults to <i>decline</i>, because these prompts gate
    /// physical acts and proceeding would assert something nobody did. That policy belongs to the session,
    /// so it is read from there rather than reinvented here.
    /// </para>
    /// </summary>
    internal void OnPromptRequested(object? sender, SessionPromptEventArgs e)
    {
        if (eventHub.ClientCount == 0)
        {
            AnswerUnattended(e, "no observer attached");
            return;
        }

        hostedSession.SetPendingPrompt(e);

        // Severity Error, not Warning: the run is blocked until somebody acts, and when it needs a body
        // at the observatory a remote operator cannot clear it themselves.
        Notify("Error", e.RequiresPhysicalPresence
            ? $"{e.Title} (needs someone at the rig): {e.Message}"
            : $"{e.Title}: {e.Message}");

        _ = BroadcastSafeAsync(new WebSocketEventDto
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
        });
    }

    /// <summary>
    /// Answers a prompt on the session's own terms when there is nobody to ask. Mirrors what the session
    /// would have done had this broadcaster never subscribed, so attaching an event stream cannot change
    /// the outcome of an unattended run.
    /// </summary>
    private void AnswerUnattended(SessionPromptEventArgs prompt, string why)
    {
        // The policy rides on the prompt (SessionPromptEventArgs.DefaultIfUnanswerable) rather than being
        // read back off the session, so this cannot drift from what the session would have decided alone.
        logger.LogInformation("Prompt '{Title}' answered {Answer} ({Why})",
            prompt.Title, prompt.DefaultIfUnanswerable ? "proceed" : "skip", why);

        prompt.Respond(prompt.DefaultIfUnanswerable);
    }

    /// <summary>
    /// Resolves an outstanding prompt once the last observer has gone. Without this, a client that
    /// attached, triggered the hold, and then dropped its socket would leave the run blocked with nobody
    /// able to answer -- the exact wedge the no-observer branch exists to prevent, reached by a different
    /// route.
    /// </summary>
    internal void ResolveOrphanedPrompt()
    {
        if (eventHub.ClientCount > 0 || hostedSession.PendingPrompt is not { } prompt)
        {
            return;
        }

        // Grab-and-clear first so a client reconnecting at this instant cannot double-answer.
        if (hostedSession.TryRespondToPrompt(prompt.DefaultIfUnanswerable))
        {
            logger.LogWarning("Prompt '{Title}' was outstanding when the last observer disconnected", prompt.Title);
            Notify("Warning", $"{prompt.Title}: observer disconnected before answering");
        }
    }

    /// <summary>Records a notification and pushes it to connected clients.</summary>
    private void Notify(string severity, string message)
    {
        var dto = new NotificationDto
        {
            Severity = severity,
            Message = message,
            TimestampUtc = timeProvider.GetUtcNow()
        };

        hostedSession.AddNotification(dto);

        _ = BroadcastSafeAsync(new WebSocketEventDto
        {
            Event = "NOTIFICATION",
            Data = new Dictionary<string, object?>
            {
                ["Severity"] = dto.Severity,
                ["Message"] = dto.Message,
                ["TimestampUtc"] = dto.TimestampUtc
            }
        });
    }

    private async Task BroadcastSafeAsync(WebSocketEventDto eventDto)
    {
        try
        {
            if (eventHub.ClientCount > 0)
            {
                await eventHub.BroadcastAsync(eventDto);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast event {Event}", eventDto.Event);
        }
    }
}
