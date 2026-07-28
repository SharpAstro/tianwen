using System;
using System.Collections.Immutable;
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
        bool IsViewed)
    {
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
                IsViewed: isViewed);
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
                IsViewed: isViewed);
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
