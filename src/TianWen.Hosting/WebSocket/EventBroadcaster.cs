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
    /// <summary>
    /// How long an outstanding prompt waits for an HTTP answer before the node proceeds on its own.
    /// See <see cref="OnPromptRequested"/> for why a bound is mandatory.
    /// </summary>
    private static readonly TimeSpan PromptAutoProceedAfter = TimeSpan.FromMinutes(2);

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
    /// Emits a <c>GUIDE-STEP</c> per newly-arrived guide sample.
    /// <para>
    /// Without this a client wanting live guide errors has to re-pull the whole ~5 minute sample ring
    /// out of <c>/session/state</c> every second and diff it locally -- tens of samples re-sent per
    /// poll to learn about the one that changed. Pushing the delta means the state payload's guider
    /// block can eventually shrink to stats-only.
    /// </para>
    /// </summary>
    /// <summary>
    /// Newest guide-sample timestamp currently in the session's ring, or <see cref="DateTimeOffset.MinValue"/>
    /// when it is empty. The ring is oldest-first, so this is the last element.
    /// </summary>
    private static DateTimeOffset NewestGuideSampleTime(ISessionTelemetry session)
    {
        var samples = session.GuideSamples;
        return samples.IsDefaultOrEmpty ? DateTimeOffset.MinValue : samples[^1].Timestamp;
    }

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
    /// Publishes a user prompt for an HTTP client to answer, then <b>guarantees the run continues</b>.
    /// <para>
    /// <b>The bound is not optional.</b> A session auto-proceeds only when <i>nothing</i> is subscribed
    /// to <c>PromptRequested</c> -- that is what keeps unattended CLI / server runs from blocking. The
    /// moment this broadcaster subscribes, the server stops auto-proceeding, so a prompt with nobody
    /// listening on the other end would wedge the night at "switch on the flat panel" until the session
    /// token cancels. Two safeguards restore the guarantee: with no WebSocket client attached the node
    /// answers <i>immediately</i> (byte-identical to the old headless behaviour), and with one attached
    /// it still proceeds after <see cref="PromptAutoProceedAfter"/> in case that client goes away
    /// without answering. Proceeding, not declining, is the right default -- it matches what a headless
    /// run has always done.
    /// </para>
    /// </summary>
    private void OnPromptRequested(object? sender, SessionPromptEventArgs e)
    {
        if (eventHub.ClientCount == 0)
        {
            e.Respond(true);
            return;
        }

        hostedSession.SetPendingPrompt(e);
        Notify("Warning", $"{e.Title}: {e.Message}");

        _ = BroadcastSafeAsync(new WebSocketEventDto
        {
            Event = "PROMPT-REQUESTED",
            Data = new Dictionary<string, object?>
            {
                ["Title"] = e.Title,
                ["Message"] = e.Message,
                ["ContinueLabel"] = e.ContinueLabel,
                ["CancelLabel"] = e.CancelLabel
            }
        });

        _ = AutoProceedAsync();

        async Task AutoProceedAsync()
        {
            try
            {
                await timeProvider.SleepAsync(PromptAutoProceedAfter, CancellationToken.None);

                // No-ops when the prompt was already answered: TryRespondToPrompt grabs-and-clears.
                if (hostedSession.TryRespondToPrompt(true))
                {
                    logger.LogWarning("Prompt '{Title}' went unanswered for {Timeout}, proceeding", e.Title, PromptAutoProceedAfter);
                    Notify("Warning", $"{e.Title}: no answer after {PromptAutoProceedAfter.TotalMinutes:0} min, proceeding");
                }
            }
            catch (Exception ex)
            {
                // Never let the fallback itself be the reason a night stalls.
                logger.LogWarning(ex, "Prompt auto-proceed timer failed for '{Title}'", e.Title);
                hostedSession.TryRespondToPrompt(true);
            }
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
