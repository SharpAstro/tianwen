using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
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
    /// <b>Fidelity.</b> Everything in <see cref="SessionStateDto"/> is faithful (phase, activity,
    /// failure reason, counters, mount pointing + name, per-OTA camera/focus/filter state and display
    /// facts, guide stats + sample ring, schedule, phase timeline). Fields with no wire representation
    /// yet return empty rather than guessing, and each says why below; the tabs already handle empty
    /// because a local session starts out that way too. <see cref="PlateSolveHistory"/> and
    /// <see cref="ExposureLog"/> are <b>event-sourced</b> rather than read from the snapshot: the node
    /// broadcasts every solve and every written frame but carries neither history in its state, so both
    /// cover everything since this mirror attached.
    /// </para>
    /// </summary>
    public sealed class RemoteSessionMirror : ISessionTelemetry, IAsyncDisposable
    {
        // Poll cadences. A running session changes visibly (countdowns, guide samples, pointing); an
        // idle node only needs to be noticed when it starts. Both are far cheaper than the LAN can
        // notice, and the WS stream already covers the moments that matter for responsiveness.
        private static readonly TimeSpan ActivePollInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);

        /// <summary>Caps on the locally accumulated event-sourced lists, each the order of a night's worth.</summary>
        private const int MaxPlateSolveHistory = 500;
        private const int MaxExposureLog = 2000;

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
        private ImmutableArray<ExposureLogEntry> _exposureLog = [];

        // Last observed guider state string, so a change can be surfaced as GuiderStateChanged until
        // the node broadcasts one itself. Only touched by the poll loop.
        private string? _lastGuiderState;
        private SessionPhase _lastPhase = SessionPhase.NotStarted;

        private CancellationTokenSource? _cts;
        private Task? _pollLoop;

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

        /// <summary>Whether the push stream is currently attached (telemetry is still correct without
        /// it, just up to one poll interval behind).</summary>
        public bool IsEventStreamConnected => _events.IsConnected;

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
        }

        // -----------------------------------------------------------------------------------------
        // Poll loop
        // -----------------------------------------------------------------------------------------

        private async Task PollLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("Remote session poll for {Node} cancelled", _client.BaseAddress);
                    break;
                }

                var interval = HasSession ? ActivePollInterval : IdlePollInterval;
                await _timeProvider.SleepAsync(interval, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>One poll cycle. Internal so a test can step it with a fake clock.</summary>
        internal async Task PollOnceAsync(CancellationToken cancellationToken)
        {
            var result = await _client.GetSessionStateAsync(cancellationToken).ConfigureAwait(false);

            if (result is { IsSuccess: true, Value: { } state })
            {
                IsNodeReachable = true;
                LastError = null;
                Volatile.Write(ref _snapshot, state);
                RaiseDerivedEvents(state);
                return;
            }

            if (result.IsNotFound)
            {
                // The node is up and idle. Drop the stale snapshot so the UI stops rendering a session
                // that has ended, and reset the change-detection baselines with it.
                IsNodeReachable = true;
                LastError = null;
                Volatile.Write(ref _snapshot, null);
                _lastGuiderState = null;
                _lastPhase = SessionPhase.NotStarted;
                return;
            }

            // Unreachable: keep the last snapshot. A brief network blip should leave the last known
            // state on screen (flagged stale via IsNodeReachable) rather than blanking the tab.
            IsNodeReachable = false;
            LastError = result.Error;
        }

        /// <summary>
        /// Events the node does not broadcast, derived from consecutive polls.
        /// <see cref="GuiderStateChanged"/> has no server-side broadcast yet, and a phase change seen
        /// only by polling (a dropped WS frame) must still reach subscribers, so both are diffed here.
        /// Raising <see cref="PhaseChanged"/> from both paths is safe because the poll fires only on an
        /// actual transition of its own baseline.
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
        }

        /// <summary>
        /// Push-event handler. <c>internal</c> so tests can feed a decoded event straight in: faking the
        /// socket itself would mean faking <see cref="System.Net.WebSockets.ClientWebSocket"/>, whose
        /// <c>ConnectAsync</c> is not virtual, and reflection is not an option in this repo.
        /// </summary>
        internal void OnNodeEvent(object? sender, WebSocketEventDto dto)
        {
            switch (dto.Event)
            {
                case "SESSION-PHASE-CHANGED":
                    // Deliberately NOT raised here: the poll's own diff owns PhaseChanged, so a phase
                    // transition cannot be announced twice (once by the push, once by the next poll)
                    // to subscribers that count transitions. The push still earns its keep -- it wakes
                    // the loop's consumer promptly via the redraw the notification triggers.
                    break;

                case "FRAME-WRITTEN":
                    AppendFrame(dto);
                    break;

                case "PLATE-SOLVE-COMPLETED":
                    AppendPlateSolve(dto);
                    break;
            }
        }

        /// <summary>
        /// The FRAME-WRITTEN broadcast happens to carry every field of an
        /// <see cref="ExposureLogEntry"/> except its timestamp, so the entry is rebuilt faithfully and
        /// stamped with arrival time (within one network hop of the node's own). That makes
        /// <see cref="ExposureLog"/> genuinely populated from the moment this mirror attached; only
        /// backfilling frames written BEFORE that still needs a server-side log endpoint.
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

            _exposureLog = Append(_exposureLog, entry, MaxExposureLog);
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
        }

        // -----------------------------------------------------------------------------------------
        // ISessionTelemetry -- faithful projections of the snapshot
        // -----------------------------------------------------------------------------------------

        private SessionStateDto? Snapshot => Volatile.Read(ref _snapshot);

        public SessionPhase Phase => Snapshot?.Phase ?? SessionPhase.NotStarted;

        public string? CurrentActivity => Snapshot?.CurrentActivity;

        public string? FailureReason => Snapshot?.FailureReason;

        public int TotalFramesWritten => Snapshot?.TotalFramesWritten ?? 0;

        public TimeSpan TotalExposureTime => TimeSpan.FromSeconds(Snapshot?.TotalExposureTimeSeconds ?? 0);

        public int CurrentObservationIndex => Snapshot?.CurrentObservationIndex ?? -1;

        public string? LastFramePath => Snapshot?.LastFramePath;

        public string MountDisplayName => Snapshot?.MountDisplayName ?? string.Empty;

        public MountState MountState
        {
            get
            {
                if (Snapshot?.Mount is not { } m)
                {
                    // Same "unknown" encoding a local session uses before its first device poll, so the
                    // reticle-suppression checks downstream behave identically.
                    return new MountState(double.NaN, double.NaN, double.NaN, PointingState.Unknown, false, false);
                }

                return new MountState(
                    m.RightAscension,
                    m.Declination,
                    m.HourAngle,
                    Enum.TryParse<PointingState>(m.PierSide, out var pier) ? pier : PointingState.Unknown,
                    m.IsSlewing,
                    m.IsTracking);
            }
        }

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

                var builder = ImmutableArray.CreateBuilder<CameraExposureState>(cameras.Length);
                foreach (var camera in cameras)
                {
                    builder.Add(new CameraExposureState(
                        camera.OtaIndex,
                        camera.ExposureStart,
                        TimeSpan.FromSeconds(camera.SubExposureSeconds),
                        camera.FrameNumber,
                        camera.FilterName,
                        camera.FocusPosition,
                        Enum.TryParse<CameraState>(camera.State, out var s) ? s : CameraState.Idle,
                        camera.FocuserTemperature,
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
                    // Exposure + gain are not carried per-frame in the state DTO; the countdown reads
                    // CameraStates.SubExposure instead, and the drift detector is a node-side concern.
                    metrics[i] = new FrameMetrics(
                        cameras[i].StarCount, cameras[i].MedianHfd, cameras[i].MedianFwhm,
                        TimeSpan.Zero, Gain: 0);
                }
                return metrics;
            }
        }

        public ScheduledObservationTree Observations
        {
            get
            {
                if (Snapshot?.Observations is not { IsDefaultOrEmpty: false } observations)
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

        public ImmutableArray<PhaseTimestamp> PhaseTimeline
        {
            get
            {
                if (Snapshot?.PhaseTimeline is not { IsDefaultOrEmpty: false } timeline)
                {
                    return [];
                }

                var builder = ImmutableArray.CreateBuilder<PhaseTimestamp>(timeline.Length);
                foreach (var pt in timeline)
                {
                    builder.Add(new PhaseTimestamp(pt.Phase, pt.StartTime));
                }
                return builder.MoveToImmutable();
            }
        }

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

                // A node with a guider that has not produced stats yet reports all zeros; treat that as
                // "no stats" so the UI shows a placeholder rather than a perfect 0.00" RMS.
                if (guider is { TotalRMS: 0, RaRMS: 0, DecRMS: 0, PeakRa: 0, PeakDec: 0 })
                {
                    return null;
                }

                return GuideStats.FromRms(guider.TotalRMS, guider.RaRMS, guider.DecRMS, guider.PeakRa, guider.PeakDec);
            }
        }

        public ImmutableArray<GuideErrorSample> GuideSamples
        {
            get
            {
                if (Snapshot?.Guider?.RecentSteps is not { IsDefaultOrEmpty: false } steps)
                {
                    return [];
                }

                var builder = ImmutableArray.CreateBuilder<GuideErrorSample>(steps.Length);
                foreach (var step in steps)
                {
                    builder.Add(new GuideErrorSample(
                        step.Timestamp, step.RaError, step.DecError,
                        step.RaCorrectionMs, step.DecCorrectionMs, step.IsDither, step.IsSettling));
                }
                return builder.MoveToImmutable();
            }
        }

        // --- Event-sourced: the state DTO carries no history for these, but the node broadcasts every
        // occurrence, so both cover everything since this mirror attached (not the whole run). ------

        public ImmutableArray<PlateSolveRecord> PlateSolveHistory => _plateSolveHistory;

        /// <inheritdoc cref="PlateSolveHistory"/>
        public ImmutableArray<ExposureLogEntry> ExposureLog => _exposureLog;

        // --- No wire representation yet. Empty, not guessed. -------------------------------------

        /// <summary>Empty: the node does not expose settle progress (needs the guider telemetry of
        /// remote-profile.md Part 2 item 3).</summary>
        public SettleProgress? GuiderSettleProgress => null;

        /// <summary>Empty: V-curve data needs Part 2 item 6 (telemetry depth).</summary>
        public ImmutableArray<FocusRunRecord> FocusHistory => [];

        /// <inheritdoc cref="FocusHistory"/>
        public ImmutableArray<(int Position, float Hfd)> ActiveFocusSamples => [];

        /// <summary>Empty: cooler temperature/power history needs Part 2 item 6 (telemetry depth).</summary>
        public ImmutableArray<CoolingSample> CoolingSamples => [];

        /// <summary>Empty: needs the per-OTA preview endpoint (Part 2 item 1). Until then a mirrored
        /// session shows telemetry without thumbnails.</summary>
        public Image?[] LastCapturedImages => [];

        /// <summary>Local-only (tier 3): the guide-camera frame and star visuals are pixel streams no
        /// endpoint serves.</summary>
        public Image? LastGuideFrame => null;

        /// <inheritdoc cref="LastGuideFrame"/>
        public (double X, double Y)? GuideStarPosition => null;

        /// <inheritdoc cref="LastGuideFrame"/>
        public double? GuideStarSNR => null;

        /// <inheritdoc cref="LastGuideFrame"/>
        public (float[] H, float[] V)? GuideStarProfile => null;

        /// <inheritdoc cref="LastGuideFrame"/>
        public CalibrationOverlayData? CalibrationOverlay => null;

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

        /// <summary>
        /// Not raised yet: the node broadcasts SCOUT-COMPLETED, but its payload carries a per-OTA
        /// star-count map that <see cref="ScoutCompletedEventArgs"/> cannot be rebuilt from faithfully
        /// (it needs the resolved <c>Target</c>). Declared with explicit accessors so a subscriber is
        /// still REGISTERED and starts receiving the moment this is wired -- a field-like event that is
        /// never raised trips CS0067, and swallowing the subscription instead would be a silent lie.
        /// </summary>
        public event EventHandler<ScoutCompletedEventArgs>? ScoutCompleted
        {
            add => _scoutCompleted += value;
            remove => _scoutCompleted -= value;
        }
        private EventHandler<ScoutCompletedEventArgs>? _scoutCompleted;

        /// <summary>Raised from the poll diff, since the node has no such broadcast yet.</summary>
        public event EventHandler<GuiderStateChangedEventArgs>? GuiderStateChanged;

        /// <summary>
        /// Never raised yet: answering a prompt needs BOTH halves of Part 2 item 2 -- the node has to
        /// broadcast the request AND accept the response -- because
        /// <see cref="SessionPromptEventArgs.Respond"/> has to reach back across the wire. Until then a
        /// remote flat run with a hand-switched panel auto-proceeds node-side, exactly as a headless CLI
        /// run does, so nothing blocks waiting for a click that can never arrive.
        /// Explicit accessors for the same reason as <see cref="ScoutCompleted"/>.
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
            new Target(obs.TargetRA, obs.TargetDec, obs.TargetName, null),
            obs.Start,
            TimeSpan.FromMinutes(obs.DurationMinutes),
            obs.AcrossMeridian,
            // The state DTO flattens the filter plan away (it is a scheduling input, not observed
            // state); the schedule-fidelity DTO of Part 2 item 8 is what carries it back.
            FilterPlan: [],
            Gain: null,
            Offset: null);

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
            ReadString(data, key) is { } s && Enum.TryParse<TEnum>(s, out var parsed) ? parsed : fallback;
    }
}
