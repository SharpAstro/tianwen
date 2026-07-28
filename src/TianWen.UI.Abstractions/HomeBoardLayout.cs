using System;
using System.Collections.Immutable;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Colours the home screen draws with, passed in rather than looked up so the tree builder stays free of
    /// any theme dependency and can be arranged in a test.
    /// </summary>
    public readonly record struct HomeBoardStyle(
        RGBAColor32 ContentBg,
        RGBAColor32 HeaderBg,
        RGBAColor32 HeaderText,
        RGBAColor32 EmptyText,
        RGBAColor32 CardBg,
        RGBAColor32 ViewedCardBg,
        RGBAColor32 BodyText,
        RGBAColor32 DimText,
        RGBAColor32 OnlineDot,
        RGBAColor32 OfflineDot,
        RGBAColor32 RunningDot,
        RGBAColor32 PromptBg,
        RGBAColor32 PromptText)
    {
        /// <summary>
        /// The shared board palette. One instance for every surface: the GPU tab and the TUI tab draw the
        /// same tree, so a second copy of these colours would be a second thing to keep in step.
        /// </summary>
        public static HomeBoardStyle Default { get; } = new HomeBoardStyle(
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
    }

    /// <summary>
    /// Builds the whole home screen as ONE <see cref="Layout.Node"/> tree, the way
    /// <see cref="EquipmentPanelLayout"/> builds the equipment panel: a static function from data to a tree,
    /// so every rect is arranged by the engine and assertable in a unit test.
    /// <para>
    /// <b>One pass, nothing hand-placed.</b> Background, header bar, header label, card flow and the
    /// trailing slack are all nodes in this tree -- there is no second <c>RenderLayout</c> at a
    /// hand-computed rect and no <c>DrawText</c> at hand-computed coordinates, so draw and hit cannot drift
    /// and a padding change cannot be made in one place and missed in another.
    /// </para>
    /// <para>
    /// <b>The cards are a grid, not a flow.</b> Columns are an even split of the width so cards line up
    /// vertically, and the grid runs in <see cref="Layout.Node.Grid.AutoRows"/> mode so each row takes its
    /// own cards' height: adding a card pushes a new row rather than shrinking every existing one, and the
    /// grid reports exactly its content height so the trailing spacer can take the slack. A <c>WrapH</c>
    /// flow was the first attempt and is wrong here -- fixed-width cards leave ragged space at the right
    /// edge and do not line up as a board.
    /// </para>
    /// <para>
    /// A card's height is therefore <c>Fixed</c> (it is what sizes its row) while its width is <c>Star</c>
    /// (it fills the column it is given) -- which is exactly what <c>RowH</c> means, so it is used
    /// deliberately here rather than by accident.
    /// </para>
    /// </summary>
    public static class HomeBoardLayout
    {
        /// <summary>
        /// Narrowest a card may get before the board drops a column. Cards stretch to their column, so this
        /// is a floor on the column width rather than the card's actual width.
        /// </summary>
        public const float MinCardWidth = 220f;

        /// <summary>Design-unit card height. Fixed, because it is what sizes the card's grid row.</summary>
        public const float CardHeight = 132f;

        /// <summary>Gap between columns, and between rows.</summary>
        public const float CardGap = 10f;

        /// <summary>Height of the full-bleed header bar.</summary>
        public const float HeaderHeight = 28f;

        /// <summary>Inset of the card area from the content edges.</summary>
        public const float BodyPadding = 12f;

        private const float BaseFontSize = 13f;
        private const float CardRadius = 12f;
        private const float CardPadding = 10f;
        private const float DotSize = 8f;

        /// <summary>
        /// The screen: a full-bleed header bar, then the card flow inset by <see cref="BodyPadding"/>, then a
        /// spacer that absorbs the slack.
        /// <para>
        /// <b>The card section is content-sized, not Star-filled.</b> The wrap takes only the height its
        /// lines need and the trailing spacer eats the rest, so multi-night progress can be added as a second
        /// section later without every card resizing. A Star-sized wrap would have to be reworked to admit
        /// one.
        /// </para>
        /// </summary>
        public static Layout.Node Build(
            ImmutableArray<RigCard> cards,
            HomeBoardStyle style,
            int columns,
            Func<RigCard, Action<InputModifier>?>? onSelect = null) =>
            Layout.Builder.VStack(
                    Header(cards, style),
                    Body(cards, style, columns, onSelect))
                .Bg(style.ContentBg);

        /// <summary>
        /// How many columns fit in <paramref name="availableWidthDesignUnits"/> (the content width less
        /// <see cref="BodyPadding"/> on both sides), at least one.
        /// <para>
        /// Resolved by the caller from the arranged width and passed in, because responsiveness here is a
        /// plain C# branch on a tree rebuilt every frame -- there is no media-query machinery to reach for,
        /// and the layout engine has no notion of "as many columns as fit".
        /// </para>
        /// </summary>
        public static int ColumnsFor(float availableWidthDesignUnits) =>
            Math.Max(1, (int)((availableWidthDesignUnits + CardGap) / (MinCardWidth + CardGap)));

        /// <summary>
        /// The header states the one fact the board exists for -- how many rigs are waiting on somebody -- so
        /// it reads without scanning the cards.
        /// </summary>
        private static Layout.Node Header(ImmutableArray<RigCard> cards, HomeBoardStyle style)
        {
            var waiting = 0;
            foreach (var card in cards)
            {
                if (card.Prompt is not null) waiting++;
            }

            var label = waiting > 0
                ? $"Home · {waiting} rig{(waiting == 1 ? "" : "s")} waiting on you"
                : $"Home · {cards.Length} rig{(cards.Length == 1 ? "" : "s")}";

            // A leading spacer rather than padding on the text, so the bar's background stays full-bleed
            // while the label lines up with the cards inset below it.
            return Layout.Builder.HStack(
                    Layout.Builder.Spacer().WFixed(BodyPadding).HStar(),
                    Layout.Builder.Text(label, BaseFontSize * 1.05f, style.HeaderText, TextAlign.Near, TextAlign.Center)
                        .WStar().HStar())
                .RowH(HeaderHeight)
                .Bg(style.HeaderBg);
        }

        private static Layout.Node Body(
            ImmutableArray<RigCard> cards, HomeBoardStyle style, int columns,
            Func<RigCard, Action<InputModifier>?>? onSelect)
        {
            if (cards.IsDefaultOrEmpty)
            {
                return Layout.Builder
                    .Text("No rigs yet.", BaseFontSize, style.EmptyText, TextAlign.Center, TextAlign.Center)
                    .WStar().HStar();
            }

            var cardNodes = new Layout.Node[cards.Length];
            for (var i = 0; i < cards.Length; i++)
            {
                cardNodes[i] = Card(cards[i], style, onSelect);
            }

            return Layout.Builder.VStack(
                    Layout.Builder.Grid(Math.Max(1, columns), cardNodes)
                        .WithGaps(CardGap, CardGap)
                        .WithAutoRows(),
                    Layout.Builder.Spacer().HStar())
                .WithGap(CardGap)
                .Pad(BodyPadding)
                .HStar();
        }

        /// <summary>
        /// One card: the rig, the profile it runs, a status line, then -- only while a run is live -- its
        /// counters and target, and a prompt badge when one is outstanding.
        /// </summary>
        public static Layout.Node Card(
            RigCard card,
            HomeBoardStyle style,
            Func<RigCard, Action<InputModifier>?>? onSelect = null)
        {
            var dot = !card.IsOnline ? style.OfflineDot : card.IsRunning ? style.RunningDot : style.OnlineDot;
            var rows = ImmutableArray.CreateBuilder<Layout.Node>(6);

            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text(card.Title, BaseFontSize * 1.05f, style.BodyText).WStar().HStar(),
                    Layout.Builder.Box(DotSize, DotSize, dot).WFixed(DotSize).HFixed(DotSize))
                .RowH(18f).WithGap(6f));

            // Title is the rig, subtitle is the profile it runs -- the field that tells two similar rigs
            // apart. "Profile unknown" is stated rather than left blank, so an unlabelled card reads as a rig
            // we have not asked yet rather than a rig with no profile.
            rows.Add(Layout.Builder
                .Text(card.Subtitle ?? "profile unknown", BaseFontSize * 0.85f, style.DimText)
                .RowH(14f));

            rows.Add(Layout.Builder.Text(card.Status, BaseFontSize * 0.9f, style.BodyText).RowH(16f));

            // The plug row: dim when something is still unplugged, normal when the rig is fully connected, so
            // the card answers "did Connect All do anything" without reading the number.
            if (card.Devices is { } devices)
            {
                rows.Add(Layout.Builder
                    .Text(devices.Describe(), BaseFontSize * 0.85f,
                        devices.AllConnected ? style.BodyText : style.DimText)
                    .RowH(14f));
            }

            if (card.IsRunning)
            {
                var counters = card.GuideRmsArcsec is { } rms
                    ? $"{card.FramesWritten} frames · RMS {rms:F2}\""
                    : $"{card.FramesWritten} frames";
                rows.Add(Layout.Builder.Text(counters, BaseFontSize * 0.85f, style.DimText).RowH(14f));

                if (card.Target is { Length: > 0 } target)
                {
                    rows.Add(Layout.Builder.Text(target, BaseFontSize * 0.85f, style.DimText).RowH(14f));
                }
            }

            if (card.Prompt is { } prompt)
            {
                // The only thing on a card that is a call to action rather than a status, so it gets the one
                // saturated colour on the screen.
                rows.Add(Layout.Builder
                    .Text(prompt.Describe(), BaseFontSize * 0.85f, style.PromptText, TextAlign.Center, TextAlign.Center)
                    .RowH(18f)
                    .Bg(style.PromptBg)
                    .Radius(6f));
            }

            return Layout.Builder.VStack([.. rows])
                .RowH(CardHeight)
                .WithGap(3f)
                .Pad(CardPadding)
                .Bg(card.IsViewed ? style.ViewedCardBg : style.CardBg)
                .Radius(CardRadius)
                .Clickable(new HitResult.ButtonHit($"HomeRig:{card.Title}"), onSelect?.Invoke(card));
        }
    }
}
