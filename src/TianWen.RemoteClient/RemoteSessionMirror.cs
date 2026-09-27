using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

// TianWen.Lib.Devices.Guider declares its own GuiderStateChangedEventArgs (driver-level app state);
// the session-level one is what ISessionTelemetry exposes. Alias so the reference is unambiguous
// without dropping the Guider namespace (SettleProgress and GuideStats come from it).
using GuiderStateChangedEventArgs = TianWen.Lib.Sequencing.GuiderStateChangedEventArgs;

namespace TianWen.RemoteClient
{
    /// <summary>
    /// Which of the node's frames a mirror pulls. Every frame is the node's own, LINEAR and full-resolution
    /// (<c>GET /frames/{source}/latest</c>, P4 of docs/plans/hardware-in-the-server.md), never the stretched preview
    /// JPEG: the Live Session and Guider tabs stretch, measure and save what they are handed, exactly as they do a
    /// local session's frame, and a picture stretched once already would be stretched twice.
    /// </summary>
    /// <param name="IncludeOtas">
    /// Whether to pull each OTA's frame, which only the Live Session tab draws (P5b part 6): a rig on screen on any
    /// other tab pulls none.
    /// </param>
    /// <param name="IncludeGuider">
    /// Whether to also pull the guide-camera frame. Off by default, and separately from the OTA frames on
    /// purpose: only a view that draws the guide camera wants it, at guiding cadence, and anything else would
    /// pay a request per poll for a picture nothing draws.
    /// </param>
    public readonly record struct PreviewOptions(bool IncludeOtas = true, bool IncludeGuider = false)
    {
        /// <summary>
        /// The declared defaults, each OTA's frame and no guide frame. Without this a bare <c>new PreviewOptions()</c> is the
        /// struct's zero, which asks for no frame at all: a record struct's parameterless <c>new()</c> never reads the primary
        /// constructor's defaults.
        /// </summary>
        public PreviewOptions() : this(IncludeOtas: true, IncludeGuider: false)
        {
        }
    }

    /// <summary>
    /// A session running on another node, observed as an <see cref="ISessionTelemetry"/>.
    /// <para>
    /// This is the payoff of the P3.1 split: the Live Session and Guider tabs, and every helper that
    /// reads a session, take <see cref="ISessionTelemetry"/> and therefore render a rig's session with
    /// no changes at all. The mirror polls <c>GET /session/state</c> and subscribes the node's WebSocket
    /// stream to re-raise the telemetry events, so <c>AppSignalHandler</c>'s subscriptions also work
    /// untouched.
    /// </para>
    /// <para>
    /// <b>Polling is authoritative; events are a latency shortcut.</b> Each poll swaps in a whole
    /// immutable <see cref="SessionStateDto"/> by a single reference write, so a reader on the render
    /// thread always sees one internally consistent snapshot with no lock and no torn mix of two polls.
    /// Events only fire notifications; they never mutate the snapshot. A missed event therefore costs a
    /// moment of staleness, never a wrong screen -- which is why there is no replay or resync protocol.
    /// </para>
    /// <para>
    /// <b>Every event the node sends is handled, and each is one of four kinds</b> (P5b part 6, <see cref="Dispatch"/>):
    /// a change to the state, which makes the mirror poll at once rather than at its next tick; a new frame
    /// (<c>FRAME-AVAILABLE</c>), which fetches that frame and nothing else; an occurrence the state does not carry, which
    /// is raised as its event (a solve, a scout, a note); or another client's business (a device's state, a profile, a
    /// job, an enhance), which this session's mirror leaves to it. <see cref="Changed"/> tells a view there is something
    /// new to draw.
    /// </para>
    /// <para>
    /// <b>Fidelity.</b> Everything in <see cref="SessionStateDto"/> is faithful (phase, activity,
    /// failure reason, counters, mount pointing + name, per-OTA camera/focus/filter state and display
    /// facts, guide stats + sample ring, schedule, phase timeline, cooling ramp, focus history and
    /// exposure log). The frames are the node's own, linear, when <see cref="Previews"/> asks for them.
    /// Fields with no wire representation yet return empty rather than guessing, and each says why below; the tabs already handle empty because a local session starts
    /// out that way too. <see cref="PlateSolveHistory"/> is <b>event-sourced</b> rather than read from
    /// the snapshot -- the node broadcasts every solve but carries no history in its state -- so it
    /// covers only what has happened since this mirror attached.
    /// </para>
    /// <para>
    /// <b>Driving, not just watching.</b> <see cref="StartAsync"/> / <see cref="StartFlatsAsync"/> /
    /// <see cref="AbortAsync"/> and the prompt round-trip make this a control surface as well as an
    /// observation one. They are declared on the mirror rather than on
    /// <see cref="ISessionTelemetry"/>, which a local <c>Session</c> also implements and which must stay
    /// a read-only contract.
    /// </para>
    /// </summary>
    public sealed class RemoteSessionMirror : ISessionTelemetry, IAsyncDisposable
    {
        // Poll cadences. A running session changes visibly (countdowns, guide samples, pointing); an
        // idle node only needs to be noticed when it starts. Both are far cheaper than the LAN can
        // notice, and the WS stream already covers the moments that matter for responsiveness.
        private static readonly TimeSpan ActivePollInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Ceiling on the poll interval for a rig that is not answering.
        /// <para>
        /// <b>What this is not for.</b> Each mirror owns its own poll loop, so a dark rig structurally
        /// cannot stall the others -- there is no shared tick to hold up. What it fixes is that a dark rig
        /// was otherwise retried at the full live cadence forever, which on a board of several powered-off
        /// rigs is steady pointless traffic and a log line every two seconds each.
        /// </para>
        /// <para>
        /// Capped rather than unbounded so a rig coming back is noticed within half a minute without
        /// anyone touching anything. Deliberately the same shape and ceiling as
        /// <c>TianWenEventStream</c>'s reconnect backoff, since they are backing off from the same rig for
        /// the same reason.
        /// </para>
        /// </summary>
        internal static readonly TimeSpan MaxUnreachablePollInterval = TimeSpan.FromSeconds(30);

        /// <summary>Cap on the locally accumulated event-sourced history, the order of a night's worth.</summary>
        private const int MaxPlateSolveHistory = 500;

        private readonly TianWenNodeClient _client;
        private readonly TianWenEventStream _events;
        private readonly ITimeProvider _timeProvider;
        private readonly ILogger _logger;

        // The whole snapshot behind one reference, published with Volatile.Write / read with
        // Volatile.Read: the poll loop runs on a thread-pool continuation while the render thread reads
        // ~30 properties per frame, and a per-field copy would let a frame mix two polls.
        private SessionStateDto? _snapshot;

        // Accumulated from the event stream. ImmutableArray + reference swap so the render thread can
        // read it torn-free while the WS callback appends (the project's standard for shared UI state).
        private ImmutableArray<PlateSolveRecord> _plateSolveHistory = [];

        // Last observed guider state string, so a change can be surfaced as GuiderStateChanged until
        // the node broadcasts one itself. Only touched by the poll loop.
        private string? _lastGuiderState;

        // Consecutive polls the node has not answered, which is what NextPollInterval backs off on.
        // Only touched by the poll loop.
        private int _consecutiveFailures;
        private SessionPhase _lastPhase = SessionPhase.NotStarted;

        // Identity of the prompt already raised locally, so the poll (which sees the same outstanding
        // prompt on every tick until it is answered) raises it exactly once. Only touched by the poll
        // loop. Cleared when the node reports no prompt, so a later prompt with identical wording -- the
        // same panel, the next filter -- is raised again rather than swallowed as a duplicate.
        private string? _raisedPromptKey;

        // The completion of the prompt raised locally, so it can be WITHDRAWN when the node stops offering
        // it (answered elsewhere, or its run moved on): a local prompt bar drops it on Settled rather than go
        // on asking. Poll loop only, like the key.
        private TaskCompletionSource<bool>? _raisedPromptCompletion;

        // The node's frames, one slot per OTA, and the number each slot holds. Published by reference swap: the
        // poll loop fetches off the render thread and the render thread reads the array per frame. A replaced
        // frame is released only once its successor is published, so a reader's lease always finds one.
        private Image?[] _previews = [];
        private int?[] _previewFrameNumbers = [];
        private Image? _guidePreview;
        private int? _guidePreviewFrameNumber;

        // One reader for every source: it keeps the planes of a released frame by shape and reads the next frame
        // of that shape into them, so a mirror showing frame after frame allocates no plane after the first two.
        private readonly FrameReader _frameReader = new FrameReader();

        /// <summary>How many planes the mirror's reader holds ready for the next frame (a test's view of the recycling).</summary>
        internal int FreeFramePlanes => _frameReader.FreePlanes;

        private CancellationTokenSource? _cts;
        private Task? _pollLoop;

        // The session's histories as polled so far, each mapped once from the wire (P5b part 7): the node sends only what
        // the cursor says is new, and it is appended here. Written by the poll loop, read by the render thread, both
        // through ONE reference, so a frame never reads one history from before a poll and another from after it.
        private Histories _histories = Histories.None;

