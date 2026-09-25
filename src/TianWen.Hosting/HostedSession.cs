using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

public interface IHostedSession : IHostedService
{
    /// <summary>
    /// The node's run: the one going on, or the last one to end, until the next start replaces it. A
    /// finished run stays readable so a client polling <c>/session/state</c> sees how it ended.
    /// </summary>
    ISession? CurrentSession { get; }

    /// <summary>Whether a run is going on now. A run that has ended does not block the next start.</summary>
    bool IsRunning { get; }

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

    /// <summary>
    /// Completes once the node has discovered its profiles and devices. That starts with the host but in
    /// the background, so the server is listening at once; a start waits here instead, and the wait
    /// belongs to the caller's token.
    /// </summary>
    Task WhenInitialisedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="run"/> over <paramref name="session"/> as the node's run, on a token the NODE
    /// owns, never a request's: it is cancelled by <see cref="TryAbort"/> and by the host stopping, and by
    /// nothing a client's connection does. A run that has ended is replaced, its session disposed before
    /// this one touches the rig.
    /// </summary>
    /// <returns><see langword="false"/> when another run is going on, in which case the session is still
    /// the caller's to dispose.</returns>
    Task<bool> TryStartAsync(ISession session, Func<ISession, CancellationToken, Task> run);

    /// <summary>
    /// Asks the run going on to stop. Its token is cancelled and it ends through its own Finalise (park,
    /// warm-up, covers), which the returned task completes after; its session is left readable, never
    /// disposed under it. Null when nothing is running.
    /// </summary>
    Task? TryAbort();
}

internal class HostedSession(ISessionFactory sessionFactory, IDeviceHub hub, ITimeProvider timeProvider, ILogger<HostedSession> logger)
    : IHostedSession
{
    /// <summary>
    /// Notification history depth. Deep enough that a client attaching part-way through a night still
    /// sees the run's story, shallow enough to stay a bounded in-memory cost.
    /// </summary>
    private const int NotificationCapacity = 200;

    /// <summary>
    /// How long the host may take to stop: a session's Finalise (its warm-up ramp is budgeted by the
    /// configuration, five minutes by default, and cannot be cut short) and then the hub's own cameras,
    /// whose out-of-session ramp is capped at 15 minutes.
    /// </summary>
    internal static readonly TimeSpan ShutdownBudget = TimeSpan.FromMinutes(30);

    // The node's run, replaced WHOLE by compare-and-swap, so two starts cannot both win and a finished run
    // needs no clearing: the next start swaps it out. A run's body waits until its start has won the swap
    // (NodeRun.Release), so no run touches the rig before it owns the node.
    private NodeRun? _run;

    // The node's own lifetime: device discovery runs on it. Runs have their own tokens, cancelled only by an
    // abort, because a run must end through its Finalise rather than be cut off by whatever stops the host.
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private volatile Task _initialisation = Task.CompletedTask;

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

    public ISession? CurrentSession => Volatile.Read(ref _run)?.Session;

    public bool IsRunning => Volatile.Read(ref _run) is { Completion.IsCompleted: false };

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

    public Task WhenInitialisedAsync(CancellationToken cancellationToken) => _initialisation.WaitAsync(cancellationToken);

    public async Task<bool> TryStartAsync(ISession session, Func<ISession, CancellationToken, Task> run)
    {
        var previous = Volatile.Read(ref _run);
        if (previous is { Completion.IsCompleted: false })
        {
            return false;
        }

        // Built before the swap and started only by winning it: building the run INSIDE the exchange
        // would start it on every racing caller (CLAUDE.md, Concurrency).
        var next = new NodeRun(session, run, logger);
        if (Interlocked.CompareExchange(ref _run, next, previous) != previous)
        {
            next.Release(won: false);
            return false;
        }

        if (previous is not null)
        {
            // The last run's session goes before this one touches the rig: a session drives its own
            // drivers (until P0b item 11 borrows them from the hub), so both could hold one device.
            await previous.DisposeAsync(logger);
        }

        next.Release(won: true);
        return true;
    }

    public Task? TryAbort()
    {
        if (Volatile.Read(ref _run) is not { Completion.IsCompleted: false } run)
        {
            return null;
        }

        run.Cancel();
        return run.Completion;
    }

    /// <summary>
    /// Starts the node's device discovery WITHOUT waiting for it: discovery probes serial ports and the
    /// network for tens of seconds, and the host must be listening long before that. This used to never
    /// run at all, since the host was never told to start this service.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _initialisation = Task.Run(() => InitialiseAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the rig in the one safe order, the same one <c>RigShutdown</c> keeps for the GUI: the run
    /// first, aborted into its own Finalise and awaited, then every camera still connected to the hub,
    /// warmed where its cooler is on. <paramref name="cancellationToken"/> is the host's shutdown timeout,
    /// which <c>AddHostedSession</c> sets long enough for a warm-up ramp.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _run) is { } run)
        {
            var ended = true;
            if (!run.Completion.IsCompleted)
            {
                logger.LogWarning("The host is stopping: aborting the running session, which ends through its Finalise");
                run.Cancel();
                try
                {
                    await run.Completion.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    ended = false;
                    logger.LogError("The host's shutdown timeout ran out before the session's Finalise finished");
                }
            }

            // Never under a run still going: disposing it disconnects its drivers mid-ramp, which is the
            // very thing the order above is for. A process that exits leaves it to the operating system.
            if (ended)
            {
                await run.DisposeAsync(logger);
            }
        }

        try
        {
            await hub.StopConnectedCamerasAsync(timeProvider, logger).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("The host's shutdown timeout ran out while its cameras were warming");
        }

        await _lifetime.CancelAsync();
    }

    private async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await sessionFactory.InitializeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Node initialisation was cancelled: the host is stopping");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Node initialisation failed: device discovery did not complete");
        }
    }

    /// <summary>One run of the node: its session, the token that aborts it, and its whole life as a task.</summary>
    private sealed class NodeRun
    {
        private readonly CancellationTokenSource _abort = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public NodeRun(ISession session, Func<ISession, CancellationToken, Task> body, ILogger logger)
        {
            Session = session;
            Completion = RunWhenReleasedAsync(body, logger);
        }

        public ISession Session { get; }

        /// <summary>Completes when the run has ended, its Finalise included, or when its start lost the swap.</summary>
        public Task Completion { get; }

        public void Release(bool won) => _release.TrySetResult(won);

        public void Cancel()
        {
            try
            {
                _abort.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposed by the start that replaced it: the run had ended already, so nothing is left to stop.
            }
        }

        /// <summary>Disposes the session once, whoever asks first.</summary>
        public async ValueTask DisposeAsync(ILogger logger)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await Session.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Disposing the node's last session failed");
            }
            _abort.Dispose();
        }

        private async Task RunWhenReleasedAsync(Func<ISession, CancellationToken, Task> body, ILogger logger)
        {
            // An abort asked for before the start won is a run that never began: nothing to finalise.
            if (!await _release.Task.ConfigureAwait(false) || _abort.IsCancellationRequested)
            {
                return;
            }

            try
            {
                // Off the starting thread, and on None: a token here would skip the run if it were already
                // cancelled, after the swap had published it, leaving a run that never ran.
                await Task.Run(() => body(Session, _abort.Token), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                logger.LogInformation("The node's run ended on an abort");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The node's run faulted");
            }
        }
    }
}
