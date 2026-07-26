using System;
using System.Collections.Immutable;
using System.Threading;

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

        /// <summary>The LAN.Lib stable node id for a remote rig; null for <see cref="ViewContextKind.Local"/>.</summary>
        public string? NodeId { get; }

        /// <summary>
        /// This context's session state. For the local context it is fed by
        /// <c>SessionBootstrapper</c> / <c>FlatsBootstrapper</c> and the preview telemetry poll; for a
        /// remote one it will be fed by a <c>RemoteSessionMirror</c> (both assign an
        /// <see cref="TianWen.Lib.Sequencing.ISessionTelemetry"/> to
        /// <see cref="LiveSessionState.ActiveSession"/> -- that is what the P3.1 split bought).
        /// </summary>
        public LiveSessionState LiveSession { get; } = new LiveSessionState();
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
        /// Polls every context's session telemetry, not just the visible one -- a local session hidden
        /// under a remote overlay must keep its phase, frame counts and mount pointing current.
        /// Cheap: <see cref="LiveSessionState.PollSession"/> returns immediately when a context has no
        /// session. Call once per frame.
        /// </summary>
        public void PollAll()
        {
            foreach (var context in _all)
            {
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
