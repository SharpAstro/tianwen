using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic Home tab: the landing screen, listing every rig this app can look at -- the local
    /// node and each bound remote one -- with live status (docs/plans/remote-profile.md, "Multi-rig
    /// dashboard").
    /// <para>
    /// <b>Read-only with respect to hardware.</b> A card click changes which rig you are LOOKING at, through
    /// the same two signals the profile picker posts, which is the same act as picking a rig there. It never
    /// connects local drivers, commands anything, or takes a device lease; driving a rig still means
    /// selecting it and using its tabs, so this is not a second way to command hardware.
    /// </para>
    /// <para>
    /// The whole screen is <see cref="HomeBoardLayout"/>'s single tree, rendered in ONE pass rooted at the
    /// content rect -- so the only geometry this class owns is how many grid columns fit, which the engine
    /// cannot decide for it.
    /// </para>
    /// </summary>
    public class HomeTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        /// <param name="now">Passed in rather than read from a clock here, so this tab has no time source of
        /// its own to disagree with the rest of the app. It resolves the flip countdown, which is why it is
        /// wanted per FRAME: the cards are rebuilt on the telemetry poll, and a countdown stored on one would
        /// only move when the poll did.</param>
        public void Render(GuiAppState appState, RectF32 contentRect, DateTimeOffset now)
        {
            // Drops last frame's clickable regions: the documented contract for a Render pass, and what every
            // other tab does.
            BeginFrame();

            // Design units, since the layout engine re-applies DpiScale itself. Columns, card detail and the
            // cards-versus-table decision are all resolved inside Build from these two numbers -- the tab
            // supplies the viewport and the user's choice, and decides nothing about shape itself.
            var scale = DpiScale > 0f ? DpiScale : 1f;

            // The cards are built once per frame by the telemetry poll, so the tab neither reaches into the
            // rig registry nor decides when a card is stale.
            // One rule however many cards there are: a click selects the rig to look at, a double-click opens
            // it. A lone card once opened on a single click, which made the same gesture mean two things.
            RenderLayout(
                HomeBoardLayout.Build(
                    appState.HomeCards, HomeBoardStyle.Default,
                    contentRect.Width / scale, now, contentRect.Height / scale,
                    appState.HomeBoardView, card => SelectAction(card, open: false), SelectViewAction,
                    GuiTheme.State, CycleThemeAction,
                    onOpen: card => SelectAction(card, open: true)),
                contentRect);
        }

        /// <summary>
        /// The tab a rig opens on, decided by what the rig needs next rather than by which kind of rig it is:
        /// <list type="bullet">
        /// <item>a run in progress, or a prompt waiting on someone: <see cref="GuiTab.LiveSession"/>, where
        /// the run is watched and the prompt answered;</item>
        /// <item>this computer with nothing configured (no profile, or a profile assigning no devices):
        /// <see cref="GuiTab.Equipment"/>, since there is nothing else to do with it yet;</item>
        /// <item>otherwise, configured and idle: <see cref="GuiTab.Planner"/>, the next step of an evening.</item>
        /// </list>
        /// A remote rig's device count is not knowable (<see cref="RigCard.Devices"/> is null for every one),
        /// so a remote rig is never judged unconfigured; its own node is where it gets set up.
        /// </summary>
        internal static GuiTab OpenTabFor(RigCard card)
        {
            if (card.IsRunning || card.Prompt is not null)
            {
                return GuiTab.LiveSession;
            }

            if (card.IsLocal && (card.Subtitle is null || card.Devices is { Assigned: 0 }))
            {
                return GuiTab.Equipment;
            }

            return GuiTab.Planner;
        }

        /// <summary>Posts the header selector's choice; the handler stores it and nothing else happens.</summary>
        private Action<InputModifier>? SelectViewAction(HomeBoardView view) =>
            _ => PostSignal(new SetHomeBoardViewSignal(view));

        /// <summary>
        /// Advances the theme. Routed through the signal rather than calling <see cref="GuiTheme"/> here, so
        /// the control and F12 land on one path and every host gets the same behaviour.
        /// </summary>
        private void CycleThemeAction(InputModifier _) => PostSignal(new CycleUiThemeSignal());

        /// <summary>
        /// Looking at a rig, not driving it. Local and remote go through the same two signals the profile
        /// picker posts, so there is exactly one path that changes the view context. With
        /// <paramref name="open"/> the signal also switches to the rig's tab (<see cref="OpenTabFor"/>),
        /// which is a change of tab and nothing more: no driver is connected and nothing is commanded.
        /// </summary>
        private Action<InputModifier>? SelectAction(RigCard card, bool open) => _ =>
        {
            GuiTab? openTab = open ? OpenTabFor(card) : null;
            if (card.IsLocal)
            {
                PostSignal(new SelectLocalContextSignal(openTab));
            }
            else
            {
                PostSignal(new SelectRemoteRigSignal(card.Title, openTab));
            }
        };
    }
}
