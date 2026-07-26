using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

public interface IHostedSession : IHostedService
{
    ISession? CurrentSession { get; }

    /// <summary>Active profile ID, set before starting a session or via profile/switch.</summary>
    Guid? ActiveProfileId { get; }

    /// <summary>Targets queued before session start. Drained into the session when it begins.</summary>
    IReadOnlyList<PendingTarget> PendingTargets { get; }

    /// <summary>
    /// A full-fidelity schedule pushed by a driving client, or empty. Takes precedence over
    /// <see cref="PendingTargets"/> at session start, because it carries the scheduler's slot times and
    /// per-filter plans that <see cref="PendingTarget"/> cannot express.
    /// </summary>
    ImmutableArray<ScheduledObservation> PendingSchedule { get; }

    /// <summary>
    /// The session's outstanding user prompt, or null. Exposed on <c>/session/state</c> as well as via
    /// the <c>PROMPT-REQUESTED</c> broadcast: polling is the authoritative channel for a mirroring
    /// client, so a prompt that is only ever pushed would be unanswerable by a client that connected
    /// after it fired (or that dropped the socket while it was open).
    /// </summary>
    SessionPromptEventArgs? PendingPrompt { get; }

    /// <summary>Most recent notifications, oldest first.</summary>
    ImmutableArray<NotificationDto> Notifications { get; }

    void SetActiveProfile(Guid profileId);
    void AddTarget(PendingTarget target);
    void ClearTargets();

    /// <summary>Replaces the pending schedule. An empty array clears it.</summary>
    void SetSchedule(ImmutableArray<ScheduledObservation> schedule);

    /// <summary>
    /// Answers the outstanding prompt. Returns false when there is none (a stale client retry, or a
    /// race with the session cancelling its own prompt) rather than throwing.
    /// </summary>
    bool TryRespondToPrompt(bool proceed);

    void AddNotification(NotificationDto notification);
}

internal class HostedSession(ISessionFactory sessionFactory) : IHostedSession
{
    /// <summary>
    /// Notification history depth. Deep enough that a client attaching part-way through a night still
    /// sees the run's story, shallow enough to stay a bounded in-memory cost.
    /// </summary>
    private const int NotificationCapacity = 200;

    private ISession? _session;
    private CancellationTokenSource? _cts;
    private Guid? _activeProfileId;
    private SessionPromptEventArgs? _pendingPrompt;
    private ImmutableArray<ScheduledObservation> _pendingSchedule = [];
    private readonly List<PendingTarget> _pendingTargets = [];

    // System.Threading.Lock rather than an object: the standing rule in CLAUDE.md. The list itself
    // cannot be handed out immutably (callers Add/Clear/Drain individual items), so the lock stays --
    // it guards only these three O(n) list operations, is never taken from a render thread, and the
    // reads it serialises already copy out.
    private readonly Lock _targetLock = new Lock();

    private readonly CircularBuffer<NotificationDto> _notifications = new CircularBuffer<NotificationDto>(NotificationCapacity);

    public ISession? CurrentSession => Interlocked.CompareExchange(ref _session, null, null);

    public Guid? ActiveProfileId => _activeProfileId;

    public IReadOnlyList<PendingTarget> PendingTargets
    {
        get
        {
            lock (_targetLock)
            {
                return [.. _pendingTargets];
            }
        }
    }

    public ImmutableArray<ScheduledObservation> PendingSchedule => _pendingSchedule;

    public SessionPromptEventArgs? PendingPrompt => Volatile.Read(ref _pendingPrompt);

    public ImmutableArray<NotificationDto> Notifications => _notifications.Snapshot;

    public void SetActiveProfile(Guid profileId)
    {
        _activeProfileId = profileId;
    }

    public void AddTarget(PendingTarget target)
    {
        lock (_targetLock)
        {
            _pendingTargets.Add(target);
        }
    }

    public void ClearTargets()
    {
        lock (_targetLock)
        {
            _pendingTargets.Clear();
        }
    }

    public void SetSchedule(ImmutableArray<ScheduledObservation> schedule)
    {
        // Whole-array swap: readers snapshot with one read, no lock needed. ImmutableInterlocked rather
        // than Volatile/Interlocked because ImmutableArray<T> is a STRUCT wrapping the array reference --
        // the plain overloads only accept reference types (the same reason CircularBuffer uses it).
        ImmutableInterlocked.InterlockedExchange(ref _pendingSchedule, schedule.IsDefault ? [] : schedule);
    }

    public bool TryRespondToPrompt(bool proceed)
    {
        // Grab-and-clear so two racing responders cannot both answer; Respond itself is idempotent
        // (TrySetResult), but clearing here is what makes a second call report "no pending prompt"
        // instead of silently succeeding.
        if (Interlocked.Exchange(ref _pendingPrompt, null) is not { } prompt)
        {
            return false;
        }

        prompt.Respond(proceed);
        return true;
    }

    public void AddNotification(NotificationDto notification) => _notifications.Add(notification);

    /// <summary>
    /// Records the session's outstanding prompt so it can be answered over HTTP. Called by
    /// <c>EventBroadcaster</c>, which is the one component already subscribed to every session event.
    /// </summary>
    internal void SetPendingPrompt(SessionPromptEventArgs? prompt) => Volatile.Write(ref _pendingPrompt, prompt);

    /// <summary>
    /// Drains pending targets and clears the list. Called by session start endpoints.
    /// </summary>
    internal PendingTarget[] DrainTargets()
    {
        lock (_targetLock)
        {
            var result = _pendingTargets.ToArray();
            _pendingTargets.Clear();
            return result;
        }
    }

    /// <summary>
    /// Drains the pushed schedule and clears it, mirroring <see cref="DrainTargets"/>.
    /// </summary>
    internal ImmutableArray<ScheduledObservation> DrainSchedule()
        => ImmutableInterlocked.InterlockedExchange(ref _pendingSchedule, []);

    /// <summary>
    /// Sets the active session (called when a session is created via the API or signal bus).
    /// </summary>
    internal void SetSession(ISession session)
    {
        Interlocked.Exchange(ref _session, session);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var oldCts = Interlocked.Exchange(ref _cts, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        if (oldCts is { IsCancellationRequested: false })
        {
            await oldCts.CancelAsync();
            oldCts.Dispose();
        }
        await sessionFactory.InitializeAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _cts, null) is { IsCancellationRequested: false } cts)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        if (Interlocked.Exchange(ref _session, null) is { } session)
        {
            // Graceful shutdown is handled by Session.RunAsync's try/finally
            // (Session.cs:288-300) which always invokes Finalise(CancellationToken.None)
            // — park mount, warm cameras, close covers — when the session token is
            // cancelled. Disposing here just releases the device handles.
            await session.DisposeAsync();
        }
    }
}