        /// <summary>
        /// A session's histories held, the session they are of, and the number of the next guide step wanted (P5b part 7).
        /// </summary>
        internal sealed record Histories(
            Guid? SessionId,
            ImmutableArray<ExposureLogEntry> ExposureLog,
            ImmutableArray<FocusRunRecord> FocusHistory,
            ImmutableArray<CoolingSample> CoolingSamples,
            ImmutableArray<PhaseTimestamp> PhaseTimeline,
            ImmutableArray<GuideErrorSample> GuideSamples,
            long NextGuideStep)
        {
            public static Histories None { get; } = new Histories(null, [], [], [], [], [], 0);

            /// <summary>Where the client's copy ends, for the next poll to name; null while it holds no session's.</summary>
            public SessionStateCursor? Cursor => SessionId is { } id
                ? new SessionStateCursor(id, ExposureLog.Length, FocusHistory.Length, CoolingSamples.Length, PhaseTimeline.Length, NextGuideStep)
                : null;

            /// <summary>
            /// <paramref name="held"/> continued with a polled state: each history appended where the node says its part
            /// starts, when that is where the copy held ends and the state is of the same session; else the part sent is
            /// the history, and one that could not be continued (a part starting anywhere but the end held or the start)
            /// leaves no session named, so the next poll asks for everything. A node from before part 7 names no session
            /// and sends every history whole, which this takes as it comes.
            /// </summary>
            public static Histories Continue(Histories held, SessionStateDto state)
            {
                var from = state.HistoryFrom;
                var same = from is not null && state.SessionId is { } id && held.SessionId == id;
                var intact = true;

                ImmutableArray<T> Next<TDto, T>(ImmutableArray<T> have, int start, ImmutableArray<TDto> sent, Func<TDto, T> map)
                {
                    var part = Map(sent, map);
                    if (same && start == have.Length)
                    {
                        return have.AddRange(part);
                    }
                    if (from is not null && start != 0)
                    {
                        intact = false;
                    }
                    return part;
                }

                var exposureLog = Next(held.ExposureLog, from?.ExposureLog ?? 0, state.ExposureLog, ToExposure);
                var focusHistory = Next(held.FocusHistory, from?.FocusHistory ?? 0, state.FocusHistory, ToFocusRun);
                var cooling = Next(held.CoolingSamples, from?.CoolingSamples ?? 0, state.CoolingSamples, ToCooling);
                var phases = Next(held.PhaseTimeline, from?.PhaseTimeline ?? 0, state.PhaseTimeline, ToPhase);

                // The guide steps are a window of the latest, like the session's own ring: appended when they follow what
                // is held (or follow a gap, when this client fell behind the ring), and trimmed to the ring's size.
                var steps = Map(state.Guider?.RecentSteps ?? [], ToGuideStep);
                var guideFrom = from?.GuideSteps ?? 0;
                var guide = same && guideFrom >= held.NextGuideStep ? held.GuideSamples.AddRange(steps) : steps;
                if (guide.Length > ISessionTelemetry.GuideSampleCapacity)
                {
                    guide = guide.RemoveRange(0, guide.Length - ISessionTelemetry.GuideSampleCapacity);
                }

                return new Histories(intact ? state.SessionId : null, exposureLog, focusHistory, cooling, phases, guide, guideFrom + steps.Length);
            }

            private static ImmutableArray<T> Map<TDto, T>(ImmutableArray<TDto> sent, Func<TDto, T> map)
            {
                if (sent.IsDefaultOrEmpty)
                {
                    return [];
                }

                var builder = ImmutableArray.CreateBuilder<T>(sent.Length);
                foreach (var item in sent)
                {
                    builder.Add(map(item));
                }
                return builder.MoveToImmutable();
            }

            private static ExposureLogEntry ToExposure(ExposureLogDto e) => new ExposureLogEntry(
                e.Timestamp, e.TargetName, e.FilterName,
                TimeSpan.FromSeconds(e.ExposureSeconds), e.FrameNumber, JsonNumber.FromWire(e.MedianHfd), e.StarCount);

            private static FocusRunRecord ToFocusRun(FocusRunDto run) => new FocusRunRecord(
                run.Timestamp, run.OtaName, run.FilterName, run.BestPosition, JsonNumber.FromWire(run.BestHfd),
                ToCurve(run.Curve), JsonNumber.FromWire(run.FitA), JsonNumber.FromWire(run.FitB));

            private static CoolingSample ToCooling(CoolingSampleDto s) => new CoolingSample(
                s.Timestamp, s.CameraIndex, JsonNumber.FromWire(s.TemperatureC), JsonNumber.FromWire(s.SetpointTemperatureC),
                JsonNumber.FromWire(s.CoolerPowerPercent));

            private static PhaseTimestamp ToPhase(PhaseTimestampDto pt) => new PhaseTimestamp(pt.Phase, pt.StartTime);

            private static GuideErrorSample ToGuideStep(GuideStepDto step) => new GuideErrorSample(
                step.Timestamp, JsonNumber.FromWire(step.RaError), JsonNumber.FromWire(step.DecError),
                JsonNumber.FromWire(step.RaCorrectionMs), JsonNumber.FromWire(step.DecCorrectionMs), step.IsDither, step.IsSettling);
        }

        // The node's token for each frame source, from the state and from FRAME-AVAILABLE, whichever came last (P5b part
        // 6): a source is fetched only when its token is not the one its slot holds. Written by the poll loop and by the
        // socket's thread, so concurrent.
        private readonly ConcurrentDictionary<string, int> _frameTokens = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        // Whether the node sends those tokens at all: false for a node older than part 6, whose frames are asked for on
        // every poll, as they were. Only touched by the poll loop.
        private volatile bool _nodeSendsFrameTokens;

        // What the events since the last pass ask of the next one: a state poll, or only a frame (P5b part 6). Raised on the
        // socket's thread, taken by the poll loop; the wake is completed and replaced on every raise, so a raise after the
        // loop looked is never slept past.
        private int _statePending;
        private int _framesPending;
        private TaskCompletionSource _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public RemoteSessionMirror(
            TianWenNodeClient client,
            TianWenEventStream events,
            ITimeProvider timeProvider,
            ILogger logger)
        {
            _client = client;
            _events = events;
            _timeProvider = timeProvider;
            _logger = logger;
            _events.EventReceived += OnNodeEvent;
            _events.ConnectedChanged += OnEventStreamConnectedChanged;
        }

        /// <summary>
        /// Whether the node is answering, and when it last did (P5b part 6): connecting until the first poll comes back,
        /// then as the latest poll found it. One rule for every view of a rig: its Home card, its Live Session and its
        /// Guider tab.
        /// </summary>
        public NodeContact Contact => new NodeContact((NodeContactState)Volatile.Read(ref _contactState), LastContactUtc);

        // The contact state, written by the poll loop and read by the render thread.
        private int _contactState = (int)NodeContactState.Connecting;

        /// <summary>
        /// The node's notes, oldest first as its ring keeps them (P5b part 6): the ring as it stood when it was fetched, then
        /// each <c>NOTIFICATION</c> it pushed. The ring is fetched when the node first answers and again after the socket
        /// reconnects, since a note pushed while it was down is in the ring and nowhere else.
        /// </summary>
        public ImmutableArray<NotificationDto> Notes => _notes;

        // Replaced whole by the poll loop (the ring) and the socket's thread (a push), never mutated: one reference, so a
        // read is never torn.
        private ImmutableArray<NotificationDto> _notes = [];

        /// <summary>The most notes kept: the node's own ring holds no more.</summary>
        internal const int MaxNotes = 500;

        // Whether the ring is to be fetched on the node's next answer: at the start, and after the socket reconnects.
        // Raised on the socket's thread, taken by the poll loop.
        private int _ringDue = 1;

        /// <summary>The socket (re)connected: the ring is read again at the node's next answer. <c>internal</c> so a test can
        /// raise it without a socket.</summary>
        internal void OnEventStreamConnectedChanged(object? sender, bool connected)
        {
            if (connected)
            {
                Interlocked.Exchange(ref _ringDue, 1);
            }
        }

        /// <summary>
        /// True once a poll has returned a session. False both while the node is unreachable and while
        /// it simply has no session running, which the two properties below separate.
        /// </summary>
        public bool HasSession => Volatile.Read(ref _snapshot) is not null;

        /// <summary>Whether the node answered the most recent poll at all. A UI shows "offline" on
        /// false and "idle" on true-with-no-session; conflating them would report a powered-off rig as
        /// idle all night.</summary>
        public bool IsNodeReachable { get; private set; }

        /// <summary>Error text from the last failed poll, for the UI to surface verbatim.</summary>
        public string? LastError { get; private set; }

        /// <summary>
        /// When the node last actually answered, or null if it never has this run.
        /// <para>
        /// This is the live truth behind "offline, last seen ...", and it needs no persistence to be
        /// right: a rig that dies mid-watch keeps its connection, so the UI reads the real last-contact
        /// time from here. <see cref="RemoteRigBinding.LastSeenUtc"/> exists only to answer the same
        /// question across a restart, when this instance is gone.
        /// </para>
        /// <para>
        /// Written by the poll loop and read from the render thread, so it is stored as UTC <b>ticks in
        /// a long</b> (0 = never) rather than as the <c>DateTimeOffset?</c> it presents. A nullable
        /// <c>DateTimeOffset</c> is ~16 bytes: well over pointer size, so an unguarded assignment can
        /// tear and a reader could see one field's ticks with another's flag.
        /// </para>
        /// </summary>
        public DateTimeOffset? LastContactUtc
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastContactTicks);
                return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        private long _lastContactTicks;

        /// <summary>
        /// How far this computer's clock is ahead of the node's, measured as each state arrives (P5b part 3). A camera's
        /// exposure start is on the node's clock and a countdown subtracts it from this one's, so the start is moved by it.
        /// </summary>
        private long _clockSkewTicks;

        private void StampContact() =>
            Interlocked.Exchange(ref _lastContactTicks, _timeProvider.GetUtcNow().UtcTicks);

        /// <summary>Whether the push stream is currently attached (telemetry is still correct without
        /// it, just up to one poll interval behind).</summary>
        public bool IsEventStreamConnected => _events.IsConnected;

