using System;
using System.Collections.Immutable;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic Home tab: the landing screen, listing every rig this app can look at -- the local
    /// node and each bound remote one -- with live status (docs/plans/remote-profile.md, "Multi-rig
    /// dashboard").
    /// <para>
    /// <b>Read-only with respect to hardware.</b> A card click changes which rig you are LOOKING at (the
    /// existing view-context overlay), which is the same act as picking a rig from the profile picker. It
    /// never connects local drivers, commands anything, or takes a device lease; driving a rig still means
    /// selecting it and using its tabs, so this is not a second way to command hardware.
    /// </para>
    /// <para>
    /// <b>The rig section is content-sized, never Star-filled.</b> Cards flow in a
    /// <see cref="Layout.Builder.WrapH"/> and a trailing spacer absorbs the slack, because multi-night
    /// progress per target is the intended neighbour on this screen. A Star-sized section would have to be
    /// reworked to admit a second one, and every card would silently resize when it was.
    /// </para>
    /// </summary>
    public class HomeTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        private static readonly RGBAColor32 ContentBg  = GuiTheme.Palette.ContentBg;
        private static readonly RGBAColor32 HeaderBg   = GuiTheme.Palette.HeaderBg;
        private static readonly RGBAColor32 HeaderText = GuiTheme.Palette.HeaderText;
        private static readonly RGBAColor32 CardBg     = GuiTheme.Palette.PanelBg;
        private static readonly RGBAColor32 BodyText   = GuiTheme.Palette.BodyText;
        private static readonly RGBAColor32 DimText    = GuiTheme.Palette.DimText;

        private static readonly RGBAColor32 ActiveCardBg = GuiTheme.Palette.Selection;
        private static readonly RGBAColor32 OnlineDot    = new RGBAColor32(0x55, 0xbb, 0x66, 0xff);
        private static readonly RGBAColor32 OfflineDot   = new RGBAColor32(0x66, 0x66, 0x74, 0xff);
        private static readonly RGBAColor32 RunningDot   = new RGBAColor32(0x55, 0x99, 0xdd, 0xff);
        private static readonly RGBAColor32 PromptBg     = new RGBAColor32(0xbb, 0x88, 0x22, 0xff);
        private static readonly RGBAColor32 PromptText   = new RGBAColor32(0x18, 0x14, 0x08, 0xff);
        private static readonly RGBAColor32 EmptyText    = new RGBAColor32(0x55, 0x55, 0x66, 0xff);

        // Design units; PaintLayout re-applies DpiScale.
        private const float BaseFontSize = 13f;
        private const float BasePadding  = 12f;
        private const float CardWidth    = 250f;
        private const float CardHeight    = 116f;
        private const float CardRadius   = 6f;
        private const float CardGap      = 10f;

        public void Render(GuiAppState appState, RectF32 contentRect)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            var fontSize = BaseFontSize * dpiScale;
            var pad = BasePadding * dpiScale;
            var headerH = 28f * dpiScale;

            RenderLayout(Layout.Builder.Spacer().Bg(ContentBg), contentRect);
            RenderLayout(Layout.Builder.Spacer().Bg(HeaderBg),
                new RectF32(contentRect.X, contentRect.Y, contentRect.Width, headerH));

            // Built once per frame by the telemetry poll, so the tab neither reaches into the rig registry
            // nor decides when a card is stale.
            var cards = appState.HomeCards;
            var waiting = 0;
            foreach (var card in cards)
            {
                if (card.Prompt is not null) waiting++;
            }

            // The header states the one fact the board exists for, so it reads without scanning the cards.
            var headerLabel = waiting > 0
                ? $"Home · {waiting} rig{(waiting == 1 ? "" : "s")} waiting on you"
                : $"Home · {cards.Length} rig{(cards.Length == 1 ? "" : "s")}";
            DrawText(headerLabel.AsSpan(), fontPath,
                contentRect.X + pad, contentRect.Y, contentRect.Width - pad * 2f, headerH,
                fontSize * 1.05f, HeaderText, TextAlign.Near, TextAlign.Center);

            var bodyRect = new RectF32(
                contentRect.X + pad,
                contentRect.Y + headerH + pad,
                Math.Max(0f, contentRect.Width - pad * 2f),
                Math.Max(0f, contentRect.Height - headerH - pad * 2f));

            if (bodyRect.Width <= 0f || bodyRect.Height <= 0f)
            {
                return;
            }

            var cardNodes = new Layout.Node[cards.Length];
            for (var i = 0; i < cards.Length; i++)
            {
                cardNodes[i] = BuildCard(cards[i]);
            }

            var board = Layout.Builder.VStack(
                Layout.Builder.WrapH(cardNodes).WithGap(CardGap),
                // Absorbs the slack so the card section stays content-sized -- see the class remarks.
                Layout.Builder.Spacer().HStar()).WithGap(CardGap);

            RenderLayout(board, bodyRect);

            if (cards.Length == 0)
            {
                DrawText("No rigs yet.".AsSpan(), fontPath,
                    bodyRect.X, bodyRect.Y, bodyRect.Width, bodyRect.Height,
                    fontSize, EmptyText, TextAlign.Center, TextAlign.Center);
            }
        }

        /// <summary>
        /// One card: rig on top, the profile it runs underneath, then status and -- only while a run is
        /// live -- its counters. Clicking looks at that rig.
        /// </summary>
        private Layout.Node BuildCard(RigCard card)
        {
            var dot = !card.IsOnline ? OfflineDot : card.IsRunning ? RunningDot : OnlineDot;

            var rows = ImmutableArray.CreateBuilder<Layout.Node>(5);

            rows.Add(Layout.Builder.HStack(
                Layout.Builder.Text(card.Title, BaseFontSize * 1.05f, BodyText).WStar().HStar(),
                Layout.Builder.Box(8f, 8f, dot).WFixed(8f).HFixed(8f)).RowH(18f).WithGap(6f));

            // Title is the rig, subtitle is the profile it runs: the field that tells two similar rigs
            // apart. "Profile unknown" is stated rather than left blank, so an unlabelled card reads as a
            // rig we have not asked yet rather than a rig with no profile.
            rows.Add(Layout.Builder
                .Text(card.Subtitle ?? "profile unknown", BaseFontSize * 0.85f, DimText)
                .RowH(14f));

            rows.Add(Layout.Builder.Text(card.Status, BaseFontSize * 0.9f, BodyText).RowH(16f));

            if (card.IsRunning)
            {
                var counters = card.GuideRmsArcsec is { } rms
                    ? $"{card.FramesWritten} frames · RMS {rms:F2}\""
                    : $"{card.FramesWritten} frames";
                rows.Add(Layout.Builder.Text(counters, BaseFontSize * 0.85f, DimText).RowH(14f));

                if (card.Target is { Length: > 0 } target)
                {
                    rows.Add(Layout.Builder.Text(target, BaseFontSize * 0.85f, DimText).RowH(14f));
                }
            }

            if (card.Prompt is { } prompt)
            {
                // The badge is the only thing on a card that is a call to action rather than a status, so it
                // gets the one saturated colour on the screen.
                rows.Add(Layout.Builder
                    .Text(prompt.Describe(), BaseFontSize * 0.85f, PromptText, TextAlign.Center, TextAlign.Center)
                    .RowH(18f)
                    .Bg(PromptBg)
                    .Radius(3f));
            }

            var node = Layout.Builder.VStack([.. rows])
                .WFixed(CardWidth)
                .RowH(CardHeight)
                .WithGap(3f)
                .Pad(10f)
                .Bg(card.IsViewed ? ActiveCardBg : CardBg)
                .Radius(CardRadius);

            // Looking at a rig, not driving it. Local and remote go through the same signals the profile
            // picker already posts, so there is exactly one path that changes the view context.
            return node.Clickable(new HitResult.ButtonHit($"HomeRig:{card.Title}"), _ =>
            {
                if (card.IsLocal)
                {
                    PostSignal(new SelectLocalContextSignal());
                }
                else
                {
                    PostSignal(new SelectRemoteRigSignal(card.Title));
                }
            });
        }
    }
}
