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

    // Written by Attach, on the thread that starts a run; read by the poll loop.
    private ISession? _subscribedSession;

    // The session the guide-step watermark belongs to. Poll-loop only, like the watermark itself.
    private ISession? _watermarkFor;

    /// <summary>
    /// Timestamp of the newest guide sample already pushed, so the poll below emits only new ones.
    /// Guide samples are appended by the guide loop and read here; a timestamp watermark is enough to
    /// diff them without holding a reference to the previous snapshot.
    /// </summary>
    private DateTimeOffset _lastGuideStepPushed = DateTimeOffset.MinValue;

    /// <summary>
    /// Attaches to every run the node starts, before its body runs (<see cref="HostedSession.RunStarting"/>).
    /// Subscribed here rather than in <see cref="ExecuteAsync"/> because the host starts this service before
    /// it serves a request, so no start can slip in ahead of it.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        hostedSession.RunStarting += Attach;
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Subscribes to <paramref name="session"/>'s events, and lets go of the previous run's. Called as the node
    /// starts a run, so the run's first events, its first prompt among them, reach the clients.
    /// </summary>
    internal void Attach(ISession session)
    {
        if (Interlocked.Exchange(ref _subscribedSession, session) is { } previous)
        {
            if (ReferenceEquals(previous, session))
            {
                return;
            }
            UnsubscribeFromSession(previous);
        }

        SubscribeToSession(session);
        logger.LogInformation("EventBroadcaster subscribed to session");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EventBroadcaster started, waiting for session");

        // The enhancer is a process-lifetime singleton, so subscribe once up front (unlike the
        // session, which comes and goes and is (un)subscribed inside the loop below).
        imageEnhancer.Progressed += OnEnhanceProgress;
        imageEnhancer.Completed += OnEnhanceCompleted;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Attach subscribed it as its run started; the poll only reads what the session has no event for.
            if (Volatile.Read(ref _subscribedSession) is { } session)
            {
                if (!ReferenceEquals(session, _watermarkFor))
                {
                    // Start the watermark at whatever the ring already holds rather than at MinValue, so
                    // attaching does not re-broadcast the existing ~5 minute window one event at a time.
                    // Backfill is the snapshot's job (the state DTO carries the ring); the broadcast exists
                    // only to announce what is new -- the same division as the exposure log.
                    _watermarkFor = session;
                    _lastGuideStepPushed = NewestGuideSampleTime(session);
                }

                PushNewGuideSteps(session);
                NotifyLimitTransition(session);
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
        hostedSession.RunStarting -= Attach;

        if (Interlocked.Exchange(ref _subscribedSession, null) is { } last)
        {
            UnsubscribeFromSession(last);
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

            BroadcastSafe(BroadcastEvents.GuideStep(sample));
        }

        _lastGuideStepPushed = newest;
    }

    private void OnEnhanceProgress(object? sender, EnhanceProgress e)
    {
        BroadcastSafe(BroadcastEvents.EnhanceProgress(e));
    }

    private void OnEnhanceCompleted(object? sender, EnhanceJobCompletedEventArgs e)
    {
        if (!e.Succeeded)
        {
            Notify("Warning", $"Image enhance failed: {e.Error ?? "unknown error"}");
        }

        BroadcastSafe(BroadcastEvents.EnhanceCompleted(e));
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

        BroadcastSafe(BroadcastEvents.PhaseChanged(e));
    }

    private void OnFrameWritten(object? sender, FrameWrittenEventArgs e)
    {
        var entry = e.Entry;
        BroadcastSafe(BroadcastEvents.FrameWritten(entry));
    }

    private void OnPlateSolveCompleted(object? sender, PlateSolveCompletedEventArgs e)
    {
        var record = e.Record;
        if (!record.Succeeded)
        {
            Notify("Warning", $"Plate solve failed ({record.Context}) on {record.OtaName}: {record.DetectedStars} stars detected");
        }

        BroadcastSafe(BroadcastEvents.PlateSolveCompleted(record));
    }

    private void OnScoutCompleted(object? sender, ScoutCompletedEventArgs e)
    {
        if (e.Outcome is not ScoutOutcome.Proceed)
        {
            Notify("Warning", $"Scout on {e.Target.Name}: {e.Classification} -> {e.Outcome}");
        }

        BroadcastSafe(BroadcastEvents.ScoutCompleted(e));
    }

    private void OnGuiderStateChanged(object? sender, GuiderStateChangedEventArgs e)
    {
        // A transition INTO "Guiding" is the recovery, anything else is a departure from it -- which is
        // the case an operator wants surfaced (star loss, a dither that never settled).
        var severity = string.Equals(e.NewState, "Guiding", StringComparison.OrdinalIgnoreCase) ? "Info" : "Warning";
        Notify(severity, $"Guider: {e.OldState ?? "none"} -> {e.NewState ?? "none"}");

        BroadcastSafe(BroadcastEvents.GuiderStateChanged(e));
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
        // Only a client that can answer counts: a ninaAPI v2 socket has no prompt route (EventHub.PromptObserverCount).
        if (eventHub.PromptObserverCount == 0)
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

        BroadcastSafe(BroadcastEvents.PromptRequested(e));
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
        if (eventHub.PromptObserverCount > 0 || hostedSession.PendingPrompt is not { } prompt)
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

    // P4 of mount-safety-limits.md, the headless half of what AppSignalHandler.NotifyLimitTransitions does
    // for the GUI: a limit reaches the node's feed (and so the remote client's card, via LastNotification)
    // when its verdict changes CLASS -- clear -> warning -> acted, or a driver-enforced stop -- never per
    // poll. The latch's downgrade to Warn after acting leaves IsWarningOnly false, so it is not a change.
    private (MountLimitKind Kind, bool WarningOnly) _lastLimitClass;

    private void NotifyLimitTransition(ISessionTelemetry session)
    {
        var verdict = session.MountLimitVerdict;
        var cls = (verdict.Kind, verdict.IsWarningOnly);
        if (cls == _lastLimitClass)
        {
            return;
        }
        var wasBreached = _lastLimitClass.Kind is not MountLimitKind.None;
        _lastLimitClass = cls;
        if (!verdict.IsBreached)
        {
            if (wasBreached)
            {
                Notify("Info", "Mount is clear of its safety limits again.");
            }
            return;
        }
        Notify(verdict.IsWarningOnly ? "Warning" : "Error", $"Mount safety limit: {verdict.Describe()}");
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

        BroadcastSafe(BroadcastEvents.Notification(dto));
    }

    /// <summary>
    /// Queues an event for every connected client. It returns at once: each client has its own sender
    /// (<see cref="EventHub"/>), so a broadcast raised on a session's thread never waits for a socket.
    /// </summary>
    private void BroadcastSafe(WebSocketEventDto eventDto)
    {
        try
        {
            eventHub.Broadcast(eventDto);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast event {Event}", eventDto.Event);
        }
    }
}
