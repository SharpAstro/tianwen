using System;
using System.Collections.Immutable;
using System.Threading;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions
{
    /// <summary>What a <see cref="ViewContext"/> is looking at.</summary>
    public enum ViewContextKind
    {
        /// <summary>This node's own equipment and session. Exactly one exists, for the process lifetime.</summary>
        Local,

        /// <summary>A rig running <c>tianwen-server</c>, observed over the network.</summary>
        Remote
    }

    /// <summary>
    /// One "what am I looking at" context: the local node, or a remote rig.
    /// <para>
    /// Each context owns its own <see cref="LiveSessionState"/>, because a session belongs to the node
    /// whose hardware it drives -- this node runs at most one, and every rig runs its own
    /// (docs/plans/remote-profile.md, "View context is an overlay, not a rebind"). Selecting a rig
    /// changes which context the tabs render; it does <b>not</b> touch what this node owns, so the local
    /// session keeps running underneath with its state intact.
    /// </para>
    /// <para>
    /// A context's identity is stable for its lifetime, so a subscriber may capture
    /// <c>ViewContexts.Local</c> (or its <see cref="LiveSession"/>) once. <see cref="ViewContexts.Active"/>
    /// may <b>not</b> be captured -- resolve it per use.
    /// </para>
    /// </summary>
    public sealed class ViewContext
    {
        private ViewContext(ViewContextKind kind, string displayName, string? nodeId)
        {
            Kind = kind;
            DisplayName = displayName;
            NodeId = nodeId;
        }

        internal static ViewContext CreateLocal() => new ViewContext(ViewContextKind.Local, "This computer", null);

        internal static ViewContext CreateRemote(string nodeId, string displayName) =>
            new ViewContext(ViewContextKind.Remote, displayName, nodeId);

        /// <summary>Local node or remote rig.</summary>
        public ViewContextKind Kind { get; }

        /// <summary>Whether this is the local node's context.</summary>
        public bool IsLocal => Kind is ViewContextKind.Local;

        /// <summary>Human label for the chrome ("This computer", or the rig's announced name).
        /// Settable so a rediscovered rig can refresh its label without invalidating the context
        /// (bindings key on <see cref="NodeId"/>, never on the name).</summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// The node's stable id: a rig's LAN.Lib id, and this computer's own node's once it has been found or started (P6,
        /// <see cref="LocalNodeConnection"/>), which is how the rig picker and the Home board know to leave that node out.
        /// </summary>
        public string? NodeId { get; internal set; }

        /// <summary>
        /// This context's session state, fed by its node's <c>RemoteSessionMirror</c> (assigned to
        /// <see cref="LiveSessionState.ActiveSession"/> as an <see cref="TianWen.Lib.Sequencing.ISessionTelemetry"/>, which is
        /// what the P3.1 split bought) and its idle devices by its connection: this computer's node's since P6, as a rig's.
        /// </summary>
        public LiveSessionState LiveSession { get; } = new LiveSessionState();

        /// <summary>
        /// A rig's mirror while it is connected, and so how a control from its view reaches its node (P5b part 5): an
        /// abort, a flat run's cancel. Null for the local context, whose runs this computer owns, and for a rig not
        /// connected.
        /// <para>
        /// The context redraws whenever its mirror has something new (<see cref="RemoteSessionMirror.Changed"/>, P5b part
        /// 6): a local session's bootstrapper flags its redraws, and nothing flagged a rig's, which repainted only on the
        /// window's own tick.
        /// </para>
        /// </summary>
        public RemoteSessionMirror? Mirror
        {
            get => _mirror;
            internal set
            {
                if (_mirror is { } old)
                {
                    old.Changed -= OnMirrorChanged;
                }
                _mirror = value;
                if (value is not null)
                {
                    value.Changed += OnMirrorChanged;
                }
            }
        }

        private RemoteSessionMirror? _mirror;

        /// <summary>
        /// A rig's own profile, as the rig reads it (P5b part 8): the site its nights are planned at and its clock and
        /// twilight drawn in, and the sensor drawn at its pointing. Null for the local context, whose profile is the app's
        /// own (<see cref="GuiAppState.ActiveProfile"/>), and until a connected rig's has been read. Written by the rig's
        /// connection on a background task, read on the render thread, by one reference.
        /// </summary>
        public TianWen.Lib.Devices.Profile? RigProfile
        {
            get => Volatile.Read(ref _rigProfile);
            internal set => Volatile.Write(ref _rigProfile, value);
        }

        private TianWen.Lib.Devices.Profile? _rigProfile;

        private void OnMirrorChanged(object? sender, EventArgs e) => LiveSession.NeedsRedraw = true;

        /// <summary>
        /// A node's notes as this computer's feed shows notes, newest first (P5b part 6): its ring, then what it pushed
        /// (<see cref="RemoteSessionMirror.Notes"/>). This computer's own node's too since P6, which its feed merges with the
        /// app's own (<see cref="MergedWith"/>); empty for a view whose node is not connected. Mapped once per change of the mirror's
        /// notes, not per frame.
        /// </summary>
        public ImmutableArray<NotificationEntry> NodeNotes
        {
            get
            {
                if (Mirror is not { } mirror)
                {
                    return [];
                }

                var notes = mirror.Notes;
                if (Volatile.Read(ref _nodeNotes) is { } cached && cached.Source == notes)
                {
                    return cached.Entries;
                }

                var entries = ImmutableArray.CreateBuilder<NotificationEntry>(notes.Length);
                for (var i = notes.Length - 1; i >= 0; i--)
                {
                    entries.Add(NotificationEntry.FromNode(notes[i]));
                }
                var mapped = new NodeNotesCache(notes, entries.MoveToImmutable());
                // A reference swap: two render loops (the GUI's, the TUI's) mapping the same notes at once write the same
                // thing, so the last write winning is harmless.
                Volatile.Write(ref _nodeNotes, mapped);
                return mapped.Entries;
            }
        }

        private sealed record NodeNotesCache(ImmutableArray<TianWen.Hosting.Dto.NotificationDto> Source, ImmutableArray<NotificationEntry> Entries);

        private NodeNotesCache? _nodeNotes;

        /// <summary>
        /// This computer's notes as its feed shows them (P6 of docs/plans/hardware-in-the-server.md, #936): the app's own
        /// (<paramref name="own"/>: its device actions, its refusals) and its node's (<see cref="NodeNotes"/>: its runs, in
        /// the words a rig's feed uses), merged newest first, since the runs whose notes the app used to write are the node's.
        /// Merged once per change of either, not per frame.
        /// </summary>
        internal ImmutableArray<NotificationEntry> MergedWith(ImmutableArray<NotificationEntry> own)
        {
            var node = NodeNotes;
            if (node.IsEmpty)
            {
                return own;
            }
            if (Volatile.Read(ref _merged) is { } cached && cached.Own == own && cached.Node == node)
            {
                return cached.Entries;
            }

            var merged = ImmutableArray.CreateBuilder<NotificationEntry>(own.Length + node.Length);
            int o = 0, n = 0;
            while (o < own.Length || n < node.Length)
            {
                // Both newest first: take the newer head, this computer's first on a tie.
                if (n >= node.Length || o < own.Length && own[o].When >= node[n].When)
                {
                    merged.Add(own[o++]);
                }
                else
                {
                    merged.Add(node[n++]);
                }
            }
            var entries = merged.MoveToImmutable();
            Volatile.Write(ref _merged, new MergedNotesCache(own, node, entries));
            return entries;
        }

        private sealed record MergedNotesCache(ImmutableArray<NotificationEntry> Own, ImmutableArray<NotificationEntry> Node, ImmutableArray<NotificationEntry> Entries);

        private MergedNotesCache? _merged;
    }

    /// <summary>
    /// The notifications a view shows (P5b part 6): this computer's own on its own view, and on a rig's its node's notes,
    /// so the Notifications tab, like every other, shows what is on screen. ONE description for the GUI's tab and the TUI's.
    /// </summary>
    /// <param name="Entries">Newest first.</param>
    /// <param name="RigName">The rig the notes are from; null for this computer's own.</param>
    public readonly record struct NotificationFeed(ImmutableArray<NotificationEntry> Entries, string? RigName)
    {
        /// <summary>The feed of the view on show.</summary>
        public static NotificationFeed Of(ViewContext view, GuiAppState app) =>
            view.IsLocal ? new NotificationFeed(view.MergedWith(app.Notifications), null) : new NotificationFeed(view.NodeNotes, view.DisplayName);

        /// <summary>Whether these are this computer's own notes, the only ones it can clear: a rig's are its node's.</summary>
        public bool IsLocal => RigName is null;

        /// <summary>The tab's heading.</summary>
        public string Header => IsLocal ? $"Notifications ({Entries.Length})" : $"Notifications from {RigName} ({Entries.Length})";

        /// <summary>What the tab says with nothing to list.</summary>
        public string EmptyText => IsLocal ? "No notifications yet." : $"No notes from {RigName} yet.";
    }

    /// <summary>
    /// The set of view contexts and which one the tabs are currently rendering.
    /// <para>
    /// <b>Local vs Active is the load-bearing distinction</b>, and every consumer has to pick
    /// deliberately:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="Active"/> -- anything that <i>renders</i> or reads "what is on
    /// screen": the tabs, the sidebar icons, the window title.</description></item>
    /// <item><description><see cref="Local"/> -- anything that <i>owns or acts on this node's
    /// hardware</i>: the preview telemetry poll, the session/flats/polar bootstrappers, quit-time abort,
    /// and <see cref="TianWen.Lib.Devices.ProfileSwitchGate"/> (rebinding the local profile stays refused
    /// even while you are watching a rig).</description></item>
    /// <item><description><see cref="All"/> -- anything that must not miss a context that is off screen:
    /// telemetry polling and the redraw flag, so a local session keeps ticking (and keeps raising
    /// notifications) while a remote context is displayed.</description></item>
    /// </list>
    /// <para>
    /// Today exactly one context exists, so every choice above is observationally identical -- which is
    /// the point of making them explicit now rather than when a second context appears.
    /// </para>
    /// </summary>
    public sealed class ViewContexts
    {
        private ImmutableArray<ViewContext> _all;
        private ViewContext _active;
        private GuiAppState? _appState;

        public ViewContexts()
        {
            Local = ViewContext.CreateLocal();
            _all = [Local];
            _active = Local;
        }

        /// <summary>This node's context. Never null, never replaced -- safe to capture.</summary>
        public ViewContext Local { get; }

        /// <summary>
        /// The context the tabs render. Reads happen on the render thread while
        /// <see cref="Activate"/> runs from a signal handler, so the reference is published
        /// with a volatile write. <b>Do not capture</b> -- resolve per use.
        /// </summary>
        public ViewContext Active => Volatile.Read(ref _active);

        /// <summary>Every known context, local first. Atomic reference swap on add.</summary>
        public ImmutableArray<ViewContext> All => _all;

        /// <summary>Whether a remote rig is currently on screen (the local session, if any, is hidden underneath).</summary>
        public bool IsRemoteActive => !Active.IsLocal;

        /// <summary>
        /// The profile of the view on show (P5b parts 8 and 9 of docs/plans/hardware-in-the-server.md): a rig's own, read
        /// from its node (<see cref="ViewContext.RigProfile"/>, null until it has been), else this computer's,
        /// <paramref name="thisComputers"/>. ONE rule for every host and tab that lays a view out from a profile: the planner
        /// plans with it, and an idle Live Session lists its OTAs, so a rig's view never shows this computer's.
        /// </summary>
        public TianWen.Lib.Devices.Profile? ProfileOnShow(TianWen.Lib.Devices.Profile? thisComputers) => Active is { IsLocal: false } rig ? rig.RigProfile : thisComputers;

        /// <summary>
        /// Attaches the app-wide state to every context's <see cref="LiveSessionState"/> (and to any
        /// added later) so <see cref="LiveSessionState.SiteTimeZone"/> resolves. Called once during app
        /// composition.
        /// </summary>
        /// <remarks>
        /// A remote context should eventually resolve its site from the <i>rig's</i> profile rather than
        /// this node's -- deferred with the binding record (P4), since there is no remote profile to read
        /// a site from yet.
        /// </remarks>
        internal void AttachAppState(GuiAppState appState)
        {
            _appState = appState;
            foreach (var context in _all)
            {
                context.LiveSession.AttachAppState(appState);
            }
        }

        /// <summary>
        /// Returns the context for <paramref name="nodeId"/>, creating it if this is the first sighting.
        /// Idempotent, so a rediscovered rig re-attaches to the same context (and its session state)
        /// instead of losing it; the label is refreshed from <paramref name="displayName"/>.
        /// </summary>
        public ViewContext GetOrAddRemote(string nodeId, string displayName)
        {
            ArgumentException.ThrowIfNullOrEmpty(nodeId);

            foreach (var existing in _all)
            {
                if (string.Equals(existing.NodeId, nodeId, StringComparison.Ordinal))
                {
                    existing.DisplayName = displayName;
                    return existing;
                }
            }

            var context = ViewContext.CreateRemote(nodeId, displayName);
            if (_appState is { } appState)
            {
                context.LiveSession.AttachAppState(appState);
            }
            _all = _all.Add(context);
            return context;
        }

        /// <summary>
        /// Makes <paramref name="context"/> the on-screen context. Returns false (and changes nothing)
        /// for a context this set does not own. Never gated on session or device state -- switching the
        /// view is not a rebind (see <see cref="TianWen.Lib.Devices.ProfileSwitchGate"/> for the one that is).
        /// </summary>
        public bool Activate(ViewContext context)
        {
            if (!_all.Contains(context))
            {
                return false;
            }

            Volatile.Write(ref _active, context);
            return true;
        }

        /// <summary>
        /// The frames a rig on screen pulls, which are the ones its tab draws (P5b part 6): each OTA's on the Live Session
        /// tab, the guide camera's on the Guider tab, and none on any other, which draws no frame of the rig's.
        /// </summary>
        internal static PreviewOptions? FramesShownOn(GuiTab? tab) => tab switch
        {
            GuiTab.LiveSession => new PreviewOptions(IncludeOtas: true, IncludeGuider: false),
            GuiTab.Guider => new PreviewOptions(IncludeOtas: false, IncludeGuider: true),
            _ => null,
        };

        /// <summary>
        /// Polls every context's session telemetry, not just the visible one -- a local session hidden
        /// under a remote overlay must keep its phase, frame counts and mount pointing current.
        /// Cheap: <see cref="LiveSessionState.PollSession"/> returns immediately when a context has no
        /// session. Call once per frame.
        /// <para>
        /// <b>A remote rig's frames follow the SCREEN</b> (P4 of docs/plans/hardware-in-the-server.md): the rig on
        /// screen pulls the linear frames its tab draws (<see cref="FramesShownOn"/>), and every other rig pulls none and
        /// gives back what it holds, since N bound rigs each pulling full frames is the load the mirror's opt-in
        /// exists to prevent. Set on every poll rather than on <see cref="Activate"/> or a tab switch, so a rig connected
        /// while it is already on screen needs no second step.
        /// </para>
        /// </summary>
        public void PollAll()
        {
            var active = Active;
            var onScreen = FramesShownOn(_appState?.ActiveTab);
            foreach (var context in _all)
            {
                if (context.LiveSession.ActiveSession is RemoteSessionMirror mirror)
                {
                    mirror.Previews = ReferenceEquals(context, active) ? onScreen : null;
                }
                context.LiveSession.PollSession();
            }
        }

        /// <summary>True when any context wants a frame (an off-screen local session still drives the
        /// chrome's status and notification surfaces).</summary>
        public bool AnyNeedsRedraw
        {
            get
            {
                foreach (var context in _all)
                {
                    if (context.LiveSession.NeedsRedraw)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>Consumes the redraw flag on every context, after a frame has actually been drawn.</summary>
        public void ClearNeedsRedraw()
        {
            foreach (var context in _all)
            {
                context.LiveSession.NeedsRedraw = false;
            }
        }
    }
}