        /// <summary>Tells the node this client can see a prompt; from the host's drawing loop (<see cref="TianWenEventStream.Beat"/>).</summary>
        public void Beat() => _events.Beat();

        /// <summary>Starts polling and attaches the event stream. Idempotent.</summary>
        public void Start(CancellationToken cancellationToken)
        {
            if (_pollLoop is not null)
            {
                return;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _events.Start(_cts.Token);
            _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            _events.EventReceived -= OnNodeEvent;
            _events.ConnectedChanged -= OnEventStreamConnectedChanged;

            if (_cts is { } cts)
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }

            if (_pollLoop is { } loop)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on our own cancellation.
                }
                _pollLoop = null;
            }

            await _events.DisposeAsync().ConfigureAwait(false);
            _cts?.Dispose();
            _cts = null;

            // The poll loop has stopped, so nothing publishes a frame any more: give back what is held.
            DropFrames();
        }

        // -----------------------------------------------------------------------------------------
        // Driving the rig
        //
        // Deliberately NOT on ISessionTelemetry, which is a read-only observation contract that a
        // local Session also implements -- putting start/abort there would imply any telemetry source
        // can be commanded. These are mirror-specific, so a caller has to hold a RemoteSessionMirror
        // (i.e. know it is driving a rig) to reach them.
        //
        // Every one of them is a bare pass-through to the node plus a log line. The node applies its
        // own rules -- 409 while a session is running, its ProfileSwitchGate, its device ownership --
        // and its refusal text is surfaced verbatim rather than second-guessed here. A client that
        // pre-judged would eventually disagree with the rig about the rig's own state.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Pushes a planner-built schedule, then starts the run. Two calls rather than one because the
        /// node keeps them separate: <c>/session/start</c> drains whatever schedule is pending.
        /// <para>
        /// A caller with a real plan must come through here and not through the target queue --
        /// <c>PendingTarget</c> drops the per-filter plan, the altitude-optimised start time and
        /// <c>AcrossMeridian</c>, and start would stamp <c>Start = now</c> over the result.
        /// </para>
        /// <para>
        /// <paramref name="configuration"/> is the configuration the run uses, sent whole; null runs on the
        /// node's declared defaults, never on a partial one.
        /// </para>
        /// </summary>
        public async Task<NodeResult<string>> StartAsync(
            ScheduledObservationDto[] schedule, Guid? profileId, SessionConfigApiDto? configuration, CancellationToken cancellationToken)
        {
            if (schedule.Length > 0)
            {
                var pushed = await _client.SetScheduleAsync(schedule, cancellationToken).ConfigureAwait(false);
                if (!pushed.IsSuccess)
                {
                    // Do NOT start anyway: a start after a failed push would run the node's own stale or
                    // empty schedule, which looks like success and images the wrong thing all night.
                    _logger.LogWarning("Pushing {Count} observations to {Node} failed: {Error}",
                        schedule.Length, _client.BaseAddress, pushed.Error);
                    return pushed;
                }
            }

            _logger.LogInformation("Starting a session on {Node} with {Count} scheduled observation(s)",
                _client.BaseAddress, schedule.Length);
            return await _client.StartSessionAsync(profileId, configuration, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Starts an on-demand flat run on the node.</summary>
        public Task<NodeResult<string>> StartFlatsAsync(FlatsRequestDto request, Guid? profileId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting a flat run on {Node}", _client.BaseAddress);
            return _client.StartFlatsAsync(request, profileId, cancellationToken);
        }

        /// <summary>Aborts the node's running session. Its finaliser still runs (park, warm, close).</summary>
        public Task<NodeResult<string>> AbortAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Aborting the session on {Node}", _client.BaseAddress);
            return _client.AbortSessionAsync(cancellationToken);
        }

        /// <summary>Clears any schedule pushed but not yet started.</summary>
        public Task<NodeResult<string>> ClearScheduleAsync(CancellationToken cancellationToken) =>
            _client.ClearScheduleAsync(cancellationToken);

        /// <summary>The node's own notification ring -- what it recorded, including anything that
        /// happened before this mirror attached.</summary>
        public Task<NodeResult<NotificationDto[]>> GetNotificationsAsync(CancellationToken cancellationToken) =>
            _client.GetNotificationsAsync(cancellationToken);

        /// <summary>The node's devices with live connected state, for a remote Equipment view.</summary>
        public Task<NodeResult<DeviceDto[]>> GetDevicesAsync(CancellationToken cancellationToken) =>
            _client.GetDevicesAsync(cancellationToken);

        // -----------------------------------------------------------------------------------------
        // Poll loop
        // -----------------------------------------------------------------------------------------

        private async Task PollLoopAsync(CancellationToken cancellationToken)
        {
            var pollState = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (pollState)
                    {
                        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await RefreshFramesAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("Remote session poll for {Node} cancelled", _client.BaseAddress);
                    break;
                }

                pollState = await WaitForNextPassAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits out the poll interval, or less when an event asks for a pass sooner (P5b part 6). True when the next pass
        /// polls the state (the interval ran out, or an event said the state changed); false when it only fetches the
        /// frames an event named.
        /// </summary>
        internal async Task<bool> WaitForNextPassAsync(CancellationToken cancellationToken)
        {
            // The wake is taken BEFORE the flags are looked at, so an event raised in between completes this one.
            var wake = Volatile.Read(ref _wake).Task;
            if (TakePendingPass() is { } pending)
            {
                return pending;
            }

            using var sleeping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sleep = _timeProvider.SleepAsync(NextPollInterval(), sleeping.Token).AsTask();
            if (await Task.WhenAny(sleep, wake).ConfigureAwait(false) == sleep)
            {
                // Ran out, or the mirror is stopping, which this rethrows. A state poll is due either way, and it fetches
                // the frames too.
                await sleep.ConfigureAwait(false);
                TakePendingPass();
                return true;
            }

            // Woken: the sleep is not wanted any more.
            await sleeping.CancelAsync().ConfigureAwait(false);
            return TakePendingPass() ?? true;
        }

        /// <summary>What the events since the last pass ask for: a state poll (true), only frames (false), or nothing.</summary>
        private bool? TakePendingPass()
        {
            if (Interlocked.Exchange(ref _statePending, 0) == 1)
            {
                // A state poll fetches the frames too.
                Interlocked.Exchange(ref _framesPending, 0);
                return true;
            }
            return Interlocked.Exchange(ref _framesPending, 0) == 1 ? false : null;
        }

        /// <summary>Asks the poll loop for a pass now: a state poll, or only the frames.</summary>
        private void Wake(bool state)
        {
            if (state)
            {
                Interlocked.Exchange(ref _statePending, 1);
            }
            else
            {
                Interlocked.Exchange(ref _framesPending, 1);
            }
            Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        /// <summary>
        /// A frames-only pass, for a <c>FRAME-AVAILABLE</c> that came between two polls: the frames the node now shows, read
        /// against the state last polled, or for a node running no session, against its view's telescopes
        /// (<see cref="IdleOtaCount"/>). Nothing while the node is not answering.
        /// </summary>
        internal async Task RefreshFramesAsync(CancellationToken cancellationToken)
        {
            if (!IsNodeReachable)
            {
                return;
            }
            await RefreshPreviewsAsync(Snapshot, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// How many telescopes the node's view shows while the node runs no session, whose frames it then fetches: a preview
        /// exposure, a solve's frame, the last frames of a session that ended (P6 of docs/plans/hardware-in-the-server.md,
        /// #936; the node shows each OTA's latest, <c>NodeFrames</c>). A node with no session reports no cameras, so the count
        /// is the view's, from its profile; zero fetches none, which is also what a view that never sets it gets.
        /// </summary>
        public int IdleOtaCount
        {
            get => Volatile.Read(ref _idleOtaCount);
            set => Volatile.Write(ref _idleOtaCount, Math.Max(0, value));
        }

        private int _idleOtaCount;

        /// <summary>
        /// How long to wait before the next poll: the live cadence while the rig is answering, doubling up
        /// to <see cref="MaxUnreachablePollInterval"/> while it is not.
        /// <para>
        /// Derived from the consecutive-failure count rather than from a stored interval, so recovery needs
        /// no reset step -- one successful poll clears the count and the very next wait is back to the live
        /// cadence. Internal so a test can assert the curve without running a real clock.
        /// </para>
        /// </summary>
        internal TimeSpan NextPollInterval()
        {
            var baseInterval = HasSession ? ActivePollInterval : IdlePollInterval;
            if (_consecutiveFailures == 0)
            {
                return baseInterval;
            }

            // Doubling from the base, capped. Shifting by the failure count directly would overflow after
            // ~60 dark polls (about two minutes), so the cap is applied to the count first.
            var doublings = Math.Min(_consecutiveFailures, 16);
            var scaled = baseInterval * (1L << doublings);
            return scaled < MaxUnreachablePollInterval ? scaled : MaxUnreachablePollInterval;
        }

        /// <summary>One poll cycle. Internal so a test can step it with a fake clock.</summary>
        internal async Task PollOnceAsync(CancellationToken cancellationToken)
        {
            var result = await _client.GetSessionStateAsync(Volatile.Read(ref _histories).Cursor, cancellationToken).ConfigureAwait(false);
            var contactBefore = (NodeContactState)Volatile.Read(ref _contactState);

            if (result is { IsSuccess: true, Value: { } state })
            {
                IsNodeReachable = true;
                LastError = null;
                _consecutiveFailures = 0;
                StampContact();
                Volatile.Write(ref _contactState, (int)NodeContactState.Answering);
                Volatile.Write(ref _clockSkewTicks, state.NodeNowUtc is { } nodeNow ? (_timeProvider.GetUtcNow() - nodeNow).Ticks : 0);
                _nodeSendsFrameTokens = state.Frames is not null;
                foreach (var token in state.Frames ?? [])
                {
                    _frameTokens[token.Source] = token.Number;
                }
                // The histories first, then the snapshot: a reader that sees the new state finds its histories already there.
                Volatile.Write(ref _histories, Histories.Continue(Volatile.Read(ref _histories), state));
                Volatile.Write(ref _snapshot, state);
                RaiseDerivedEvents(state);
                RaiseChanged();
                await RefreshPreviewsAsync(state, cancellationToken).ConfigureAwait(false);
                // After the state and its frames are out: the ring is the node's history, which a view can wait for.
                await RefreshNotesIfDueAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (result.IsNotFound)
            {
                // The node is up and idle. Drop the stale snapshot so the UI stops rendering a session
                // that has ended, and reset the change-detection baselines with it.
                IsNodeReachable = true;
                LastError = null;
                // A 404 is the node ANSWERING, so it resets the backoff -- an idle rig is a healthy rig.
                _consecutiveFailures = 0;
                StampContact(); // a 404 is the node answering -- "seen" is about the node, not the session
                Volatile.Write(ref _contactState, (int)NodeContactState.Answering);
                var ended = Interlocked.Exchange(ref _snapshot, null) is not null;
                Volatile.Write(ref _histories, Histories.None);
                _lastGuiderState = null;
                _lastPhase = SessionPhase.NotStarted;
                WithdrawRaisedPrompt();
                // The frames the node still shows (its previews, an ended session's last), never blanked for the session's end.
                await RefreshPreviewsAsync(null, cancellationToken).ConfigureAwait(false);
                await RefreshNotesIfDueAsync(cancellationToken).ConfigureAwait(false);
                if (ended || contactBefore is not NodeContactState.Answering)
                {
                    RaiseChanged();
                }
                return;
            }

            // Unreachable: keep the last snapshot. A brief network blip should leave the last known
            // state on screen (flagged stale via IsNodeReachable and Contact) rather than blanking the tab.
            IsNodeReachable = false;
            LastError = result.Error;
            Volatile.Write(ref _contactState, (int)NodeContactState.NotAnswering);
            if (contactBefore is not NodeContactState.NotAnswering)
            {
                RaiseChanged();
            }
            if (_consecutiveFailures < int.MaxValue)
            {
                _consecutiveFailures++;
            }
        }

        /// <summary>
        /// Fetches the node's notification ring when it is due (<see cref="Notes"/>), and merges it with what was pushed. A
        /// ring that could not be read is due again at the next answer: a feed missing its start would read as a node that
        /// said nothing before this client attached.
        /// </summary>
        private async Task RefreshNotesIfDueAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _ringDue, 0) == 0)
            {
                return;
            }

            var ring = await _client.GetNotificationsAsync(cancellationToken).ConfigureAwait(false);
            if (ring is { IsSuccess: true, Value: { } notes })
            {
                if (MergeNotes(notes))
                {
                    RaiseChanged();
                }
                return;
            }

            _logger.LogDebug("Reading the notification ring of {Node} failed: {Error}", _client.BaseAddress, ring.Error);
            Interlocked.Exchange(ref _ringDue, 1);
        }

        /// <summary>
        /// Merges notes into <see cref="Notes"/>, the ring's and the pushes' alike: one of each note (a note is its instant,
        /// severity and wording), oldest first, the newest <see cref="MaxNotes"/> kept. True when a note was new.
        /// </summary>
        internal bool MergeNotes(IReadOnlyCollection<NotificationDto> incoming) =>
            ImmutableInterlocked.Update(ref _notes, static (held, incoming) =>
            {
                var seen = new HashSet<(DateTimeOffset, string, string)>(held.Length + incoming.Count);
                foreach (var note in held)
                {
                    seen.Add((note.TimestampUtc, note.Severity, note.Message));
                }

                List<NotificationDto>? added = null;
                foreach (var note in incoming)
                {
                    if (seen.Add((note.TimestampUtc, note.Severity, note.Message)))
                    {
                        (added ??= []).Add(note);
                    }
                }
                if (added is null)
                {
                    // Nothing new: the same array, so Update reports no change.
                    return held;
                }

                // OrderBy is stable, so two notes of one instant keep the order they came in.
                ImmutableArray<NotificationDto> merged = [.. held.AddRange(added).OrderBy(static n => n.TimestampUtc)];
                if (merged.Length > MaxNotes)
                {
                    merged = merged.RemoveRange(0, merged.Length - MaxNotes);
                }

                // Notes older than a full feed keeps come back with every read of the ring and are trimmed straight off
                // again: that is nothing new, and the same array says so.
                return merged.SequenceEqual(held) ? held : merged;
            }, incoming);

        /// <summary>
        /// Whether to fetch the node's frames at all. Off by default: a mirror is often attached just to
        /// watch phase and counters (a multi-rig dashboard), and full-resolution frames are by far the most
        /// expensive thing on the link. The GUI and the TUI ask only while the rig is on screen
        /// (<c>ViewContexts.PollAll</c>), and a mirror asked for none gives back what it holds.
        /// </summary>
        public PreviewOptions? Previews { get; set; }

        /// <summary>
        /// Pulls each OTA's latest frame, naming the one the mirror already holds, which the node answers with a 204
        /// and no body while its slot still shows it.
        /// <para>
        /// Runs on the poll loop, so reading a frame never touches the render thread. Frames are fetched
        /// sequentially rather than in parallel: a multi-OTA rig is normally on the far end of a home
        /// LAN or a VPN, and N concurrent full frames would spike latency for the state poll that
        /// everything else depends on.
        /// </para>
        /// </summary>
        /// <param name="state">The state last polled; null for a node running no session, whose view's telescopes
        /// (<see cref="IdleOtaCount"/>) are fetched instead, and which shows no guide frame.</param>
        private async Task RefreshPreviewsAsync(SessionStateDto? state, CancellationToken cancellationToken)
        {
            if (Previews is not { } options)
            {
                // Not asked for (a rig off screen): hold nothing, so a board of rigs costs no frame memory either.
                DropFrames();
                return;
            }

            var cameras = state is null ? IdleOtaCount : state.Cameras.IsDefaultOrEmpty ? 0 : state.Cameras.Length;
            var otaCount = options.IncludeOtas ? cameras : 0;
            if (otaCount == 0)
            {
                ClearPreviews();
                await RefreshGuidePreviewAsync(state, options, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Copied on the first change only, and never mutated in place: the render thread may be reading the
            // published array right now. A rig whose OTA count changed starts from empty slots.
            var held = Volatile.Read(ref _previews);
            var heldNumbers = _previewFrameNumbers;
            Image?[]? images = null;
            int?[]? numbers = null;
            List<Image>? replaced = null;
            if (held.Length != otaCount)
            {
                images = new Image?[otaCount];
                numbers = new int?[otaCount];
                replaced = [.. Held(held)];
            }

            for (var i = 0; i < otaCount; i++)
            {
                var source = FrameSources.Ota(i);
                var heldNumber = (numbers ?? heldNumbers)[i];
                if (!HasNewFrame(source, heldNumber, out var token))
                {
                    continue;
                }
                if (await FetchFrameAsync(source, heldNumber, token, cancellationToken).ConfigureAwait(false) is not { } frame)
                {
                    continue;
                }

                images ??= (Image?[])held.Clone();
                numbers ??= (int?[])heldNumbers.Clone();
                if (images[i] is { } old)
                {
                    (replaced ??= []).Add(old);
                }
                images[i] = frame.Image;
                numbers[i] = frame.Number;
            }

            if (images is not null && numbers is not null)
            {
                _previewFrameNumbers = numbers;
                Volatile.Write(ref _previews, images);
                RaiseChanged();
            }

            // Only now that their successors are published: a reader that read the old array leases before this,
            // or is refused and reads the new one.
            foreach (var image in replaced ?? [])
            {
                image.Release();
            }

            await RefreshGuidePreviewAsync(state, options, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Whether the node shows a frame on <paramref name="source"/> other than the one held (<paramref name="held"/>),
        /// by the source's token (P5b part 6): a slot is asked for only once there is something new in it. Always true from
        /// a node that sends no tokens, which is asked every poll, naming the frame held, as it was before.
        /// </summary>
        private bool HasNewFrame(string source, int? held, out int token)
        {
            if (!_nodeSendsFrameTokens)
            {
                token = 0;
                return true;
            }

            // A token of 0 is a source that has never shown a frame.
            token = _frameTokens.TryGetValue(source, out var known) ? known : 0;
            return token != (held ?? 0);
        }

        /// <summary>
        /// The frame <paramref name="source"/> shows, when it is not the one held (<paramref name="held"/>); null when
        /// there is nothing new: the same frame, no frame yet, or a failed fetch.
        /// <para>
        /// Asked for because the source's token was <paramref name="token"/>: when the node answers that it shows the frame
        /// held after all, or none, the token is put back to the one held, unless another has come in since, so an older
        /// token (a state polled before the event that brought the frame held) is not asked about again on every pass.
        /// </para>
        /// </summary>
        private async Task<(Image Image, int? Number)?> FetchFrameAsync(string source, int? held, int token, CancellationToken cancellationToken)
        {
            var result = await _client.GetLatestFrameAsync(source, held, _frameReader, cancellationToken).ConfigureAwait(false);
            if (result.Error is { } error)
            {
                // A frame failure must never blank the telemetry: keep the last frame and let the state poll go on
                // reporting. Debug, not Warning -- a link too slow for frames would otherwise flood the log every poll.
                _logger.LogDebug("Frame fetch for {Source} on {Node} failed: {Error}", source, _client.BaseAddress, error);
                return null;
            }

            if (result.HasImage)
            {
                return (result.Image, result.FrameNumber);
            }

            _frameTokens.TryUpdate(source, held ?? 0, token);
            return null;
        }

        private static IEnumerable<Image> Held(Image?[] images)
        {
            foreach (var image in images)
            {
                if (image is not null)
                {
                    yield return image;
                }
            }
        }

        /// <summary>
        /// Pulls the guide-camera frame, after the OTA previews: the science frames are what the operator is
        /// judging the night by, so if the link only has room for some of this, the guide picture is the
        /// part that can wait a poll.
        /// <para>
        /// Gated on <see cref="PreviewOptions.IncludeGuider"/> AND on the node reporting a guider at all,
        /// so a rig guiding through nothing (or a PHD2 setup, which serves no frames) costs no request per
        /// poll rather than a 404 per poll.
        /// </para>
        /// </summary>
        private async Task RefreshGuidePreviewAsync(
            SessionStateDto? state, PreviewOptions options, CancellationToken cancellationToken)
        {
            if (!options.IncludeGuider || state is null)
            {
                // Not asked for, or no session: the guide camera shows frames only in a session's guide loop.
                DropGuideFrame();
                return;
            }

            if (state.Guider is null || !HasNewFrame(FrameSources.Guider, _guidePreviewFrameNumber, out var token))
            {
                return;
            }

            if (await FetchFrameAsync(FrameSources.Guider, _guidePreviewFrameNumber, token, cancellationToken).ConfigureAwait(false)
                is not { } frame)
            {
                return;
            }

            _guidePreviewFrameNumber = frame.Number;
            // Published, THEN the frame it replaces released, as for the OTA slots.
            Interlocked.Exchange(ref _guidePreview, frame.Image)?.Release();
            RaiseChanged();
        }

        /// <summary>Gives back every frame held, the OTA slots and the guide frame.</summary>
        private void DropFrames()
        {
            ClearPreviews();
            DropGuideFrame();
        }

        private void DropGuideFrame()
        {
            _guidePreviewFrameNumber = null;
            Interlocked.Exchange(ref _guidePreview, null)?.Release();
        }

        /// <summary>Empties the OTA slots, releasing each frame once the empty array is published.</summary>
        private void ClearPreviews()
        {
            if (Volatile.Read(ref _previews).Length == 0)
            {
                return;
            }

            _previewFrameNumbers = [];
            foreach (var image in Held(Interlocked.Exchange(ref _previews, [])))
            {
                image.Release();
            }
        }

        /// <summary>
        /// The events the poll owns, from the difference between consecutive states: a phase, the guider's state and a
        /// prompt. The node broadcasts each of them too, and those pushes only make the mirror poll at once
        /// (<see cref="Dispatch"/>), so an event seen both ways, or only by polling after a dropped frame, is raised once.
        /// </summary>
        private void RaiseDerivedEvents(SessionStateDto state)
        {
            if (state.Phase != _lastPhase)
            {
                var old = _lastPhase;
                _lastPhase = state.Phase;
                PhaseChanged?.Invoke(this, new SessionPhaseChangedEventArgs(old, state.Phase));
            }

            var guiderState = state.Guider?.State;
            if (!string.Equals(guiderState, _lastGuiderState, StringComparison.Ordinal))
            {
                var old = _lastGuiderState;
                _lastGuiderState = guiderState;
                if (guiderState is not null)
                {
                    GuiderStateChanged?.Invoke(this, new GuiderStateChangedEventArgs(old, guiderState));
                }
            }

            RaisePromptIfNew(state.PendingPrompt);
        }

        /// <summary>
        /// Raises <see cref="PromptRequested"/> the first time a given prompt is seen, wiring its
        /// <c>Respond</c> to <c>POST /session/prompt/respond</c>.
        /// </summary>
        private void RaisePromptIfNew(PendingPromptDto? pending)
        {
            if (pending is null)
            {
                WithdrawRaisedPrompt();
                return;
            }

            // NUL as the separator, written as an escape rather than a literal byte: a raw NUL in
            // the source makes the whole file read as BINARY to grep/ripgrep, which silently hides
            // it from every code search. Neither a title nor a message can contain one, so the
            // join stays unambiguous either way.
            var key = $"{pending.Title}\0{pending.Message}";
            if (string.Equals(key, _raisedPromptKey, StringComparison.Ordinal))
            {
                return;
            }

            // A different prompt replaces the one raised before, which the node no longer offers.
            WithdrawRaisedPrompt();
            _raisedPromptKey = key;

            if (_promptRequested is not { } handler)
            {
                // Nobody is listening. Deliberately do NOT answer on the node's behalf: the node already
                // resolved that question for itself before ever broadcasting (it answers its own
                // DefaultIfUnanswerable when no observer is attached). A second opinion from here would
                // be a client fabricating a decision about hardware it cannot see.
                _logger.LogDebug("Remote prompt '{Title}' from {Node} has no local handler", pending.Title, _client.BaseAddress);
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _raisedPromptCompletion = completion;
            handler(this, new SessionPromptEventArgs(
                pending.Title,
                pending.Message,
                pending.ContinueLabel,
                pending.CancelLabel,
                completion,
                pending.RequiresPhysicalPresence,
                // The node owns the unattended policy and has already applied it if it was going to; a
                // prompt that reached us is one it decided to hold. Nothing here should re-derive it.
                defaultIfUnanswerable: false,
                // Passed straight through, null included. Substituting "now" would date the prompt from
                // when this client attached, so a rig stuck since dusk would read as freshly waiting on
                // every GUI restart -- the one number a board of rigs exists to make visible.
                raisedUtc: pending.RaisedUtc));

            _ = ForwardPromptAnswerAsync(completion.Task, pending.Title);
        }

        /// <summary>
        /// Withdraws the prompt raised locally, if any: the node no longer offers it, so a local prompt bar
        /// drops it (<see cref="SessionPromptEventArgs.Settled"/>) and nothing is forwarded. It used to stay up,
        /// inviting an answer to a question the node's run had moved past (P0b item 13 of
        /// docs/plans/hardware-in-the-server.md, #752).
        /// </summary>
        private void WithdrawRaisedPrompt()
        {
            _raisedPromptKey = null;
            _raisedPromptCompletion?.TrySetCanceled();
            _raisedPromptCompletion = null;
        }

        private async Task ForwardPromptAnswerAsync(Task<bool> answer, string title)
        {
            bool proceed;
            try
            {
                proceed = await answer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Withdrawn: the node stopped offering it, so there is no answer to forward.
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local handler for remote prompt '{Title}' faulted; not answering", title);
                return;
            }

            // Its own token: the answer must reach the node even as this mirror is being torn down --
            // a UI answering "Cancel" and then closing the rig view is the ordinary way to decline, and
            // dropping the POST would leave the run held open.
            var result = await _client.RespondToPromptAsync(proceed, CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                // 404 here is benign and expected: the node resolved the prompt itself first (its last
                // observer dropped, or the run was aborted).
                _logger.LogInformation("Answering remote prompt '{Title}' with {Answer} was not accepted: {Error}",
                    title, proceed, result.Error);
            }
        }

        /// <summary>
        /// Push-event handler. <c>internal</c> so tests can feed a decoded event straight in: faking the
        /// socket itself would mean faking <see cref="System.Net.WebSockets.ClientWebSocket"/>, whose
        /// <c>ConnectAsync</c> is not virtual, and reflection is not an option in this repo.
        /// </summary>
        internal void OnNodeEvent(object? sender, WebSocketEventDto dto)
        {
            if (Dispatch(dto) is NodeEventKind.Unknown)
            {
                // A node newer than this client: nothing to do with it, and not an error.
                _logger.LogDebug("Event {Event} from {Node} is not one this mirror knows", dto.Event, _client.BaseAddress);
            }
        }

        /// <summary>What an event from the node is to this mirror (P5b part 6).</summary>
        internal enum NodeEventKind
        {
            /// <summary>The session's state changed: the mirror polls now, or at its tick for one as frequent as a guide step.</summary>
            StateChanged,

            /// <summary>A frame source shows a new frame: fetch it.</summary>
            FrameAvailable,

            /// <summary>Something the state does not carry happened: raised as its event.</summary>
            Occurrence,

            /// <summary>Another client's business: a device's state, a profile, a job, an enhance.</summary>
            NotTheSessions,

            /// <summary>An event this client does not know, from a newer node.</summary>
            Unknown,
        }

        /// <summary>
        /// Handles one event from the node and says which kind it was. Every event the node broadcasts is one of the first
        /// four kinds (<c>RemoteSessionMirrorEventTests</c> sends each one here), so none goes unhandled.
        /// </summary>
        internal NodeEventKind Dispatch(WebSocketEventDto dto)
        {
            switch (dto.Event)
            {
                case "SESSION-PHASE-CHANGED":
                case "GUIDER-STATE-CHANGED":
                case "PROMPT-REQUESTED":
                    // Deliberately NOT raised from the push: the poll owns PhaseChanged, GuiderStateChanged and
                    // PromptRequested (RaiseDerivedEvents), so a transition cannot be announced twice to subscribers that
                    // count them. The push makes the poll come now instead of at its tick.
                    Wake(state: true);
                    return NodeEventKind.StateChanged;

                case "FRAME-WRITTEN":
                    AppendFrame(dto);
                    Wake(state: true);
                    return NodeEventKind.StateChanged;

                case "GUIDE-STEP":
                    // The guide graph's samples are on the state, which the poll brings at its cadence: polling a whole
                    // state on every guide step is exactly the load part 7 takes off, so a step wakes nothing.
                    return NodeEventKind.StateChanged;

                case NodeWire.FrameAvailableEvent:
                    if (FrameAvailableDto.TryFromEvent(dto, out var frame))
                    {
                        _frameTokens[frame.Source] = frame.Number;
                        // Only a node that keeps tokens pushes them, and an idle one never answers a state to say so.
                        _nodeSendsFrameTokens = true;
                        Wake(state: false);
                    }
                    return NodeEventKind.FrameAvailable;

                case "PLATE-SOLVE-COMPLETED":
                    AppendPlateSolve(dto);
                    return NodeEventKind.Occurrence;

                case "SCOUT-COMPLETED":
                    RaiseScoutCompleted(dto);
                    return NodeEventKind.Occurrence;

                case "NOTIFICATION":
                    RaiseNoteReceived(dto);
                    return NodeEventKind.Occurrence;

                case NodeWire.DeviceStateEvent:
                case NodeWire.ProfileChangedEvent:
                case "JOB-PROGRESS":
                case "ENHANCE-PROGRESS":
                case "ENHANCE-COMPLETED":
                    // The device plane's, a profile's, a job's and an enhance's, each read by the client that asked for it.
                    NodeEventNotTheSessions?.Invoke(this, dto);
                    return NodeEventKind.NotTheSessions;

                default:
                    return NodeEventKind.Unknown;
            }
        }

        /// <summary>
        /// Raises <see cref="ScoutCompleted"/> for a scout the node's session finished, with the target it scouted, whole.
        /// Nothing is raised for a payload whose classification or outcome is not one this client knows: a scout reported
        /// as Healthy because its word was not understood would be a verdict nobody gave.
        /// </summary>
        private void RaiseScoutCompleted(WebSocketEventDto dto)
        {
            if (dto.Data is not { } data
                || !TryParseEnum<ScoutClassification>(data, "Classification", out var classification)
                || !TryParseEnum<ScoutOutcome>(data, "Outcome", out var outcome))
            {
                return;
            }

            var target = new Target(
                ReadDouble(data, "TargetRA") ?? double.NaN,
                ReadDouble(data, "TargetDec") ?? double.NaN,
                ReadString(data, "TargetName") ?? string.Empty,
                ReadULong(data, "CatalogIndex") is { } index ? (CatalogIndex)index : null);
            _scoutCompleted?.Invoke(this, new ScoutCompletedEventArgs(
                target,
                classification,
                ReadDouble(data, "EstimatedClearInSeconds") is { } clearIn ? TimeSpan.FromSeconds(clearIn) : null,
                outcome,
                ReadInts(data, "StarCountsPerOTA")));
        }

        /// <summary>Adds a note the node pushed to <see cref="Notes"/>, and raises <see cref="NoteReceived"/> for it, unless the
        /// feed has it already.</summary>
        private void RaiseNoteReceived(WebSocketEventDto dto)
        {
            if (dto.Data is not { } data
                || ReadString(data, "Message") is not { } message
                || ReadDateTimeOffset(data, "TimestampUtc") is not { } when)
            {
                return;
            }

            var note = new NotificationDto
            {
                Severity = ReadString(data, "Severity") ?? nameof(NotificationSeverity.Info),
                Message = message,
                TimestampUtc = when,
            };
            // One note once: a push the ring read already brought (the socket and the read raced) is not raised again.
            if (!MergeNotes([note]))
            {
                return;
            }
            NoteReceived?.Invoke(this, note);
            RaiseChanged();
        }

        /// <summary>Tells a view there is something new to draw.</summary>
        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// Raises <see cref="FrameWritten"/> for a frame the node just wrote.
        /// <para>
        /// The broadcast carries every field of an <see cref="ExposureLogEntry"/> except its timestamp,
        /// which is stamped with arrival time (within one network hop of the node's own). It is used only
        /// to fire the notification -- the <see cref="ExposureLog"/> collection itself comes from the
        /// polled snapshot, which carries the whole run rather than only what arrived after this mirror
        /// attached. Keeping a second event-sourced copy here would have to be reconciled against that
        /// one, for no gain beyond half a poll interval of latency.
        /// </para>
        /// </summary>
        private void AppendFrame(WebSocketEventDto dto)
        {
            if (dto.Data is not { } data)
            {
                return;
            }

            var entry = new ExposureLogEntry(
                Timestamp: _timeProvider.GetUtcNow(),
                TargetName: ReadString(data, "TargetName") ?? string.Empty,
                FilterName: ReadString(data, "FilterName") ?? string.Empty,
                Exposure: TimeSpan.FromSeconds(ReadDouble(data, "ExposureSeconds") ?? 0),
                FrameNumber: (int)(ReadDouble(data, "FrameNumber") ?? 0),
                MedianHfd: (float)(ReadDouble(data, "MedianHfd") ?? 0),
                StarCount: (int)(ReadDouble(data, "StarCount") ?? 0));

            FrameWritten?.Invoke(this, new FrameWrittenEventArgs(entry));
        }

        private void AppendPlateSolve(WebSocketEventDto dto)
        {
            if (dto.Data is not { } data)
            {
                return;
            }

            var record = new PlateSolveRecord(
                // The broadcast carries the solved centre but not the full WCS, so Solution stays null
                // (a consumer wanting the plate geometry has to solve locally). PlateSolveContext has
                // no "unknown" member, so an unrecognised context falls back to Centering -- the
                // overwhelmingly common case, and the only one a UI groups by.
                Timestamp: _timeProvider.GetUtcNow(),
                Context: ParseEnum(data, "Context", PlateSolveContext.Centering),
                OtaName: ReadString(data, "OtaName") ?? string.Empty,
                Succeeded: ReadBool(data, "Succeeded"),
                Solution: null,
                Elapsed: TimeSpan.FromMilliseconds(ReadDouble(data, "ElapsedMs") ?? 0),
                DetectedStars: (int)(ReadDouble(data, "DetectedStars") ?? 0),
                MatchedStars: (int)(ReadDouble(data, "MatchedStars") ?? 0));

            _plateSolveHistory = Append(_plateSolveHistory, record, MaxPlateSolveHistory);
            PlateSolveCompleted?.Invoke(this, new PlateSolveCompletedEventArgs(record));
            RaiseChanged();
        }

        // -----------------------------------------------------------------------------------------
        // ISessionTelemetry -- faithful projections of the snapshot
        // -----------------------------------------------------------------------------------------

        private SessionStateDto? Snapshot => Volatile.Read(ref _snapshot);

        public SessionPhase Phase => Snapshot?.Phase ?? SessionPhase.NotStarted;

        /// <summary>The node's run on the session it serves (P5b part 4); none with no session, or while the node answers idle.</summary>
        public ReportedRun? Run => Snapshot is not { } snapshot ? ReportedRun.NoSession : snapshot.Run switch
        {
            NodeRunKind.Session => ReportedRun.Session,
            NodeRunKind.Flats => ReportedRun.Flats,
            _ => ReportedRun.None,
        };

        public string? CurrentActivity => Snapshot?.CurrentActivity;

        public string? FailureReason => Snapshot?.FailureReason;

        public int TotalFramesWritten => Snapshot?.TotalFramesWritten ?? 0;

        public TimeSpan TotalExposureTime => TimeSpan.FromSeconds(Snapshot?.TotalExposureTimeSeconds ?? 0);

        public int CurrentObservationIndex => Snapshot?.CurrentObservationIndex ?? -1;

        public string? LastFramePath => Snapshot?.LastFramePath;

        /// <summary>
        /// Whether this mirror reaches its node over the machine's own socket, so a path the node names is a path
        /// here too. The creator says so from its transport (<c>NodeTransport.SocketPath is not null</c>); the default,
        /// false, is the safe answer.
        /// </summary>
        public bool IsOnThisMachine { get; init; }

        /// <summary>
        /// The last sub the node SAVED, as a file this machine can open: <see cref="LastFramePath"/> when the mirror
        /// reaches its node over the local socket (<see cref="IsOnThisMachine"/>), otherwise null. Read it through
        /// <see cref="Image.TryReadFitsFile(string, out Image?)"/>: the FITS file as the node wrote it, headers and all
        /// (the local half of "a saved frame is its FITS file", P4 of docs/plans/hardware-in-the-server.md). Over TCP
        /// the path names a file on ANOTHER machine, which a file of the same name here must never be taken for;
        /// fetching it is the remote half (P4r, deferred).
        /// </summary>
        public string? SavedFramePathOnThisMachine => IsOnThisMachine ? LastFramePath : null;

        public string MountDisplayName => Snapshot?.MountDisplayName ?? string.Empty;

        public MountState MountState
        {
            get
            {
                if (Snapshot?.Mount is not { } m)
                {
                    // Same "unknown" encoding a local session uses before its first device poll, so the
                    // reticle-suppression checks downstream behave identically.
                    return MountState.Unknown;
                }

                return new MountState(
                    JsonNumber.FromWire(m.RightAscension),
                    JsonNumber.FromWire(m.Declination),
                    JsonNumber.FromWire(m.HourAngle),
                    Enum.TryParse<PointingState>(m.PierSide, out var pier) ? pier : PointingState.Unknown,
                    m.IsSlewing,
                    m.IsTracking,
                    JsonNumber.FromWire(m.RaJ2000),
                    JsonNumber.FromWire(m.DecJ2000),
                    JsonNumber.FromWire(m.Altitude),
                    JsonNumber.FromWire(m.PrimaryAxisAngleDeg));
            }
        }

        /// <summary>
        /// The node's pending flip, verbatim. An instant, so it needs no adjustment for how long the poll
        /// took to arrive -- which is exactly why the wire carries the instant and not a countdown.
        /// </summary>
        public DateTimeOffset? MeridianFlipUtc => Snapshot?.MeridianFlipUtc;

        /// <summary>The node's limit verdict, verbatim; Clear when the node predates the field or nothing is wrong.</summary>
        public MountLimitVerdict MountLimitVerdict => Snapshot?.MountLimit?.ToVerdict() ?? MountLimitVerdict.Clear;

        /// <summary>
        /// The newest entry of the node's notification ring, carried on the state poll. Not part of
        /// <see cref="ISessionTelemetry"/> -- a local session raises notifications as events and the app
        /// records them, so only a mirror has a "last notification" to hand out.
        /// </summary>
        public NotificationDto? LastNotification => Snapshot?.LastNotification;

        public ImmutableArray<TelescopeDisplayInfo> TelescopeDisplays
        {
            get
            {
                if (Snapshot?.Cameras is not { IsDefaultOrEmpty: false } cameras)
                {
                    return [];
                }

                var builder = ImmutableArray.CreateBuilder<TelescopeDisplayInfo>(cameras.Length);
                foreach (var camera in cameras)
                {
                    builder.Add(new TelescopeDisplayInfo(camera.CameraName, camera.HasFocuser, camera.HasFilterWheel));
                }
                return builder.MoveToImmutable();
            }
        }

        public ImmutableArray<CameraExposureState> CameraStates
        {
            get
            {
                if (Snapshot?.Cameras is not { IsDefaultOrEmpty: false } cameras)
                {
                    return [];
                }

                var skew = TimeSpan.FromTicks(Volatile.Read(ref _clockSkewTicks));
                var builder = ImmutableArray.CreateBuilder<CameraExposureState>(cameras.Length);
                foreach (var camera in cameras)
                {
                    builder.Add(new CameraExposureState(
                        camera.OtaIndex,
                        camera.ExposureStart + skew,
                        TimeSpan.FromSeconds(camera.SubExposureSeconds),
                        camera.FrameNumber,
                        camera.FilterName,
                        camera.FocusPosition,
                        Enum.TryParse<CameraState>(camera.State, out var s) ? s : CameraState.Idle,
                        JsonNumber.FromWire(camera.FocuserTemperature),
                        camera.FocuserIsMoving));
                }
                return builder.MoveToImmutable();
            }
        }

        public FrameMetrics[] LastFrameMetrics
        {
            get
            {
                if (Snapshot?.Cameras is not { IsDefaultOrEmpty: false } cameras)
                {
                    return [];
                }

                var metrics = new FrameMetrics[cameras.Length];
                for (var i = 0; i < cameras.Length; i++)
                {
                    metrics[i] = new FrameMetrics(
                        cameras[i].StarCount, JsonNumber.FromWire(cameras[i].MedianHfd), JsonNumber.FromWire(cameras[i].MedianFwhm),
                        TimeSpan.FromSeconds(cameras[i].MetricsExposureSeconds), cameras[i].MetricsGain, cameras[i].MetricsFilterPosition);
                }
                return metrics;
            }
        }

        /// <summary>
        /// The node's schedule, mapped once per polled state (P5b part 8), so every read between two polls, a frame's
        /// included, gets the same tree: the live view counts it each frame and the sky map keys its markers on it.
        /// </summary>
        public ScheduledObservationTree Observations
        {
            get
            {
                var snapshot = Snapshot;
                if (Volatile.Read(ref _observations) is { } cached && ReferenceEquals(cached.Source, snapshot))
                {
                    return cached.Tree;
                }

                var tree = MapObservations(snapshot);
                // A reference swap: two readers mapping the same state at once build the same tree, and either may stay.
                Volatile.Write(ref _observations, new ObservationsCache(snapshot, tree));
                return tree;
            }
        }

        private sealed record ObservationsCache(SessionStateDto? Source, ScheduledObservationTree Tree);

        private ObservationsCache? _observations;

        private static ScheduledObservationTree MapObservations(SessionStateDto? snapshot)
        {
            if (snapshot?.Observations is not { IsDefaultOrEmpty: false } observations)
            {
                return new ScheduledObservationTree([]);
            }

            var builder = ImmutableArray.CreateBuilder<ScheduledObservation>(observations.Length);
            foreach (var obs in observations)
            {
                builder.Add(ToScheduled(obs));
            }
            return new ScheduledObservationTree(builder.MoveToImmutable());
        }

        public ScheduledObservation? ActiveObservation
        {
            get
            {
                if (Snapshot is not { } state)
                {
                    return null;
                }

                var index = state.CurrentObservationIndex;
                if (index >= 0 && !state.Observations.IsDefaultOrEmpty && index < state.Observations.Length)
                {
                    return ToScheduled(state.Observations[index]);
                }

                // Index unavailable but a target is named: synthesize a minimal observation so the
                // status line and window title still show what is being imaged. Coordinates come from
                // the schedule entry when one matches by name.
                if (state.ActiveTargetName is not { Length: > 0 } name)
                {
                    return null;
                }

                foreach (var obs in state.Observations.IsDefaultOrEmpty ? [] : state.Observations)
                {
                    if (string.Equals(obs.TargetName, name, StringComparison.Ordinal))
                    {
                        return ToScheduled(obs);
                    }
                }

                return new ScheduledObservation(
                    new Target(double.NaN, double.NaN, name, null),
                    default, TimeSpan.Zero, AcrossMeridian: false, FilterPlan: [], Gain: null, Offset: null);
            }
        }

        public ImmutableArray<PhaseTimestamp> PhaseTimeline => Volatile.Read(ref _histories).PhaseTimeline;

        public string? GuiderState => Snapshot?.Guider?.State;

        public TimeSpan GuideExposure => TimeSpan.FromSeconds(Snapshot?.Guider?.GuideExposureSeconds ?? 0);

        public GuideStats? LastGuideStats
        {
            get
            {
                if (Snapshot?.Guider is not { } guider)
                {
                    return null;
                }

                // A guider that has no stats yet sends none: null, which the UI shows as a placeholder.
                if (guider is { TotalRMS: null, RaRMS: null, DecRMS: null, PeakRa: null, PeakDec: null })
                {
                    return null;
                }

                return GuideStats.FromRms(JsonNumber.FromWire(guider.TotalRMS), JsonNumber.FromWire(guider.RaRMS),
                    JsonNumber.FromWire(guider.DecRMS), JsonNumber.FromWire(guider.PeakRa), JsonNumber.FromWire(guider.PeakDec),
                    guider.LastRaErr, guider.LastDecErr, guider.LastRaPulseMs, guider.LastDecPulseMs);
            }
        }

        public ImmutableArray<GuideErrorSample> GuideSamples => Volatile.Read(ref _histories).GuideSamples;

        // --- Event-sourced: the state DTO carries no history for these, but the node broadcasts every
        // occurrence, so it covers everything since this mirror attached (not the whole run). --------

        public ImmutableArray<PlateSolveRecord> PlateSolveHistory => _plateSolveHistory;

        /// <summary>
        /// The <b>whole run's</b> log, from the polls (P5b part 7: the first poll brings it whole, each later one only the
        /// entries after those held). Deliberately NOT built from FRAME-WRITTEN, which only ever covered frames written
        /// while this mirror was attached, so a client joining mid-night showed an empty frame list next to a non-zero
        /// frame count. Polling is the authoritative channel (the broadcast is a latency hint), so the worst case is
        /// lagging one frame by one poll.
        /// </summary>
        public ImmutableArray<ExposureLogEntry> ExposureLog => Volatile.Read(ref _histories).ExposureLog;

        public ImmutableArray<FocusRunRecord> FocusHistory => Volatile.Read(ref _histories).FocusHistory;

        public ImmutableArray<(int Position, float Hfd)> ActiveFocusSamples
            => ToCurve(Snapshot?.ActiveFocusSamples ?? []);

        public ImmutableArray<CoolingSample> CoolingSamples => Volatile.Read(ref _histories).CoolingSamples;

        private static ImmutableArray<(int Position, float Hfd)> ToCurve(ImmutableArray<FocusSampleDto> curve)
        {
            if (curve.IsDefaultOrEmpty)
            {
                return [];
            }

            var builder = ImmutableArray.CreateBuilder<(int, float)>(curve.Length);
            foreach (var sample in curve)
            {
                builder.Add((sample.Position, JsonNumber.FromWire(sample.Hfd)));
            }
            return builder.MoveToImmutable();
        }

        public SettleProgress? GuiderSettleProgress => Snapshot?.Guider?.Settle?.ToSettle();

        /// <summary>
        /// The node's per-OTA frames, linear and full-resolution, fetched by the poll loop.
        /// <para>
        /// The same contract as a local session's slots: each frame is the mirror's, and it releases one (giving
        /// its planes back to the mirror's reader) once the next is published, so a reader LEASES, never holds the
        /// bare reference (<see cref="Image.TryLease"/>).
        /// </para>
        /// </summary>
        public Image?[] LastCapturedImages => Volatile.Read(ref _previews);

        /// <inheritdoc/>
        /// <remarks>The token the node answered with when the frame in <see cref="LastCapturedImages"/> was
        /// fetched, so a view of a remote rig reads the same number as the node's own slot carries.</remarks>
        public int LastCapturedImageNumber(int otaIndex)
        {
            var numbers = Volatile.Read(ref _previewFrameNumbers);
            return (uint)otaIndex < (uint)numbers.Length && numbers[otaIndex] is { } number ? number : 0;
        }

        /// <summary>
        /// The mirrored guide-camera frame, present once <see cref="Previews"/> asks for it and the node has a
        /// guider producing frames. Like <see cref="LastCapturedImages"/> it is the mirror's, released once its
        /// successor is published, so a reader leases.
        /// </summary>
        public Image? LastGuideFrame => Volatile.Read(ref _guidePreview);

        /// <inheritdoc/>
        public int LastGuideFrameNumber => Snapshot?.Guider?.GuideFrameNumber ?? 0;

        /// <summary>Mirrored from the state poll, so the crosshair lands on the star the node is
        /// actually tracking rather than on the brightest thing in a re-encoded preview.</summary>
        public (double X, double Y)? GuideStarPosition =>
            Snapshot?.Guider is { GuideStarX: { } x, GuideStarY: { } y } ? (x, y) : null;

        /// <inheritdoc cref="GuideStarPosition"/>
        public double? GuideStarSNR => Snapshot?.Guider?.GuideStarSNR;

        /// <summary>
        /// The node's own profile of the star, taken from its full guide frame: this side cannot derive one, since
        /// cross-sections of the stretched, lossy preview would give a confidently wrong FWHM rather than none.
        /// </summary>
        public (float[] H, float[] V)? GuideStarProfile => Snapshot?.Guider is { StarProfileH: { IsDefaultOrEmpty: false } h, StarProfileV: { IsDefaultOrEmpty: false } v }
            ? (h.ToArray(), v.ToArray())
            : null;

        public CalibrationOverlayData? CalibrationOverlay => Snapshot?.Guider?.Calibration?.ToOverlay();

        /// <summary>Empty: backlash estimates are mirrored back onto the node's own focuser URIs at its
        /// session end, so they never need to cross the wire.</summary>
        public ImmutableDictionary<Uri, BacklashEstimateRecord> FocuserBacklashEstimates =>
            ImmutableDictionary<Uri, BacklashEstimateRecord>.Empty;

        // -----------------------------------------------------------------------------------------
        // Events
        // -----------------------------------------------------------------------------------------

        /// <summary>Raised from the poll diff (see <see cref="RaiseDerivedEvents"/>).</summary>
        public event EventHandler<SessionPhaseChangedEventArgs>? PhaseChanged;

        /// <summary>Raised from the node's FRAME-WRITTEN broadcast (see <see cref="AppendFrame"/>).</summary>
        public event EventHandler<FrameWrittenEventArgs>? FrameWritten;

        /// <summary>Raised from the node's PLATE-SOLVE-COMPLETED broadcast.</summary>
        public event EventHandler<PlateSolveCompletedEventArgs>? PlateSolveCompleted;

        /// <summary>Raised from the node's SCOUT-COMPLETED broadcast, which carries the scouted target whole (P5b part 6).</summary>
        public event EventHandler<ScoutCompletedEventArgs>? ScoutCompleted
        {
            add => _scoutCompleted += value;
            remove => _scoutCompleted -= value;
        }
        private EventHandler<ScoutCompletedEventArgs>? _scoutCompleted;

        /// <summary>Raised from the poll diff (see <see cref="RaiseDerivedEvents"/>); the node's GUIDER-STATE-CHANGED only makes it poll now.</summary>
        public event EventHandler<GuiderStateChangedEventArgs>? GuiderStateChanged;

        /// <summary>
        /// A note the node recorded, as it pushed it (<c>NOTIFICATION</c>). The node's ring
        /// (<see cref="GetNotificationsAsync"/>) holds what came before.
        /// </summary>
        public event EventHandler<NotificationDto>? NoteReceived;

        /// <summary>
        /// An event from the node that is not its session's: a device's state, a profile, a job, an enhance, each handed to
        /// the client that reads it (P6 of docs/plans/hardware-in-the-server.md, #936: a view's device model keeps the
        /// devices a node holds from <c>DEVICE-STATE</c>). Raised on the socket's thread, as it arrived.
        /// </summary>
        public event EventHandler<WebSocketEventDto>? NodeEventNotTheSessions;

        /// <summary>
        /// Something a view draws changed (P5b part 6): a state polled, the node gone quiet or back, a session ended, a
        /// frame, a solve or a note. Raised on the poll loop or the socket's thread, so a handler only flags a redraw.
        /// </summary>
        public event EventHandler? Changed;

        /// <summary>
        /// Raised when the node reports an outstanding prompt, with a <see cref="SessionPromptEventArgs.Respond"/>
        /// that POSTs the answer back to the node.
        /// <para>
        /// <b>Sourced from the poll, not the broadcast</b> -- deliberately, and this is the case that
        /// shows why polling is authoritative here. A prompt delivered only by <c>PROMPT-REQUESTED</c>
        /// would be unanswerable by a client that attached after it fired, or whose socket dropped and
        /// reconnected while it stood: the node would hold the run open forever waiting for an answer
        /// from a UI that never learned there was a question. Carrying it on <c>/session/state</c> means
        /// any client that can see the rig can also unblock it.
        /// </para>
        /// <para>
        /// Explicit accessors for the same reason as <see cref="ScoutCompleted"/>.
        /// </para>
        /// </summary>
        public event EventHandler<SessionPromptEventArgs>? PromptRequested
        {
            add => _promptRequested += value;
            remove => _promptRequested -= value;
        }
        private EventHandler<SessionPromptEventArgs>? _promptRequested;

        // -----------------------------------------------------------------------------------------
        // Mapping helpers
        // -----------------------------------------------------------------------------------------

        /// <summary>Bounded append with an atomic reference swap: the render thread may be enumerating
        /// the previous array while the WS callback publishes the next one.</summary>
        private static ImmutableArray<T> Append<T>(ImmutableArray<T> current, T item, int cap)
        {
            var next = current.Add(item);
            return next.Length > cap ? next.RemoveAt(0) : next;
        }

        private static ScheduledObservation ToScheduled(ObservationDto obs) => new ScheduledObservation(
            new Target(JsonNumber.FromWire(obs.TargetRA), JsonNumber.FromWire(obs.TargetDec), obs.TargetName,
                obs.CatalogIndex is { } index ? (CatalogIndex)index : null),
            obs.Start,
            TimeSpan.FromMinutes(obs.DurationMinutes),
            obs.AcrossMeridian,
            // The plan filter by filter, as the node scheduled it (P5b part 3). It used to be guessed from the frame
            // estimate as one filter, and two filters of 20 s and 30 s came back as one of 26 s.
            FilterPlan: [.. obs.FilterPlan.Select(fe => new FilterExposure(fe.FilterPosition, TimeSpan.FromSeconds(fe.SubExposureSeconds), fe.Count))],
            Gain: obs.Gain,
            Offset: obs.Offset,
            Priority: obs.Priority);

        // The WS payload is a Dictionary<string, object?> (the AOT constraint on the event bag), so
        // values arrive as JsonElement. These readers keep that detail in one place.
        private static string? ReadString(System.Collections.Generic.Dictionary<string, object?> data, string key) =>
            data.TryGetValue(key, out var v) && v is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e
                ? e.GetString()
                : v as string;

        private static bool ReadBool(System.Collections.Generic.Dictionary<string, object?> data, string key) =>
            data.TryGetValue(key, out var v) && v switch
            {
                System.Text.Json.JsonElement e => e.ValueKind is System.Text.Json.JsonValueKind.True,
                bool b => b,
                _ => false
            };

        private static double? ReadDouble(System.Collections.Generic.Dictionary<string, object?> data, string key)
        {
            if (!data.TryGetValue(key, out var v))
            {
                return null;
            }

            return v switch
            {
                System.Text.Json.JsonElement e when e.ValueKind is System.Text.Json.JsonValueKind.Number => e.GetDouble(),
                double d => d,
                int i => i,
                _ => null
            };
        }

        private static TEnum ParseEnum<TEnum>(System.Collections.Generic.Dictionary<string, object?> data, string key, TEnum fallback)
            where TEnum : struct, Enum =>
            TryParseEnum<TEnum>(data, key, out var parsed) ? parsed : fallback;

        private static bool TryParseEnum<TEnum>(System.Collections.Generic.Dictionary<string, object?> data, string key, out TEnum parsed)
            where TEnum : struct, Enum
        {
            parsed = default;
            return ReadString(data, key) is { } s && Enum.TryParse(s, out parsed);
        }

        private static ulong? ReadULong(System.Collections.Generic.Dictionary<string, object?> data, string key) =>
            data.TryGetValue(key, out var v) ? v switch
            {
                System.Text.Json.JsonElement e when e.ValueKind is System.Text.Json.JsonValueKind.Number && e.TryGetUInt64(out var u) => u,
                ulong u => u,
                _ => null
            } : null;

        private static DateTimeOffset? ReadDateTimeOffset(System.Collections.Generic.Dictionary<string, object?> data, string key) =>
            data.TryGetValue(key, out var v) ? v switch
            {
                System.Text.Json.JsonElement e when e.ValueKind is System.Text.Json.JsonValueKind.String && e.TryGetDateTimeOffset(out var at) => at,
                DateTimeOffset at => at,
                _ => null
            } : null;

        private static int[] ReadInts(System.Collections.Generic.Dictionary<string, object?> data, string key)
        {
            if (!data.TryGetValue(key, out var v))
            {
                return [];
            }
            if (v is int[] ints)
            {
                return ints;
            }
            if (v is not System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } array)
            {
                return [];
            }

            var read = new int[array.GetArrayLength()];
            var i = 0;
            foreach (var item in array.EnumerateArray())
            {
                read[i++] = item.TryGetInt32(out var n) ? n : 0;
            }
            return read;
        }
    }
}
