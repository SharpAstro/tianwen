using System;
using System.Collections.Immutable;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// An outstanding prompt as a card shows it: what was asked, and for how long.
    /// </summary>
    /// <param name="Title">The prompt's heading, e.g. "Manual flat panel".</param>
    /// <param name="Waiting">
    /// How long it has been unanswered, or <see langword="null"/> when the raising node did not say when
    /// it asked. Never substituted with "since we noticed" -- see
    /// <see cref="SessionPromptEventArgs.RaisedUtc"/>.
    /// </param>
    /// <param name="RequiresPhysicalPresence">Whether answering needs somebody at that rig.</param>
    public readonly record struct RigCardPrompt(string Title, TimeSpan? Waiting, bool RequiresPhysicalPresence)
    {
        /// <summary>
        /// The badge text. The duration is the point of the badge, so it leads; without one the badge still
        /// says a rig is blocked, which is the part that must never be dropped.
        /// </summary>
        public string Describe() =>
            Waiting is { } waiting
                ? $"WAITING {LiveSessionActions.FormatDuration(waiting)}"
                : "WAITING";
    }

    /// <summary>
    /// How much of a rig's equipment is actually connected, as a card shows it.
    /// <para>
    /// Worth its own line because it is the one thing about a rig that a session phase cannot tell you: a
    /// node sitting at "Idle" with nothing connected and one with every driver up look identical otherwise,
    /// and pressing Connect All has no visible effect anywhere on this screen without it.
    /// </para>
    /// </summary>
    /// <param name="Connected">Assigned devices currently connected on that node.</param>
    /// <param name="Assigned">Devices the active profile assigns.</param>
    public readonly record struct RigDeviceLink(int Connected, int Assigned)
    {
        /// <summary>All of them, and at least one.</summary>
        public bool AllConnected => Assigned > 0 && Connected == Assigned;

        /// <summary>
        /// The badge: a socket glyph plus the count, so a partial connect is distinguishable from a complete
        /// one at a glance.
        /// <para>
        /// The glyph and the text share ONE string, which only works because the layout painter resolves
        /// emoji runs to <c>PixelWidgetBase.EmojiFontPath</c> per run. Before that a mixed string was
        /// impossible -- a run is drawn with exactly one font, so an emoji inside ordinary text rendered as
        /// blank space, and every emoji in the app had to be its own draw with the emoji font passed AS the
        /// font.
        /// </para>
        /// </summary>
        public string Describe() =>
            Connected == 0 ? "🔌 No devices connected"
            : Assigned == 1 ? "🔌 1 device connected"
            : AllConnected ? $"🔌 All {Assigned} devices connected"
            : $"🔌 {Connected} of {Assigned} devices connected";
    }

    /// <summary>
    /// How far through the night a rig is: which target of how many, and how many of the current target's
    /// planned frames are in the can.
    /// <para>
    /// Frame counts are for the CURRENT TARGET, not the session. A session total answers "has it been busy";
    /// what an operator actually wants from a board is "is this one nearly done", and only the per-target
    /// ratio says that.
    /// </para>
    /// </summary>
    /// <param name="TargetIndex">1-based position in the schedule.</param>
    /// <param name="TargetCount">Scheduled observations, or 0 when the run has no schedule (a single
    /// ad-hoc target), in which case the target part is simply not shown.</param>
    /// <param name="FramesDone">Frames written for the current target, across all OTAs.</param>
    /// <param name="FramesPlanned">Frames the filter plan asks for, across all OTAs, or 0 when unknown --
    /// which is what a target queued with no filter plan looks like.</param>
    public readonly record struct RigCardProgress(int TargetIndex, int TargetCount, int FramesDone, int FramesPlanned)
    {
        /// <summary>
        /// "target 2/3 · frame 23/100", degrading a part at a time: no plan leaves a bare frame count, no
        /// schedule leaves just the frames. Never invents a denominator it does not have.
        /// </summary>
        public string Describe()
        {
            var frames = FramesPlanned > 0 ? $"frame {FramesDone}/{FramesPlanned}" : $"{FramesDone} frames";
            return TargetCount > 0 ? $"target {TargetIndex}/{TargetCount} · {frames}" : frames;
        }
    }

    /// <summary>
    /// Where a rig's cooling has got to, as a card shows it.
    /// <para>
    /// <b>This is the row that makes a board worth having during setup.</b> Cooling several rigs in parallel
    /// is dead time, and the question is which ones are ready -- a fact that otherwise lives only in the
    /// activity string of whichever rig you happen to have selected, and vanishes from it the moment the
    /// ramp ends.
    /// </para>
    /// <para>
    /// Reported for the camera FURTHEST from its setpoint, because a rig is ready when its last camera is.
    /// </para>
    /// </summary>
    /// <param name="TemperatureC">Sensor temperature of the furthest-off camera.</param>
    /// <param name="SetpointC">That camera's setpoint, or NaN when it reports none.</param>
    /// <param name="PowerPercent">That camera's cooler power.</param>
    /// <param name="AtSetpoint">How many cameras are within <see cref="SetpointToleranceC"/>.</param>
    /// <param name="CameraCount">Cameras reporting cooling at all.</param>
    /// <param name="IsRamping">Whether the session says it is actively ramping (phase Cooling), which is the
    /// rig's own answer and outranks the arithmetic below.</param>
    public readonly record struct RigCardCooling(
        double TemperatureC, double SetpointC, double PowerPercent, int AtSetpoint, int CameraCount, bool IsRamping)
    {
        /// <summary>
        /// How close counts as "there", in degrees.
        /// <para>
        /// A <b>display</b> threshold, and deliberately not presented as the session's own verdict: the ramp
        /// finishes on cooler power plus a consecutive-sample count (<c>CameraCoolingState</c>), which is not
        /// on the wire and would be a much larger change to put there. One degree is the ramp's own step
        /// size, so it is the resolution the numbers are moving in rather than an invented tolerance.
        /// </para>
        /// </summary>
        public const double SetpointToleranceC = 1.0;

        /// <summary>Every camera within tolerance, and the session is not still ramping.</summary>
        public bool IsSettled => !IsRamping && CameraCount > 0 && AtSetpoint == CameraCount;

        /// <summary>
        /// "at -10.0°C · 38%" when there, "-2.1 → -10.0°C · 100%" while ramping, with a per-camera tally on a
        /// multi-OTA rig so one lagging camera is visible.
        /// </summary>
        public string Describe()
        {
            var power = double.IsNaN(PowerPercent) ? "" : $" · {PowerPercent:F0}%";
            var tally = CameraCount > 1 ? $" · {AtSetpoint}/{CameraCount} cameras" : "";

            if (double.IsNaN(SetpointC))
            {
                // No setpoint to aim at: report the sensor, and do not imply a target exists.
                return $"{TemperatureC:F1}°C{power}{tally}";
            }

            return IsSettled
                ? $"at {SetpointC:F1}°C{power}{tally}"
                : $"{TemperatureC:F1} → {SetpointC:F1}°C{power}{tally}";
        }
    }

    /// <summary>
    /// The last thing a rig said, as a card shows it.
    /// <para>
    /// Distinct from the status line, which mirrors <c>CurrentActivity</c> and is overwritten by every
    /// sub-step -- so a warning raised between two polls leaves no trace there. This is the one line on the
    /// card that survives.
    /// </para>
    /// </summary>
    /// <param name="Severity">Parsed at the boundary, so the layout colours by an enum rather than
    /// re-parsing the wire's string.</param>
    /// <param name="Message">The text as recorded.</param>
    /// <param name="Age">How long ago, or null when the source carries no timestamp.</param>
    public readonly record struct RigCardNote(NotificationSeverity Severity, string Message, TimeSpan? Age)
    {
        /// <summary>
        /// The message, prefixed with its age when known. Age leads for the same reason it leads on the
        /// prompt badge: a warning from four hours ago and one from ten seconds ago need different reactions.
        /// </summary>
        public string Describe() =>
            Age is { } age ? $"{LiveSessionActions.FormatDuration(age)} ago · {Message}" : Message;
    }

    /// <summary>
    /// One rig on the home screen, flattened to exactly what a card draws. A pure snapshot: built once per
    /// frame from live state, holding no references back to it, so the tab cannot accidentally read a
    /// half-updated session while painting.
    /// </summary>
    /// <param name="Title">The rig. "This computer" for the local node, which is deliberately just
    /// another card rather than a special case.</param>
    /// <param name="Subtitle">The profile it runs, or <see langword="null"/> when not known yet. This is
    /// the field that tells two similar rigs apart -- which optical train is on which pier.</param>
    /// <param name="IsLocal">Whether this is this node's own card.</param>
    /// <param name="IsOnline">Whether the rig is currently answering.</param>
    /// <param name="Phase">The session phase, for colour and for the status line's fallback.</param>
    /// <param name="Status">One line: what it is doing, or why it is not.</param>
    /// <param name="Target">What it is pointing at, when a run has a current observation.</param>
    /// <param name="FramesWritten">Frames written this run.</param>
    /// <param name="GuideRmsArcsec">Total guide RMS, when guiding.</param>
    /// <param name="Prompt">An outstanding prompt, which is the one thing on a card that is a call to
    /// action rather than a status.</param>
    /// <param name="Devices">How much of the rig's equipment is connected, or <see langword="null"/> when
    /// that is not knowable -- which today is every REMOTE rig, because the session snapshot carries no
    /// device count and asking costs a separate request per rig.</param>
    /// <param name="IsViewed">Whether this is the rig currently on screen. Resolved here rather than in the
    /// tab, which would otherwise have to match a card back to a binding by its title.</param>
    /// <param name="Progress">Which target of how many, and frames done of planned, or
    /// <see langword="null"/> when no run is live.</param>
    /// <param name="Cooling">Cooling state, or <see langword="null"/> when no camera reports any -- which is
    /// most of the night, since the ramp only runs at the start.</param>
    /// <param name="MedianHfd">Median HFD of the last measured frame, or <see langword="null"/> before the
    /// first one. A collapsible detail: it says whether focus is holding, which matters when you are
    /// deciding which rig to look at.</param>
    /// <param name="MeridianFlipUtc">When the current target's flip is due, or <see langword="null"/> when
    /// none is pending. The instant, not the remaining time -- the card subtracts, so the countdown stays
    /// true between polls instead of freezing at whatever it was when the poll landed.</param>
    /// <param name="LastNote">The newest notification, or <see langword="null"/> when the rig has said
    /// nothing.</param>
    public readonly record struct RigCard(
        string Title,
        string? Subtitle,
        bool IsLocal,
        bool IsOnline,
        SessionPhase Phase,
        string Status,
        string? Target,
        int FramesWritten,
        double? GuideRmsArcsec,
        RigCardPrompt? Prompt,
        RigDeviceLink? Devices,
        bool IsViewed,
        RigCardProgress? Progress = null,
        RigCardCooling? Cooling = null,
        double? MedianHfd = null,
        DateTimeOffset? MeridianFlipUtc = null,
        RigCardNote? LastNote = null)
    {
        /// <summary>
        /// How long until the flip, against <paramref name="now"/>, or <see langword="null"/> when no flip is
        /// pending or it is already due.
        /// <para>
        /// Resolved at render time rather than stored, which is the whole reason the instant is what travels:
        /// a stored duration would be as stale as the last poll, and on a rig polled every 30 s a countdown
        /// that only moves in 30 s steps reads as broken.
        /// </para>
        /// </summary>
        public TimeSpan? TimeToMeridianFlip(DateTimeOffset now) =>
            MeridianFlipUtc is { } due && due > now ? due - now : null;

        /// <summary>
        /// Whether a run is in progress, i.e. whether the run fields mean anything. The terminal phases
        /// count as not running: a rig sitting on Complete or Failed has counters from a night that is
        /// over, and presenting them as live would be the card's one outright lie.
        /// </summary>
        public bool IsRunning => IsOnline && Phase is not (
            SessionPhase.NotStarted or SessionPhase.Complete or SessionPhase.Failed or SessionPhase.Aborted);
    }

    /// <summary>
    /// Builds the home screen's rig board (docs/plans/remote-profile.md, "Multi-rig dashboard").
    /// <para>
    /// <b>Read-only by construction.</b> Nothing here connects, commands, or actuates -- driving a rig
    /// still means selecting it, so the overlay model is not quietly duplicated into a second way to
    /// command hardware. The one action the board performs is <see cref="RemoteRigActions.ConnectAllAsync"/>,
    /// which starts read-only mirrors and changes nothing about what is on screen.
    /// </para>
    /// <para>
    /// <b>Opening the board never touches hardware.</b> A remote "connect" is an HTTP mirror; a local one
    /// would open drivers and power a mount, and the local card needs neither -- it reads
    /// <see cref="LiveSessionState"/>, which is populated whether or not anything is connected, so "this
    /// scope, idle, nothing connected" is an accurate free card.
    /// </para>
    /// </summary>
    public static class HomeBoard
    {
        /// <summary>
        /// Every rig this node can look at, local first and then bound rigs by name.
        /// <para>
        /// Sorted by name rather than left in binding order because binding order is load order: a board
        /// whose cards moved between runs would make the wrong rig the one you click by muscle memory.
        /// </para>
        /// </summary>
        public static ImmutableArray<RigCard> BuildCards(
            ViewContexts contexts,
            RemoteRigRegistry rigs,
            GuiAppState appState,
            DateTimeOffset now)
        {
            var active = contexts.Active;
            var cards = ImmutableArray.CreateBuilder<RigCard>(rigs.Bindings.Length + 1);
            cards.Add(LocalCard(contexts.Local, appState, now, isViewed: active.IsLocal));

            foreach (var binding in rigs.Bindings.Sort(static (a, b) =>
                         string.Compare(a.Alias, b.Alias, StringComparison.CurrentCultureIgnoreCase)))
            {
                // Matched on node id, never on the alias: two rigs may announce the same name, and a
                // renamed rig keeps its binding.
                var isViewed = !active.IsLocal
                    && string.Equals(active.NodeId, binding.NodeId, StringComparison.OrdinalIgnoreCase);

                cards.Add(rigs.Find(binding.BindingId) is { } connection
                    ? RemoteCard(connection, now, isViewed)
                    : OfflineCard(binding, now, isViewed));
            }

            return cards.ToImmutable();
        }

        /// <summary>
        /// The local node's card. Always online -- it is this machine, so "not answering" is not a state it
        /// can be in -- and its profile is this app's active profile rather than anything fetched.
        /// </summary>
        private static RigCard LocalCard(ViewContext local, GuiAppState appState, DateTimeOffset now, bool isViewed)
        {
            var session = local.LiveSession;
            return new RigCard(
                Title: local.DisplayName,
                Subtitle: appState.ActiveProfile?.DisplayName,
                IsLocal: true,
                IsOnline: true,
                Phase: session.Phase,
                Status: DescribeActivity(session),
                Target: session.ActiveObservation?.Target.Name,
                FramesWritten: session.TotalFramesWritten,
                GuideRmsArcsec: session.LastGuideStats?.TotalRMS,
                // Aged from the same clock as every other card: a local prompt blocks a run just as long.
                Prompt: DescribePrompt(session, now),
                Devices: LocalDeviceLink(appState),
                IsViewed: isViewed,
                Progress: DescribeProgress(session),
                Cooling: DescribeCooling(session, now),
                MedianHfd: DescribeHfd(session),
                MeridianFlipUtc: session.MeridianFlipUtc,
                LastNote: LocalNote(appState, now));
        }

        /// <summary>
        /// A bound rig with a live mirror. Still shown as offline when the node has stopped answering: the
        /// mirror keeps running and keeps its last snapshot, so without this check a rig that went dark
        /// would keep displaying the session it was running when it did.
        /// </summary>
        private static RigCard RemoteCard(RemoteRigConnection connection, DateTimeOffset now, bool isViewed)
        {
            var session = connection.Context.LiveSession;
            var online = connection.Mirror.IsNodeReachable;

            return new RigCard(
                Title: connection.Binding.Alias,
                Subtitle: connection.ProfileName,
                IsLocal: false,
                IsOnline: online,
                Phase: session.Phase,
                // BindingAsReached folds in the mirror's own last-contact time, so a rig that answered this
                // session reports minutes rather than the stale stamp from a previous run.
                Status: online
                    ? DescribeActivity(session)
                    : $"Not answering{RemoteRigActions.DescribeLastSeen(connection.BindingAsReached(), now)}",
                Target: online ? session.ActiveObservation?.Target.Name : null,
                FramesWritten: session.TotalFramesWritten,
                GuideRmsArcsec: online ? session.LastGuideStats?.TotalRMS : null,
                // A prompt outlives the connection going dark -- it is still blocking that rig's run, and
                // is arguably more urgent once nobody can answer it remotely.
                Prompt: DescribePrompt(session, now),
                // Not knowable from the snapshot -- see RigCard.Devices.
                Devices: null,
                IsViewed: isViewed,
                // Suppressed while dark, for the same reason Target and the RMS are: these describe what the
                // rig is doing NOW, and the mirror keeps its last snapshot, so showing them would present a
                // frozen night as a live one. The prompt and the note are the deliberate exceptions -- a
                // blocked run stays blocked, and the last thing a rig said before going quiet is often the
                // reason it went quiet.
                Progress: online ? DescribeProgress(session) : null,
                Cooling: online ? DescribeCooling(session, now) : null,
                MedianHfd: online ? DescribeHfd(session) : null,
                MeridianFlipUtc: online ? session.MeridianFlipUtc : null,
                LastNote: RemoteNote(connection.Mirror.LastNotification, now));
        }

        /// <summary>
        /// A rig that is bound but has no connection this run: no address was discovered and no stored hint
        /// worked. Kept on the board rather than hidden, because "I own this rig and it is not there" is
        /// information, and a silently missing card looks like a lost binding.
        /// </summary>
        private static RigCard OfflineCard(RemoteRigBinding binding, DateTimeOffset now, bool isViewed) =>
            new RigCard(
                Title: binding.Alias,
                Subtitle: null,
                IsLocal: false,
                IsOnline: false,
                Phase: SessionPhase.NotStarted,
                Status: $"Offline{RemoteRigActions.DescribeLastSeen(binding, now)}",
                Target: null,
                FramesWritten: 0,
                GuideRmsArcsec: null,
                Prompt: null,
                Devices: null,
                IsViewed: isViewed);

        /// <summary>
        /// This node's connected-device count, or null when the active profile assigns none (a fresh profile
        /// with nothing chosen yet, where "0/0 connected" would be noise rather than information).
        /// <para>
        /// Read straight from the hub, which is a handful of dictionary lookups -- it does NOT talk to a
        /// driver, so the board keeps its "no device I/O" property.
        /// </para>
        /// </summary>
        private static RigDeviceLink? LocalDeviceLink(GuiAppState appState)
        {
            if (appState.ActiveProfile?.Data is not { } profileData)
            {
                return null;
            }

            var assigned = 0;
            var connected = 0;
            foreach (var uri in profileData.AssignedDeviceUris)
            {
                assigned++;
                if (appState.DeviceHub?.IsConnected(uri) == true)
                {
                    connected++;
                }
            }

            return assigned == 0 ? null : new RigDeviceLink(connected, assigned);
        }

        /// <summary>
        /// Which target of how many, and frames done of planned, or null when no run is live.
        /// <para>
        /// One path for local and mirrored rigs. That holds because the mirror rebuilds its observations with
        /// the plan's frame total (see <c>RemoteSessionMirror.ToScheduled</c>), so
        /// <see cref="ScheduledObservation.PlannedFrameCount"/> answers the same question on both.
        /// </para>
        /// </summary>
        private static RigCardProgress? DescribeProgress(LiveSessionState session)
        {
            if (!session.HasActiveRun || session.ActiveObservation is not { } active)
            {
                return null;
            }

            var scheduled = session.ObservationCount;
            var index = session.CurrentObservationIndex;

            // Each OTA works the plan in parallel, so the denominator scales with the cameras that are
            // actually shooting. Frames done counts every OTA's, so both sides of the ratio agree.
            var otas = Math.Max(1, session.CameraStates.Length);
            var planned = active.PlannedFrameCount * otas;

            return new RigCardProgress(
                TargetIndex: index >= 0 ? index + 1 : 0,
                // A run with no schedule (a single ad-hoc target) reports no target part rather than "1/0".
                TargetCount: index >= 0 ? scheduled : 0,
                FramesDone: FramesForTarget(session.ExposureLog, active.Target.Name),
                FramesPlanned: planned);
        }

        /// <summary>
        /// Frames written for <paramref name="targetName"/>, counted backwards from the newest entry and
        /// stopping at the first that belongs to something else.
        /// <para>
        /// The session images one target at a time, so its frames are the tail of the log -- which makes this
        /// O(frames for this target) rather than O(the night). That matters because the board rebuilds every
        /// card every frame, times however many rigs are bound.
        /// </para>
        /// </summary>
        private static int FramesForTarget(ImmutableArray<ExposureLogEntry> log, string targetName)
        {
            if (log.IsDefaultOrEmpty)
            {
                return 0;
            }

            var count = 0;
            for (var i = log.Length - 1; i >= 0; i--)
            {
                if (!string.Equals(log[i].TargetName, targetName, StringComparison.Ordinal))
                {
                    break;
                }

                count++;
            }

            return count;
        }

        /// <summary>
        /// How long after the newest sample the cooling row still describes the present.
        /// <para>
        /// The ramp records a sample per camera every 15 s and <b>records nothing outside the ramp</b>, so an
        /// older sample is not a current temperature -- it is where the camera was hours ago, at the start of
        /// the night. Presenting that as live would be the same lie as reporting a finished night's frame
        /// counters as a running one. Two minutes is eight missed samples: unambiguously over.
        /// </para>
        /// </summary>
        private static readonly TimeSpan CoolingFreshness = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Cooling as the furthest-off camera reports it, or null when nothing recent says anything about it.
        /// <para>
        /// The row is deliberately transient: it answers "is this rig cold yet", which only has a live answer
        /// while the ramp is running. Once the run has moved on to focusing or imaging, the PHASE already
        /// carries the answer -- a rig cannot reach those without having cooled -- so the row retires rather
        /// than lingering all night on a stale reading.
        /// </para>
        /// </summary>
        private static RigCardCooling? DescribeCooling(LiveSessionState session, DateTimeOffset now)
        {
            var samples = session.CoolingSamples;
            if (samples.IsDefaultOrEmpty)
            {
                return null;
            }

            // Append-ordered across all cameras, so the last entry is the newest of any of them.
            if (session.Phase is not SessionPhase.Cooling
                && now - samples[^1].Timestamp > CoolingFreshness)
            {
                return null;
            }

            // Newest sample per camera. The ramp is append-ordered and interleaves cameras, so one backwards
            // pass with a seen-set of indices beats filtering per camera.
            var seen = 0uL;
            var cameras = 0;
            var atSetpoint = 0;
            var worst = null as CoolingSample?;
            var worstDelta = double.NaN;

            for (var i = samples.Length - 1; i >= 0; i--)
            {
                var sample = samples[i];
                // 64 cameras is far beyond any real rig, and a bit mask keeps this allocation-free on a path
                // that runs per card per frame.
                if (sample.CameraIndex is < 0 or >= 64)
                {
                    continue;
                }

                var bit = 1uL << sample.CameraIndex;
                if ((seen & bit) != 0)
                {
                    continue;
                }

                seen |= bit;
                cameras++;

                var delta = Math.Abs(sample.TemperatureC - sample.SetpointTempC);
                if (delta <= RigCardCooling.SetpointToleranceC)
                {
                    atSetpoint++;
                }

                // A camera reporting no setpoint has a NaN delta, which cannot be compared -- so a KNOWN
                // delta always beats it, and it only decides the display when nothing comparable is there.
                // Every NaN comparison being false is what makes the naive "delta > worstDelta" wrong here:
                // a setpoint-less camera seen first would hold "worst" forever and hide a lagging one.
                if (worst is null || (!double.IsNaN(delta) && (double.IsNaN(worstDelta) || delta > worstDelta)))
                {
                    worstDelta = delta;
                    worst = sample;
                }
            }

            return worst is not { } lagging
                ? null
                : new RigCardCooling(
                    TemperatureC: lagging.TemperatureC,
                    SetpointC: lagging.SetpointTempC,
                    PowerPercent: lagging.CoolerPowerPercent,
                    AtSetpoint: atSetpoint,
                    CameraCount: cameras,
                    // The session's own answer, which outranks the arithmetic: while it says Cooling, it is
                    // still ramping however close the numbers happen to look.
                    IsRamping: session.Phase is SessionPhase.Cooling);
        }

        /// <summary>
        /// Median HFD of the last measured frame across OTAs, or null before anything has been measured.
        /// Worst (largest) of the OTAs, on the same "a rig is only as good as its weakest OTA" reading the
        /// cooling row uses.
        /// </summary>
        private static double? DescribeHfd(LiveSessionState session)
        {
            var worst = double.NaN;
            foreach (var metrics in session.LastFrameMetrics)
            {
                // NaN until a frame has been measured, and NaN loses every comparison -- so this naturally
                // reports null until at least one OTA has a real figure.
                if (metrics.MedianHfd > worst || double.IsNaN(worst))
                {
                    worst = metrics.MedianHfd;
                }
            }

            return double.IsNaN(worst) || worst <= 0 ? null : worst;
        }

        /// <summary>
        /// This app's own newest notification.
        /// <para>
        /// <b>The two feeds are ordered oppositely</b> and that is not a mistake to be tidied away here:
        /// <see cref="GuiAppState.Notifications"/> is newest-FIRST, while the node's ring crosses the wire
        /// oldest-first (matching how it is read for display). Each is indexed on its own terms.
        /// </para>
        /// </summary>
        private static RigCardNote? LocalNote(GuiAppState appState, DateTimeOffset now) =>
            appState.Notifications is { IsDefaultOrEmpty: false } feed
                ? new RigCardNote(feed[0].Severity, feed[0].Message, Age(feed[0].When, now))
                : null;

        /// <inheritdoc cref="LocalNote"/>
        private static RigCardNote? RemoteNote(NotificationDto? note, DateTimeOffset now) =>
            note is { } n
                ? new RigCardNote(
                    // The wire deliberately carries the severity as a string matching these names, so that
                    // the contracts assembly need not reference this one. Anything unrecognised reads as
                    // Info: a note is worth showing even when its severity is not understood.
                    Enum.TryParse<NotificationSeverity>(n.Severity, ignoreCase: true, out var severity)
                        ? severity
                        : NotificationSeverity.Info,
                    n.Message,
                    Age(n.TimestampUtc, now))
                : null;

        /// <summary>How long ago, or null for a stamp that is not in the past (clock skew between nodes).</summary>
        private static TimeSpan? Age(DateTimeOffset at, DateTimeOffset now) => now > at ? now - at : null;

        /// <summary>
        /// The status line for a rig that is answering: what the session says it is doing, falling back to
        /// the phase, and "Idle" when nothing is running at all.
        /// </summary>
        private static string DescribeActivity(LiveSessionState session) =>
            !session.HasActiveRun ? "Idle"
            : session.CurrentActivity is { Length: > 0 } activity ? activity
            : session.Phase.ToString();

        /// <summary>
        /// Projects an outstanding prompt onto its badge. <paramref name="now"/> is only consulted when the
        /// prompt carries a raised-at instant, so an unknown age stays unknown.
        /// </summary>
        private static RigCardPrompt? DescribePrompt(LiveSessionState session, DateTimeOffset now) =>
            session.PendingPrompt is { } prompt
                ? new RigCardPrompt(
                    prompt.Title,
                    prompt.RaisedUtc is { } raised && now > raised ? now - raised : null,
                    prompt.RequiresPhysicalPresence)
                : null;
    }
}
