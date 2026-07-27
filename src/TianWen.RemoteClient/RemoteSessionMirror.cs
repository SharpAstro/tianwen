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
    /// How a mirror should pull preview frames. Both knobs trade link cost against fidelity, and both
    /// are applied by the node's encoder, so a downscaled preview costs the link only what it is worth.
    /// </summary>
    /// <param name="Quality">JPEG quality 1-100; null uses the node's default (80).</param>
    /// <param name="Scale">Downscale factor in (0, 1); null or out of range means full resolution. A
    /// thumbnail strip wants something like 0.25.</param>
    public readonly record struct PreviewOptions(int? Quality = null, double? Scale = null);

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
    /// facts, guide stats + sample ring, schedule, phase timeline, cooling ramp, focus history and
    /// exposure log). Preview frames are fetched as JPEG and decoded here when
    /// <see cref="Previews"/> is set. Fields with no wire representation yet return empty rather than
    /// guessing, and each says why below; the tabs already handle empty because a local session starts
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
        private SessionPhase _lastPhase = SessionPhase.NotStarted;

        // Identity of the prompt already raised locally, so the poll (which sees the same outstanding
        // prompt on every tick until it is answered) raises it exactly once. Only touched by the poll
        // loop. Cleared when the node reports no prompt, so a later prompt with identical wording -- the
        // same panel, the next filter -- is raised again rather than swallowed as a duplicate.
        private string? _raisedPromptKey;

        // Decoded preview frames, one slot per OTA, and the frame number each slot holds. Published by
        // reference swap: the poll loop decodes off the render thread and the render thread reads the
        // array per frame.
        private Image?[] _previews = [];
        private long?[] _previewFrameNumbers = [];

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

        private void StampContact() =>
            Interlocked.Exchange(ref _lastContactTicks, _timeProvider.GetUtcNow().UtcTicks);

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
        /// </summary>
        public async Task<NodeResult<string>> StartAsync(
            ScheduledObservationDto[] schedule, Guid? profileId, CancellationToken cancellationToken)
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
            return await _client.StartSessionAsync(profileId, cancellationToken).ConfigureAwait(false);
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
                StampContact();
                Volatile.Write(ref _snapshot, state);
                RaiseDerivedEvents(state);
                await RefreshPreviewsAsync(state, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (result.IsNotFound)
            {
                // The node is up and idle. Drop the stale snapshot so the UI stops rendering a session
                // that has ended, and reset the change-detection baselines with it.
                IsNodeReachable = true;
                LastError = null;
                StampContact(); // a 404 is the node answering -- "seen" is about the node, not the session
                Volatile.Write(ref _snapshot, null);
                _lastGuiderState = null;
                _lastPhase = SessionPhase.NotStarted;
                _raisedPromptKey = null;
                ClearPreviews();
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
        /// <summary>
        /// Whether to fetch preview frames at all, and how. Off by default: a mirror is often attached
        /// just to watch phase and counters (a multi-rig dashboard), and previews are by far the most
        /// expensive thing on the link. A UI that actually shows thumbnails turns them on.
        /// </summary>
        public PreviewOptions? Previews { get; set; }

        /// <summary>
        /// Pulls each OTA's latest preview, skipping any whose frame number the mirror already holds.
        /// <para>
        /// Runs on the poll loop, so decode never touches the render thread. Frames are fetched
        /// sequentially rather than in parallel: a multi-OTA rig is normally on the far end of a home
        /// LAN or a VPN, and N concurrent full-frame JPEGs would spike latency for the state poll that
        /// everything else depends on.
        /// </para>
        /// </summary>
        private async Task RefreshPreviewsAsync(SessionStateDto state, CancellationToken cancellationToken)
        {
            if (Previews is not { } options)
            {
                return;
            }

            var otaCount = state.Cameras.IsDefaultOrEmpty ? 0 : state.Cameras.Length;
            if (otaCount == 0)
            {
                ClearPreviews();
                return;
            }

            var images = Volatile.Read(ref _previews);
            var numbers = _previewFrameNumbers;
            if (images.Length != otaCount)
            {
                images = new Image?[otaCount];
                numbers = new long?[otaCount];
            }
            else
            {
                // Copy before mutating: the render thread may be reading the published array right now.
                images = (Image?[])images.Clone();
                numbers = (long?[])numbers.Clone();
            }

            var changed = images.Length != otaCount;
            for (var i = 0; i < otaCount; i++)
            {
                PreviewResult result;
                try
                {
                    result = await _client
                        .GetPreviewAsync(i, options.Quality, options.Scale, numbers[i], cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                if (result.IsUnchanged)
                {
                    continue;
                }

                if (result.Error is { } error)
                {
                    // A preview failure must never blank the telemetry: keep the last frame and let the
                    // state poll go on reporting. Debug, not Warning -- a link too slow for previews
                    // would otherwise flood the log every poll.
                    _logger.LogDebug("Preview fetch for OTA {Ota} on {Node} failed: {Error}", i, _client.BaseAddress, error);
                    continue;
                }

                if (!result.HasImage)
                {
                    // No frame captured yet.
                    continue;
                }

                if (Image.TryDecodeRaster(result.Jpeg, out var decoded))
                {
                    images[i] = decoded;
                    numbers[i] = result.FrameNumber;
                    changed = true;
                }
                else
                {
                    _logger.LogDebug("Preview frame for OTA {Ota} on {Node} did not decode", i, _client.BaseAddress);
                }
            }

            if (changed)
            {
                _previewFrameNumbers = numbers;
                Volatile.Write(ref _previews, images);
            }
        }

        private void ClearPreviews()
        {
            if (Volatile.Read(ref _previews).Length == 0)
            {
                return;
            }

            _previewFrameNumbers = [];
            Volatile.Write(ref _previews, []);
        }

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
                _raisedPromptKey = null;
                return;
            }

            var key = $"{pending.Title} {pending.Message}";
            if (string.Equals(key, _raisedPromptKey, StringComparison.Ordinal))
            {
                return;
            }

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
            handler(this, new SessionPromptEventArgs(
                pending.Title,
                pending.Message,
                pending.ContinueLabel,
                pending.CancelLabel,
                completion,
                pending.RequiresPhysicalPresence,
                // The node owns the unattended policy and has already applied it if it was going to; a
                // prompt that reached us is one it decided to hold. Nothing here should re-derive it.
                defaultIfUnanswerable: false));

            _ = ForwardPromptAnswerAsync(completion.Task, pending.Title);
        }

        private async Task ForwardPromptAnswerAsync(Task<bool> answer, string title)
        {
            bool proceed;
            try
            {
                proceed = await answer.ConfigureAwait(false);
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
        // occurrence, so it covers everything since this mirror attached (not the whole run). --------

        public ImmutableArray<PlateSolveRecord> PlateSolveHistory => _plateSolveHistory;

        /// <summary>
        /// Read from the snapshot, which carries the <b>whole run</b>. Deliberately NOT the
        /// event-sourced <c>_exposureLog</c> that FRAME-WRITTEN feeds: that only ever covered frames
        /// written while this mirror was attached, so a client joining mid-night showed an empty frame
        /// list next to a non-zero frame count. Polling is the authoritative channel (the broadcast is a
        /// latency hint), so the snapshot wins and the worst case is lagging one frame by one poll.
        /// </summary>
        public ImmutableArray<ExposureLogEntry> ExposureLog
        {
            get
            {
                if (Snapshot?.ExposureLog is not { IsDefaultOrEmpty: false } log)
                {
                    return [];
                }

                var builder = ImmutableArray.CreateBuilder<ExposureLogEntry>(log.Length);
                foreach (var e in log)
                {
                    builder.Add(new ExposureLogEntry(
                        e.Timestamp, e.TargetName, e.FilterName,
                        TimeSpan.FromSeconds(e.ExposureSeconds), e.FrameNumber, e.MedianHfd, e.StarCount));
                }
                return builder.MoveToImmutable();
            }
        }

        public ImmutableArray<FocusRunRecord> FocusHistory
        {
            get
            {
                if (Snapshot?.FocusHistory is not { IsDefaultOrEmpty: false } runs)
                {
                    return [];
                }

                var builder = ImmutableArray.CreateBuilder<FocusRunRecord>(runs.Length);
                foreach (var run in runs)
                {
                    builder.Add(new FocusRunRecord(
                        run.Timestamp, run.OtaName, run.FilterName, run.BestPosition, run.BestHfd,
                        ToCurve(run.Curve), run.FitA, run.FitB));
                }
                return builder.MoveToImmutable();
            }
        }

        public ImmutableArray<(int Position, float Hfd)> ActiveFocusSamples
            => ToCurve(Snapshot?.ActiveFocusSamples ?? []);

        public ImmutableArray<CoolingSample> CoolingSamples
        {
            get
            {
                if (Snapshot?.CoolingSamples is not { IsDefaultOrEmpty: false } samples)
                {
                    return [];
                }

                var builder = ImmutableArray.CreateBuilder<CoolingSample>(samples.Length);
                foreach (var s in samples)
                {
                    builder.Add(new CoolingSample(
                        s.Timestamp, s.CameraIndex, s.TemperatureC, s.SetpointTemperatureC, s.CoolerPowerPercent));
                }
                return builder.MoveToImmutable();
            }
        }

        private static ImmutableArray<(int Position, float Hfd)> ToCurve(ImmutableArray<FocusSampleDto> curve)
        {
            if (curve.IsDefaultOrEmpty)
            {
                return [];
            }

            var builder = ImmutableArray.CreateBuilder<(int, float)>(curve.Length);
            foreach (var sample in curve)
            {
                builder.Add((sample.Position, sample.Hfd));
            }
            return builder.MoveToImmutable();
        }

        // --- No wire representation yet. Empty, not guessed. -------------------------------------

        /// <summary>Empty: the node does not expose settle progress (needs the guider telemetry of
        /// remote-profile.md Part 2 item 3).</summary>
        public SettleProgress? GuiderSettleProgress => null;

        /// <summary>
        /// The node's per-OTA preview frames, fetched as JPEG and decoded by the poll loop.
        /// <para>
        /// Unlike a local session -- where this array hands out a <b>pinned camera buffer</b> the caller
        /// must not retain -- these are ordinary decoded images owned by the mirror. There is nothing to
        /// release, and no risk of starving a camera's recycle loop by holding one.
        /// </para>
        /// </summary>
        public Image?[] LastCapturedImages => Volatile.Read(ref _previews);

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
