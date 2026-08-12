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
        RGBAColor32 PromptText,
        RGBAColor32 WarnText,
        RGBAColor32 ErrorText)
    {
        /// <summary>
        /// The shared board palette. One instance for every surface: the GPU tab and the TUI tab draw the
        /// same tree, so a second copy of these colours would be a second thing to keep in step.
        /// </summary>
        /// <remarks>
        /// A computed property, NOT a <c>static readonly</c> initialiser. It used to be the latter,
        /// which snapshots the palette at type-init and then never moves again -- so the board would
        /// have kept its old colours through every theme switch, silently and only on this screen.
        /// <see cref="HomeBoardStyle"/> is a struct, so recomputing per access costs a copy and no
        /// allocation.
        /// </remarks>
        public static HomeBoardStyle Default
        {
            get
            {
                var p = GuiTheme.Palette;
                return new HomeBoardStyle(
                    ContentBg:    p.ContentBg,
                    HeaderBg:     p.HeaderBg,
                    HeaderText:   p.HeaderText,
                    EmptyText:    p.DimText,
                    CardBg:       p.PanelBg,
                    ViewedCardBg: p.Selection,
                    BodyText:     p.BodyText,
                    DimText:      p.DimText,
                    // Online is the one dot with a conventional colour (green) that a palette may be
                    // unable to spend -- Night cannot -- so it goes through the Success role, which
                    // falls back to the accent exactly there.
                    OnlineDot:    p.Success,
                    OfflineDot:   p.DimText,
                    RunningDot:   p.Info,
                    PromptBg:     p.Warn,
                    PromptText:   Ink(p.Warn),
                    // Shared with the notifications feed's row stripes, so one warning is one colour app-wide.
                    WarnText:     p.Warn,
                    ErrorText:    p.Error);
            }
        }

        // Ink-on-fill moved to GuiTheme.InkOn when the Connect All button became the second caller;
        // a second copy of the rule is how one of them drifts.
        private static RGBAColor32 Ink(RGBAColor32 fill) => GuiTheme.InkOn(fill);
    }

    /// <summary>
    /// How much of a card to draw. The board narrows before it drops a column, so at the tightest column
    /// width the two extras that are nice-to-know rather than need-to-know come off.
    /// <para>
    /// Deliberately a small, blunt distinction rather than a per-row priority list: two levels can be
    /// reasoned about and tested, and the rows that survive are the ones that say what a rig is doing, how
    /// far through it is, and whether it needs a human.
    /// </para>
    /// </summary>
    public enum RigCardDetail
    {
        /// <summary>Everything, including median HFD and the last notification.</summary>
        Full,

        /// <summary>Drops the last-notification line and the HFD figure; guide RMS stays.</summary>
        Compact
    }

    /// <summary>
    /// Which shape the board draws in.
    /// <para>
    /// <b>Two shapes rather than a card that shrinks.</b> A card that keeps halving to fit ends up saying
    /// almost nothing while still costing a card's worth of chrome; one row per rig says more in a third of
    /// the space, which is the whole reason tables exist. Both are built from the same
    /// <see cref="RigCard"/> data by this class, so neither surface knows there are two -- the shared tree is
    /// a description language, not a fixed shape.
    /// </para>
    /// <para>
    /// <b>Not a stack of overlapping cards.</b> That was the other candidate for the cramped case, and it
    /// hides rigs behind other rigs. The prompt badge is the one thing on this screen that must never be
    /// hidden -- it is what the board exists to answer, two rigs can be waiting at once so only one could be
    /// at the front, and what you could see would depend on animation timing.
    /// </para>
    /// </summary>
    public enum HomeBoardView
    {
        /// <summary>
        /// Cards while they fit, the table once they do not. The default, and the only value that reacts to
        /// the window: it is what makes a shrunk window degrade into something readable instead of a board
        /// running off the bottom edge.
        /// </summary>
        Auto,

        /// <summary>Always cards, even when that overflows. An explicit choice is not second-guessed.</summary>
        Cards,

        /// <summary>Always the table -- one row per rig, which is what you want past a handful of rigs.</summary>
        Table
    }

    /// <summary>
    /// How the theme control presents itself. A code-level choice, not a user setting: a host knows how much
    /// header it has, and a reader should never have to configure their way to a legible control.
    /// <para>
    /// <b><see cref="IconOnly"/> is a real option and is the wrong default</b>, which is worth stating
    /// because it is the obvious one. Four states share three marks: <see cref="UiThemeState.Night"/> is a
    /// dark scheme, so it takes the same crescent as <see cref="UiThemeState.Dark"/>, and inside Night the
    /// entire UI is red, so the colour that would otherwise separate them says nothing. A control whose only
    /// job is telling an observer at the mount which scheme they are in cannot be ambiguous about exactly
    /// that pair. Keep it for a header too tight for anything else, where two dark states reading alike
    /// beats the control being dropped.
    /// </para>
    /// </summary>
    public enum ThemeControlStyle
    {
        /// <summary>The mark plus the state's name. The default: the mark carries the family at a glance and
        /// the word settles Dark from Night.</summary>
        IconAndLabel,

        /// <summary>The mark alone, at one segment's width. See the type remarks before choosing it.</summary>
        IconOnly,

        /// <summary>The word alone. Unambiguous, but a text pill beside three pictograms reads as a
        /// different species of control.</summary>
        LabelOnly
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

        /// <summary>
        /// Design-unit <b>minimum</b> card height. A card is as tall as the rows it built, so this only keeps
        /// a rig with little to report looking like a card rather than a label. It used to be the exact height
        /// and had to be raised by hand whenever a row was added, which silently clipped the last row when it
        /// was not.
        /// </summary>
        public const float CardHeight = 132f;

        /// <summary>Narrowest a card may be and still show its collapsible extras -- see <see cref="DetailFor"/>.</summary>
        public const float FullDetailCardWidth = 260f;

        private const float TitleRowHeight = 18f;
        private const float StatusRowHeight = 16f;
        private const float DetailRowHeight = 14f;
        private const float RowGap = 3f;

        /// <summary>Gap between columns, and between rows.</summary>
        public const float CardGap = 10f;

        /// <summary>Height of the full-bleed header bar.</summary>
        public const float HeaderHeight = 28f;

        /// <summary>
        /// Slack left above and below the header bar's controls, which is what separates them from the app's
        /// own top bar directly above.
        /// <para>
        /// Both bars paint <c>Palette.HeaderBg</c>, so controls that stop short of the bar's edge leave a
        /// band of that same colour above them: space without a seam. An earlier attempt put a strip of
        /// <c>ContentBg</c> between the two bars instead, which reads as a dark rule drawn across the window.
        /// </para>
        /// <para>
        /// The controls are CENTRED by <c>Layout.CrossAlign.Center</c> (DIR.Lib 7.21), not by this number. A
        /// Stack used to place every child at the cross-axis start, so a Fixed-height button in a taller bar
        /// hugged the top and sat half the slack high; the workaround here was to pad the bar and make every
        /// child Star-height, which also inset the label horizontally as a side effect. The engine knows both
        /// extents, so it is the right place for the arithmetic.
        /// </para>
        /// </summary>
        public const float HeaderControlSlack = 4f;

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
        /// <param name="width">Content width in DESIGN units. Columns and card detail are resolved in here
        /// rather than by the caller: both hosts were running the same three-step arithmetic
        /// (<see cref="ColumnsFor"/> then <see cref="ColumnWidth"/> then <see cref="DetailFor"/>) and a fourth
        /// input would have had to be threaded through both again.</param>
        /// <param name="height">Content height in design units, which is what <see cref="HomeBoardView.Auto"/>
        /// decides on. <see cref="float.PositiveInfinity"/> means "unbounded", so a caller that genuinely does
        /// not know (a test arranging into a tall surface) never gets the table by accident.</param>
        /// <param name="now">Resolves each card's flip countdown -- see <see cref="Card"/>.</param>
        /// <param name="view">The user's choice from the header selector; Auto reacts to <paramref name="height"/>.</param>
        /// <param name="onSelectView">Posts the header selector's choice, or null to draw no selector -- which
        /// is what a caller that has nowhere to persist the choice should do.</param>
        /// <param name="theme">Which of the four theme states the control SHOWS. Passed in rather than read
        /// off <see cref="GuiTheme"/> for the same reason <paramref name="style"/> is: this builder stays a
        /// pure function of its arguments, so a test can arrange any state without touching global state.</param>
        /// <param name="onCycleTheme">Advances the theme, or null to draw no theme control.</param>
        /// <param name="themeControl">How that control presents itself; see <see cref="ThemeControlStyle"/>
        /// before reaching for <see cref="ThemeControlStyle.IconOnly"/>.</param>
        public static Layout.Node Build(
            ImmutableArray<RigCard> cards,
            HomeBoardStyle style,
            float width,
            DateTimeOffset now,
            float height = float.PositiveInfinity,
            HomeBoardView view = HomeBoardView.Auto,
            Func<RigCard, Action<InputModifier>?>? onSelect = null,
            Func<HomeBoardView, Action<InputModifier>?>? onSelectView = null,
            UiThemeState theme = UiThemeState.Dark,
            Action<InputModifier>? onCycleTheme = null,
            ThemeControlStyle themeControl = ThemeControlStyle.IconAndLabel)
        {
            var cardArea = width - BodyPadding * 2f;
            var columns = ColumnsFor(cardArea, cards.Length);

            Layout.Node body;
            var fellBack = false;

            if (view is HomeBoardView.Table)
            {
                body = Table(cards, style, now, onSelect);
            }
            else
            {
                // Auto falls back to the table when the cards would not fit; an explicit Cards does not,
                // because overriding a choice the user just made is worse than an overflowing board.
                var cardsFitWithin = view is HomeBoardView.Auto
                    ? height - HeaderHeight - BodyPadding * 2f
                    : float.PositiveInfinity;

                body = Body(cards, style, columns, now, DetailFor(ColumnWidth(cardArea, columns)), onSelect,
                    cardsFitWithin, out fellBack);

                if (fellBack)
                {
                    body = Table(cards, style, now, onSelect);
                }
            }

            return Layout.Builder.VStack(
                    Header(cards, style, view, fellBack, onSelectView, theme, onCycleTheme, themeControl),
                    body)
                .Bg(style.ContentBg);
        }

        /// <summary>
        /// How many columns fit in <paramref name="availableWidthDesignUnits"/> (the content width less
        /// <see cref="BodyPadding"/> on both sides), at least one.
        /// <para>
        /// Resolved by the caller from the arranged width and passed in, because responsiveness here is a
        /// plain C# branch on a tree rebuilt every frame -- there is no media-query machinery to reach for,
        /// and the layout engine has no notion of "as many columns as fit".
        /// </para>
        /// </summary>
        /// <param name="cardCount">
        /// Caps the answer: <b>a board never has more columns than rigs.</b> Without this, a wide window
        /// resolves to as many columns as physically fit -- 6 on a 200-column terminal -- so a four-rig board
        /// is laid out in six, leaving two empty columns AND squeezing the four real cards below the width at
        /// which they show full detail. The cards get narrower the wider the window is, which is precisely
        /// backwards.
        /// </param>
        public static int ColumnsFor(float availableWidthDesignUnits, int cardCount) =>
            Math.Clamp((int)((availableWidthDesignUnits + CardGap) / (MinCardWidth + CardGap)), 1, Math.Max(1, cardCount));

        /// <summary>
        /// How much of a card fits, from the width one column actually got.
        /// <para>
        /// Takes the resolved column width rather than the screen width, because that is what a card has to
        /// live in: six rigs on a wide monitor give narrow columns and want the compact card, while one rig on
        /// a small window gets a wide column and can afford everything.
        /// </para>
        /// </summary>
        public static RigCardDetail DetailFor(float columnWidthDesignUnits) =>
            columnWidthDesignUnits >= FullDetailCardWidth ? RigCardDetail.Full : RigCardDetail.Compact;

        /// <summary>
        /// The width one column gets, for feeding <see cref="DetailFor"/>. Mirrors how the grid divides the
        /// card area, so the two cannot disagree about how wide a card is.
        /// </summary>
        public static float ColumnWidth(float availableWidthDesignUnits, int columns) =>
            columns <= 0 ? availableWidthDesignUnits
            : (availableWidthDesignUnits - CardGap * (columns - 1)) / columns;

        /// <summary>
        /// The header states the one fact the board exists for -- how many rigs are waiting on somebody -- so
        /// it reads without scanning the cards.
        /// </summary>
        /// <param name="fellBackToTable">Whether Auto overrode the cards this frame, so the header can SAY so.
        /// A shape that changes under you with no explanation reads as a glitch; naming it turns the same
        /// event into the nudge that the window is too small.</param>
        private static Layout.Node Header(
            ImmutableArray<RigCard> cards, HomeBoardStyle style, HomeBoardView view, bool fellBackToTable,
            Func<HomeBoardView, Action<InputModifier>?>? onSelectView,
            UiThemeState theme, Action<InputModifier>? onCycleTheme,
            ThemeControlStyle themeControl)
        {
            var waiting = 0;
            foreach (var card in cards)
            {
                if (card.Prompt is not null) waiting++;
            }

            var label = waiting > 0
                ? $"Home · {waiting} rig{(waiting == 1 ? "" : "s")} waiting on you"
                : $"Home · {cards.Length} rig{(cards.Length == 1 ? "" : "s")}";

            if (fellBackToTable)
            {
                label += " · table (window too small for cards)";
            }

            // A leading spacer rather than padding on the text, so the bar's background stays full-bleed
            // while the label lines up with the cards inset below it.
            var children = ImmutableArray.CreateBuilder<Layout.Node>(8);
            children.Add(Layout.Builder.Spacer().WFixed(BodyPadding).HStar());
            children.Add(Layout.Builder
                .Text(label, BaseFontSize * 1.05f, style.HeaderText, TextAlign.Near, TextAlign.Center)
                .WStar().HStar());

            if (onSelectView is not null)
            {
                foreach (var option in ViewOptions)
                {
                    children.Add(ViewButton(option, view, style, onSelectView));
                }
            }

            if (onCycleTheme is not null)
            {
                // Set off from the view segments, which are one control: adjacent buttons at the same gap
                // would read as a four-cell selector whose fourth cell does something unrelated.
                children.Add(Layout.Builder.Spacer().WFixed(8f).HStar());
                children.Add(ThemeButton(theme, style, themeControl, onCycleTheme));
            }

            if (onSelectView is not null || onCycleTheme is not null)
            {
                children.Add(Layout.Builder.Spacer().WFixed(BodyPadding).HStar());
            }

            return Layout.Builder.HStack([.. children])
                .RowH(HeaderHeight)
                .CrossCenter()
                .WithGap(4f)
                .Bg(style.HeaderBg);
        }

        /// <summary>Width of the label-only control, sized for its longest label ("System").</summary>
        private const float ThemeLabelWidth = 56f;

        /// <summary>Width of the icon-plus-label control: a square icon cell, the gap, then the word.</summary>
        private const float ThemeIconLabelWidth = 76f;

        /// <summary>
        /// Which mark stands for a state. Three marks cover four states because
        /// <see cref="UiThemeState.Night"/> IS a dark scheme; the label is what separates the two, which is
        /// why <see cref="ThemeControlStyle.IconOnly"/> carries the caveat it does.
        /// </summary>
        private static Layout.IconKind IconFor(UiThemeState theme) => theme switch
        {
            UiThemeState.Light => Layout.IconKind.ThemeLight,
            UiThemeState.System => Layout.IconKind.ThemeSystem,
            _ => Layout.IconKind.ThemeDark,
        };

        /// <summary>
        /// The four-state theme control: one button that SHOWS the current state and advances on click.
        /// <para>
        /// It shows the state it is IN rather than the one a click reaches, because with four states no
        /// single mark can say "what happens next", and the reader's actual question is which scheme they
        /// are looking at.
        /// </para>
        /// </summary>
        private static Layout.Node ThemeButton(
            UiThemeState theme, HomeBoardStyle style, ThemeControlStyle presentation,
            Action<InputModifier>? onCycleTheme)
        {
            var font = BaseFontSize * 0.85f;

            Layout.Node Label(TextAlign hAlign) =>
                Layout.Builder.Text(theme.Label(), font, style.BodyText, hAlign, TextAlign.Center);

            var (content, width) = presentation switch
            {
                ThemeControlStyle.LabelOnly => (Label(TextAlign.Center), ThemeLabelWidth),
                ThemeControlStyle.IconOnly => (
                    Layout.Builder.Icon(IconFor(theme), ViewIconSize, style.BodyText), ViewButtonWidth),
                // The icon takes a SQUARE cell and the label the remainder. Splitting the pill by eye
                // instead squeezes the mark: these are drawn to a square, so a 13-unit icon in an 18-unit
                // cell is drawn at 13 and the sun's rays lose their gap first.
                _ => (Layout.Builder.HStack(
                        Layout.Builder.Icon(IconFor(theme), ThemeIconSize, style.BodyText)
                            .WFixed(ControlHeight).HStar(),
                        Label(TextAlign.Near).WStar().HStar())
                    .WithGap(2f), ThemeIconLabelWidth),
            };

            return content
                // HFixed rather than RowH, for the reason spelt out in ViewButton.
                .WFixed(width)
                .HFixed(ControlHeight)
                .Radius(4f)
                .Bg(style.ViewedCardBg)
                // Named by the state it currently SHOWS, not the one a click reaches: that is what an
                // inspector snapshot and a test both want to assert, and it matches the label.
                .Clickable(new HitResult.ButtonHit($"HomeTheme:{theme}"), onCycleTheme);
        }

        /// <summary>Selector order, and the single list both the buttons and the tests read.</summary>
        private static readonly ImmutableArray<HomeBoardView> ViewOptions =
            [HomeBoardView.Auto, HomeBoardView.Cards, HomeBoardView.Table];

        /// <summary>
        /// Height of every control in the header bar: the bar less its padding on both edges. One constant,
        /// because the requirement is that they share a top and a bottom, and three call sites computing the
        /// same difference is how one of them ends up a unit off.
        /// </summary>
        private const float ControlHeight = HeaderHeight - HeaderControlSlack * 2f;

        /// <summary>
        /// Width of one selector segment. SQUARE, hence the same constant as the height: a pictogram button
        /// wider than it is tall reads as a text button someone forgot to label, and the three sit side by
        /// side where any inconsistency is obvious. Square is also most of what the icons bought: three
        /// words cost 162 units of a header that also has to hold the rig count, which is the thing the
        /// board exists to state.
        /// </summary>
        private const float ViewButtonWidth = ControlHeight;

        /// <summary>
        /// Mark size inside a shape segment, inset within the square button rather than filling it, so the
        /// selected segment's fill reads as a button carrying a mark. DIR.Lib 7.20 draws an icon at the size
        /// it DECLARES rather than stretching it to its cell, so this is the real drawn size and not merely
        /// an intrinsic hint.
        /// </summary>
        private const float ViewIconSize = 14f;

        /// <summary>
        /// Mark size inside the theme pill, which is SMALLER than a segment's because it sits beside a word.
        /// Measured rather than guessed: at 13 the crescent inks about 8.75 design units against the label's
        /// 9.75 of cap height, so the mark reads as part of the same line. At the segment's 20 it stood 38%
        /// taller than the word and looked vertically misaligned even though both were centred on the row.
        /// </summary>
        private const float ThemeIconSize = 13f;

        /// <summary>
        /// What each shape LOOKS like, which is the whole reason <see cref="Layout.Content.Icon"/> names a
        /// meaning rather than a drawing: the GPU board gets rectangles and the TUI gets a block-element
        /// glyph from one tree.
        /// </summary>
        private static Layout.IconKind IconFor(HomeBoardView view) => view switch
        {
            HomeBoardView.Cards => Layout.IconKind.Grid,
            HomeBoardView.Table => Layout.IconKind.List,
            _ => Layout.IconKind.Auto,
        };

        /// <summary>
        /// One segmented-selector button. Segments rather than a dropdown: there are three, and the current
        /// one has to be readable at a glance from across the room -- a dropdown would hide two of the three
        /// behind a click and still cost the same width.
        /// <para>
        /// <b>All three stay visible, Auto included.</b> Auto is a real state and the default one, so
        /// hiding it behind "no segment lit" would make the board's most common configuration the one with
        /// no indication of what it is doing. The camera-style bracketed A is the affordance that makes it
        /// showable at icon size at all.
        /// </para>
        /// </summary>
        private static Layout.Node ViewButton(
            HomeBoardView option, HomeBoardView selected, HomeBoardStyle style,
            Func<HomeBoardView, Action<InputModifier>?> onSelectView)
        {
            var isSelected = option == selected;
            var node = Layout.Builder
                .Icon(IconFor(option), ViewIconSize, isSelected ? style.BodyText : style.DimText)
                // HFixed, NOT RowH: RowH is "a full-width row of fixed height" and sets Width = Star, which
                // silently discards the WFixed above it. That is what the segments were doing before the
                // icons landed -- ViewButtonWidth was inert and the three buttons sprawled across the whole
                // header, which is only obvious once you look at an arranged rect. Same trap the card's
                // width hit (see HomeTabLayoutTests' class remarks).
                .WFixed(ViewButtonWidth)
                .HFixed(ControlHeight)
                .Radius(4f)
                .Clickable(new HitResult.ButtonHit($"HomeView:{option}"), onSelectView(option));

            // Only the selected segment is filled; an unselected one is bare so the row reads as one control
            // with a current value rather than three separate buttons.
            return isSelected ? node.Bg(style.ViewedCardBg) : node;
        }

        /// <summary>
        /// One row per rig: the same facts a card carries, laid out as columns.
        /// <para>
        /// Built from the same <see cref="RigCard"/> data by the same builder, so a fact added to the card is
        /// added here too or the omission is visible in one file -- and both surfaces get the table with no
        /// per-surface code, exactly as they get the cards.
        /// </para>
        /// <para>
        /// Columns past the first two <see cref="Layout.Node.CollapseBelow"/> their minimum, so a narrow window
        /// drops whole columns instead of truncating every cell into ambiguity. The prompt badge is NOT among
        /// them: it is the one column that must survive any width.
        /// </para>
        /// </summary>
        public static Layout.Node Table(
            ImmutableArray<RigCard> cards,
            HomeBoardStyle style,
            DateTimeOffset now,
            Func<RigCard, Action<InputModifier>?>? onSelect = null)
        {
            if (cards.IsDefaultOrEmpty)
            {
                return Layout.Builder
                    .Text("No rigs yet.", BaseFontSize, style.EmptyText, TextAlign.Center, TextAlign.Center)
                    .WStar().HStar();
            }

            var rows = ImmutableArray.CreateBuilder<Layout.Node>(cards.Length + 1);
            rows.Add(TableHeaderRow(style));

            foreach (var card in cards)
            {
                rows.Add(TableRow(card, style, now, onSelect));
            }

            rows.Add(Layout.Builder.Spacer().HStar());

            // WStar is not optional here: a Node's default Width is Sizing.Auto, and a VStack of rows whose
            // cells are all Star measures to a near-zero intrinsic width. The table would then be handed
            // almost no width, every column would fall under its collapse threshold, and the whole thing
            // would arrange down to nothing but the min-clamped prompt badge.
            return Layout.Builder.VStack([.. rows])
                .WithGap(2f)
                .Pad(BodyPadding)
                .WStar()
                .HStar();
        }

        // Which rig it is and what it is doing are the row; everything else is detail that may come off.
        // Mandatory columns pass no threshold, so they cannot collapse -- see TableCell for why that matters
        // more than it looks: the engine prunes every under-threshold child in ONE pass rather than dropping
        // the least important first, so at a squeeze anything collapsible goes at once.
        private static Layout.Node TableHeaderRow(HomeBoardStyle style) =>
            Layout.Builder.HStack(
                    Layout.Builder.Spacer().WFixed(DotSize),
                    TableCell("Rig", style.DimText, 2f),
                    TableCell("Profile", style.DimText, 1.5f, 90f),
                    TableCell("Status", style.DimText, 2f),
                    TableCell("Progress", style.DimText, 1.5f, 110f),
                    TableCell("Cooling", style.DimText, 1.5f, 110f),
                    TableCell("Flip", style.DimText, 1f, 70f),
                    TableCell("RMS", style.DimText, 1f, 60f))
                .RowH(DetailRowHeight)
                .WithGap(8f);

        private static Layout.Node TableRow(
            RigCard card, HomeBoardStyle style, DateTimeOffset now,
            Func<RigCard, Action<InputModifier>?>? onSelect)
        {
            var dot = !card.IsOnline ? style.OfflineDot : card.IsRunning ? style.RunningDot : style.OnlineDot;

            // The badge replaces the two rightmost columns rather than adding a ninth: a waiting rig's flip
            // countdown and guide RMS are not what you need from that row.
            var tail = card.Prompt is { } prompt
                ? (Layout.Builder
                    .Text(prompt.Describe(), BaseFontSize * 0.8f, style.PromptText, TextAlign.Center, TextAlign.Center)
                    .WStar(2f, 130f)
                    .Bg(style.PromptBg)
                    .Radius(4f), (Layout.Node?)null)
                : (TableCell(card.TimeToMeridianFlip(now) is { } untilFlip
                        ? LiveSessionActions.FormatDuration(untilFlip)
                        : "", style.DimText, 1f, 70f),
                   TableCell(card.GuideRmsArcsec is { } rms ? $"{rms:F2}\"" : "", style.DimText, 1f, 60f));

            var cells = ImmutableArray.CreateBuilder<Layout.Node>(9);
            cells.Add(Layout.Builder.Box(DotSize, DotSize, dot).WFixed(DotSize).HFixed(DotSize));
            cells.Add(TableCell(card.Title, style.BodyText, 2f));
            cells.Add(TableCell(card.Subtitle ?? "", style.DimText, 1.5f, 90f));
            cells.Add(TableCell(card.Status, style.BodyText, 2f));
            cells.Add(TableCell(card.Progress?.Describe() ?? "", style.DimText, 1.5f, 110f));
            cells.Add(TableCell(card.Cooling is { } cooling ? cooling.Describe() : "", style.DimText, 1.5f, 110f));
            cells.Add(tail.Item1);
            if (tail.Item2 is { } rmsCell)
            {
                cells.Add(rmsCell);
            }

            return Layout.Builder.HStack([.. cells])
                .RowH(TitleRowHeight)
                .WithGap(8f)
                .Clickable(new HitResult.ButtonHit($"HomeRig:{card.Title}"), onSelect?.Invoke(card));
        }

        /// <summary>
        /// One table cell: a weighted column that collapses out entirely rather than truncating, so a narrow
        /// window loses whole columns and the ones that remain stay readable.
        /// </summary>
        /// <param name="min">
        /// The width below which the column comes off, or 0 for a column that must never come off.
        /// <para>
        /// Deliberately NOT also a Star minimum: a min-clamped Star holds its floor and lets the row overflow,
        /// which would mean the collapse threshold could never be reached and every column would stay,
        /// squeezed, past the edge of the screen. Leaving the Star free is what lets a column actually shrink
        /// under the threshold and be dropped, handing its space to the columns that remain.
        /// </para>
        /// <para>
        /// The engine drops <b>every</b> under-threshold child in one pass and then re-resolves, rather than
        /// shedding the least important first -- so at a real squeeze everything collapsible goes together.
        /// That is why the columns that carry the row's meaning take no threshold instead of a small one.
        /// </para>
        /// </param>
        private static Layout.Node TableCell(string text, RGBAColor32 color, float weight, float min = 0f) =>
            Layout.Builder
                .Text(text, BaseFontSize * 0.85f, color, TextAlign.Near, TextAlign.Center)
                .WStar(weight)
                .HStar()
                .CollapseBelow(min);

        /// <param name="fitWithin">
        /// Height the card grid must not exceed, or <see cref="float.PositiveInfinity"/> to never fall back.
        /// Checked against the height the cards ACTUALLY came to rather than a worst-case constant, so a board
        /// of quiet rigs keeps its cards in a window where a board of busy ones would not -- and there is no
        /// second estimate of the card height to drift from the real one.
        /// </param>
        /// <param name="fellBackToTable">True when the grid did not fit and the caller should draw the table.</param>
        private static Layout.Node Body(
            ImmutableArray<RigCard> cards, HomeBoardStyle style, int columns,
            DateTimeOffset now, RigCardDetail detail,
            Func<RigCard, Action<InputModifier>?>? onSelect,
            float fitWithin, out bool fellBackToTable)
        {
            fellBackToTable = false;

            if (cards.IsDefaultOrEmpty)
            {
                return Layout.Builder
                    .Text("No rigs yet.", BaseFontSize, style.EmptyText, TextAlign.Center, TextAlign.Center)
                    .WStar().HStar();
            }

            // Every card gets the SAME box, sized to the busiest one. Cards are built first and measured
            // second, so the height comes from the rows that were actually emitted rather than from a
            // constant somebody has to remember to raise -- which is what used to clip the last row.
            //
            // One shared height rather than a per-card one is what keeps this a board: a grid row already
            // equalises its own cards, so per-card heights would only show up as ragged row heights, and an
            // idle rig's card would change size the moment its rig started a run. The cost is some empty
            // space on quiet cards when one rig is busy, which is the right trade for a screen you scan.
            var built = new (Layout.Node Node, float Height)[cards.Length];
            var tallest = CardHeight;
            for (var i = 0; i < cards.Length; i++)
            {
                built[i] = CardBody(cards[i], style, now, detail, onSelect);
                tallest = Math.Max(tallest, built[i].Height);
            }

            var gridRows = (cards.Length + columns - 1) / Math.Max(1, columns);
            if (gridRows * tallest + CardGap * Math.Max(0, gridRows - 1) > fitWithin)
            {
                fellBackToTable = true;
                return Layout.Builder.Spacer();
            }

            var cardNodes = new Layout.Node[cards.Length];
            for (var i = 0; i < cards.Length; i++)
            {
                cardNodes[i] = built[i].Node.RowH(tallest);
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
        /// One card: the rig, the profile it runs, a status line, then -- only while a run is live -- how far
        /// through the night it is, and a prompt badge when one is outstanding.
        /// <para>
        /// <b>Every row past the first three is conditional on having something to say</b>, and the card's
        /// height is the sum of the rows it actually built. That is why there is no constant here to keep in
        /// step: adding a row cannot clip the card, and a rig with nothing to report does not reserve blank
        /// space for rows it will never draw. <see cref="CardHeight"/> survives only as a floor, so a bare
        /// card still looks like a card.
        /// </para>
        /// </summary>
        /// <param name="now">Resolves the flip countdown. Passed in rather than read from a clock so the tree
        /// stays a pure function of its inputs and a test can assert a countdown without waiting for one --
        /// and so the countdown is computed per FRAME rather than per telemetry poll, which is the whole
        /// reason <see cref="RigCard.MeridianFlipUtc"/> is an instant.</param>
        /// <param name="detail">How much to show. Only the two collapsible extras honour it.</param>
        public static Layout.Node Card(
            RigCard card,
            HomeBoardStyle style,
            DateTimeOffset now,
            RigCardDetail detail = RigCardDetail.Full,
            Func<RigCard, Action<InputModifier>?>? onSelect = null)
        {
            var (node, height) = CardBody(card, style, now, detail, onSelect);
            return node.RowH(height);
        }

        /// <summary>
        /// The card, plus the height its rows came to. Split out so <see cref="Body"/> can size every card to
        /// the tallest without a second function deciding which rows exist -- two such functions would drift
        /// the first time a row was added to one of them.
        /// </summary>
        private static (Layout.Node Node, float Height) CardBody(
            RigCard card,
            HomeBoardStyle style,
            DateTimeOffset now,
            RigCardDetail detail,
            Func<RigCard, Action<InputModifier>?>? onSelect)
        {
            var dot = !card.IsOnline ? style.OfflineDot : card.IsRunning ? style.RunningDot : style.OnlineDot;
            var rows = ImmutableArray.CreateBuilder<Layout.Node>(11);
            var contentHeight = 0f;

            void Row(Layout.Node node, float height)
            {
                rows.Add(node.RowH(height));
                contentHeight += height;
            }

            Row(Layout.Builder.HStack(
                    Layout.Builder.Text(card.Title, BaseFontSize * 1.05f, style.BodyText).WStar().HStar(),
                    Layout.Builder.Box(DotSize, DotSize, dot).WFixed(DotSize).HFixed(DotSize))
                .WithGap(6f), TitleRowHeight);

            // Title is the rig, subtitle is the profile it runs -- the field that tells two similar rigs
            // apart. "Profile unknown" is stated rather than left blank, so an unlabelled card reads as a rig
            // we have not asked yet rather than a rig with no profile.
            Row(Layout.Builder.Text(card.Subtitle ?? "profile unknown", BaseFontSize * 0.85f, style.DimText),
                DetailRowHeight);

            Row(Layout.Builder.Text(card.Status, BaseFontSize * 0.9f, style.BodyText), StatusRowHeight);

            // Cooling sits directly under the status because during setup it IS the status, and it is the one
            // row that answers a question about a rig you are NOT looking at: is this one ready yet. Settled
            // reads in full body text -- "done" is the thing you are scanning for -- while a ramp in progress
            // stays dim like the other in-flight counters.
            if (card.Cooling is { } cooling)
            {
                Row(Layout.Builder.Text($"❄ {cooling.Describe()}", BaseFontSize * 0.85f,
                    cooling.IsSettled ? style.BodyText : style.DimText), DetailRowHeight);
            }

            if (card.IsRunning)
            {
                if (card.Progress is { } progress)
                {
                    Row(Layout.Builder.Text(progress.Describe(), BaseFontSize * 0.85f, style.DimText),
                        DetailRowHeight);
                }

                if (card.Target is { Length: > 0 } target)
                {
                    Row(Layout.Builder.Text(target, BaseFontSize * 0.85f, style.DimText), DetailRowHeight);
                }

                // A flip interrupts imaging, so knowing one is minutes away changes whether you go to bed.
                if (card.TimeToMeridianFlip(now) is { } untilFlip)
                {
                    Row(Layout.Builder.Text(
                            $"flip in {LiveSessionActions.FormatDuration(untilFlip)}",
                            BaseFontSize * 0.85f, style.DimText),
                        DetailRowHeight);
                }

                if (DescribeStats(card, detail) is { } stats)
                {
                    Row(Layout.Builder.Text(stats, BaseFontSize * 0.85f, style.DimText), DetailRowHeight);
                }
            }

            // The plug row: dim when something is still unplugged, normal when the rig is fully connected, so
            // the card answers "did Connect All do anything" without reading the number.
            if (card.Devices is { } devices)
            {
                Row(Layout.Builder.Text(devices.Describe(), BaseFontSize * 0.85f,
                    devices.AllConnected ? style.BodyText : style.DimText), DetailRowHeight);
            }

            // Last thing the rig said. Coloured by severity, because the reason to keep a line for this is
            // the warning that would otherwise have been overwritten by the next activity string.
            if (detail is RigCardDetail.Full && card.LastNote is { } note)
            {
                var noteColor = note.Severity switch
                {
                    NotificationSeverity.Error => style.ErrorText,
                    NotificationSeverity.Warning => style.WarnText,
                    _ => style.DimText
                };
                Row(Layout.Builder.Text(note.Describe(), BaseFontSize * 0.8f, noteColor), DetailRowHeight);
            }

            if (card.Prompt is { } prompt)
            {
                // The only thing on a card that is a call to action rather than a status, so it gets the one
                // saturated colour on the screen.
                Row(Layout.Builder
                        .Text(prompt.Describe(), BaseFontSize * 0.85f, style.PromptText, TextAlign.Center, TextAlign.Center)
                        .Bg(style.PromptBg)
                        .Radius(6f),
                    TitleRowHeight);
            }

            var gaps = RowGap * Math.Max(0, rows.Count - 1);
            var node = Layout.Builder.VStack([.. rows])
                .WithGap(RowGap)
                .Pad(CardPadding)
                .Bg(card.IsViewed ? style.ViewedCardBg : style.CardBg)
                .Radius(CardRadius)
                .Clickable(new HitResult.ButtonHit($"HomeRig:{card.Title}"), onSelect?.Invoke(card));

            return (node, Math.Max(CardHeight, contentHeight + gaps + CardPadding * 2f));
        }

        /// <summary>
        /// The collapsible stats line: guide RMS, plus median HFD when the card is showing full detail. Null
        /// when there is nothing measured to put on it.
        /// </summary>
        private static string? DescribeStats(RigCard card, RigCardDetail detail)
        {
            var rms = card.GuideRmsArcsec is { } r ? $"RMS {r:F2}\"" : null;
            var hfd = detail is RigCardDetail.Full && card.MedianHfd is { } h ? $"HFD {h:F2}" : null;

            return (rms, hfd) switch
            {
                ({ } a, { } b) => $"{a} · {b}",
                ({ } a, null) => a,
                (null, { } b) => b,
                _ => null
            };
        }
    }
}
