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
            // With one card there is nothing to choose between, and a click that only re-selected the rig
            // already being looked at did nothing visible, so a lone card's click opens it.
            var openOnClick = appState.HomeCards.Length == 1;
            RenderLayout(
                HomeBoardLayout.Build(
                    appState.HomeCards, HomeBoardStyle.Default,
                    contentRect.Width / scale, now, contentRect.Height / scale,
                    appState.HomeBoardView, card => SelectAction(card, openOnClick), SelectViewAction,
                    GuiTheme.State, CycleThemeAction,
                    onOpen: card => SelectAction(card, open: true)),
                contentRect);
        }

        /// <summary>
        /// The tab a rig opens on: this computer's Equipment tab, where its hardware is set up, and a remote
        /// rig's Live Session, which is what its mirror is for.
        /// </summary>
        private static GuiTab OpenTabFor(RigCard card) => card.IsLocal ? GuiTab.Equipment : GuiTab.LiveSession;

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
