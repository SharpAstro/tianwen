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
        /// <summary>The screen's palette, resolved once from the shared theme.</summary>
        private static readonly HomeBoardStyle Style = new HomeBoardStyle(
            ContentBg:    GuiTheme.Palette.ContentBg,
            HeaderBg:     GuiTheme.Palette.HeaderBg,
            HeaderText:   GuiTheme.Palette.HeaderText,
            EmptyText:    new RGBAColor32(0x55, 0x55, 0x66, 0xff),
            CardBg:       GuiTheme.Palette.PanelBg,
            ViewedCardBg: GuiTheme.Palette.Selection,
            BodyText:     GuiTheme.Palette.BodyText,
            DimText:      GuiTheme.Palette.DimText,
            OnlineDot:    new RGBAColor32(0x55, 0xbb, 0x66, 0xff),
            OfflineDot:   new RGBAColor32(0x66, 0x66, 0x74, 0xff),
            RunningDot:   new RGBAColor32(0x55, 0x99, 0xdd, 0xff),
            PromptBg:     new RGBAColor32(0xbb, 0x88, 0x22, 0xff),
            PromptText:   new RGBAColor32(0x18, 0x14, 0x08, 0xff));

        public void Render(GuiAppState appState, RectF32 contentRect)
        {
            // Drops last frame's clickable regions: the documented contract for a Render pass, and what every
            // other tab does.
            BeginFrame();

            // How many columns fit is the one responsive decision, and it is a plain branch on this frame's
            // width -- design units, since the layout engine re-applies DpiScale itself.
            var designWidth = DpiScale > 0f ? contentRect.Width / DpiScale : contentRect.Width;
            var columns = HomeBoardLayout.ColumnsFor(designWidth - HomeBoardLayout.BodyPadding * 2f);

            // The cards are built once per frame by the telemetry poll, so the tab neither reaches into the
            // rig registry nor decides when a card is stale.
            RenderLayout(HomeBoardLayout.Build(appState.HomeCards, Style, columns, SelectAction), contentRect);
        }

        /// <summary>
        /// Looking at a rig, not driving it. Local and remote go through the same two signals the profile
        /// picker posts, so there is exactly one path that changes the view context.
        /// </summary>
        private Action<InputModifier>? SelectAction(RigCard card) => _ =>
        {
            if (card.IsLocal)
            {
                PostSignal(new SelectLocalContextSignal());
            }
            else
            {
                PostSignal(new SelectRemoteRigSignal(card.Title));
            }
        };
    }
}
