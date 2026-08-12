using Console.Lib;
using DIR.Lib;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Geometry tests for the Home tab, rendered offline through <see cref="RgbaImageRenderer"/> -- no GPU and
    /// no window (the <see cref="PlannerTabLayoutTests"/> pattern).
    /// <para>
    /// These exist because the board's geometry is exactly the kind that looks fine in a build and wrong on
    /// screen. The first working version used a fixed-size <c>WrapH</c> flow, and a single fluent call
    /// (<c>RowH</c>, which sets <c>Width = Star</c>) silently discarded the card's fixed width and collapsed
    /// every card to its text. Only an arranged rect shows that.
    /// </para>
    /// </summary>
    public class HomeTabLayoutTests
    {
        /// <summary>
        /// Fixed clock, so a flip countdown is assertable without waiting for one.
        /// </summary>
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 7, 29, 22, 0, 0, TimeSpan.Zero);

        private static RigCard Card(
            string title, bool running = false, RigCardPrompt? prompt = null, RigDeviceLink? devices = null,
            RigCardProgress? progress = null, RigCardCooling? cooling = null, double? hfd = null,
            DateTimeOffset? flipUtc = null, RigCardNote? note = null) =>
            new RigCard(
                Title: title,
                Subtitle: "Test",
                IsLocal: title == "This computer",
                IsOnline: true,
                Phase: running ? SessionPhase.Observing : SessionPhase.NotStarted,
                Status: running ? "Imaging" : "Idle",
                Target: running ? "M31" : null,
                FramesWritten: running ? 12 : 0,
                GuideRmsArcsec: running ? 0.62 : null,
                Prompt: prompt,
                Devices: devices,
                IsViewed: false,
                Progress: progress,
                Cooling: cooling,
                MedianHfd: hfd,
                MeridianFlipUtc: flipUtc,
                LastNote: note);

        private static HomeTab<RgbaImage> RenderTab(
            RgbaImageRenderer renderer, ImmutableArray<RigCard> cards, float dpiScale = 1f, int frames = 1)
        {
            var tab = new HomeTab<RgbaImage>(renderer)
            {
                DpiScale = dpiScale,
                FontPath = FontResolver.ResolveSystemFont(),
            };
            var appState = new GuiAppState { HomeCards = cards };
            for (var i = 0; i < frames; i++)
            {
                tab.Render(appState, new RectF32(0, 0, renderer.Width, renderer.Height), Now);
            }
            return tab;
        }

        private static ClickableRegion[] Cards(HomeTab<RgbaImage> tab) =>
            [.. tab.GetRegisteredRegions()
                .Where(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("HomeRig:"))
                .OrderBy(r => r.Y).ThenBy(r => r.X)];

        [Fact]
        public void CardsFillTheirColumnAtTheDesignedHeight()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = RenderTab(renderer, [Card("This computer")]);

            var card = Cards(tab).ShouldHaveSingleItem();

            // Height is Fixed because it sizes the grid row; width is Star so the card fills its column. The
            // regression this pins: a fluent call that quietly re-set Width collapsed the card to its text.
            card.Height.ShouldBe(HomeBoardLayout.CardHeight, 0.5f);
            card.Width.ShouldBeGreaterThan(HomeBoardLayout.MinCardWidth);
        }

        [Fact]
        public void ColumnsAreDroppedAsTheWindowNarrowsRatherThanCardsBeingCrushed()
        {
            var wide = HomeBoardLayout.MinCardWidth * 3 + HomeBoardLayout.CardGap * 2;
            HomeBoardLayout.ColumnsFor(wide, cardCount: 8).ShouldBe(3);
            HomeBoardLayout.ColumnsFor(HomeBoardLayout.MinCardWidth, cardCount: 8).ShouldBe(1);

            // Never zero, however little room there is -- a zero-column grid would drop every card silently.
            HomeBoardLayout.ColumnsFor(0f, cardCount: 8).ShouldBe(1);
            HomeBoardLayout.ColumnsFor(-50f, cardCount: 8).ShouldBe(1);
            HomeBoardLayout.ColumnsFor(wide, cardCount: 0).ShouldBe(1);
        }

        [Fact]
        public void AWideWindowDoesNotSplitAFewRigsIntoMoreColumnsThanThereAreRigs()
        {
            // A 200-column terminal has room for six columns. Laying four rigs out in six leaves two empty,
            // and -- worse -- squeezes the four real cards under the width at which they show full detail, so
            // the cards get NARROWER the wider the window is.
            var wideBoard = 200 * 8f - HomeBoardLayout.BodyPadding * 2f;
            HomeBoardLayout.ColumnsFor(wideBoard, cardCount: 8).ShouldBeGreaterThan(4);

            var columns = HomeBoardLayout.ColumnsFor(wideBoard, cardCount: 4);
            columns.ShouldBe(4);
            HomeBoardLayout.DetailFor(HomeBoardLayout.ColumnWidth(wideBoard, columns)).ShouldBe(RigCardDetail.Full);
        }

        [Fact]
        public void CardsLineUpInColumnsAndRows()
        {
            using var renderer = new RgbaImageRenderer(1200, 1000);
            var tab = RenderTab(renderer, [Card("A"), Card("B"), Card("C"), Card("D")]);

            var cards = Cards(tab);
            cards.Length.ShouldBe(4);

            // A grid means cards share row tops and column lefts, which is what a ragged flow does not give:
            // card i and card i+columns line up on X and are the same width.
            var columns = cards.Count(c => Math.Abs(c.Y - cards[0].Y) < 0.5f);
            columns.ShouldBeGreaterThan(1);
            for (var i = 0; i < cards.Length; i++)
            {
                cards[i].X.ShouldBe(cards[i % columns].X, 0.5f);
                cards[i].Width.ShouldBe(cards[0].Width, 0.5f);
            }
        }

        [Fact]
        public void AnExtraCardAddsARowInsteadOfShrinkingTheExistingOnes()
        {
            using var renderer = new RgbaImageRenderer(1200, 1000);

            // The whole point of content-sized rows: the board grows downward, it does not squeeze.
            var twoCards = Cards(RenderTab(renderer, [Card("A"), Card("B")]));
            var manyCards = Cards(RenderTab(renderer,
                [Card("A"), Card("B"), Card("C"), Card("D"), Card("E"), Card("F"), Card("G")]));

            manyCards.Length.ShouldBe(7);
            foreach (var card in manyCards)
            {
                card.Height.ShouldBe(twoCards[0].Height, 0.5f);
            }
            manyCards[^1].Y.ShouldBeGreaterThan(twoCards[0].Y);
        }

        [Fact]
        public void CardsScaleWithDpiRatherThanStayingAtDesignUnits()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = RenderTab(renderer, [Card("This computer")], dpiScale: 2f);

            Cards(tab).ShouldHaveSingleItem().Height.ShouldBe(HomeBoardLayout.CardHeight * 2f, 1f);
        }

        [Fact]
        public void ARunningRigAndAWaitingRigKeepTheSameCardBox()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);

            // Extra rows (counters, target, badge) live inside a fixed box, so one busy or blocked rig must
            // not reflow the whole board.
            var idle = Cards(RenderTab(renderer, [Card("A")])).ShouldHaveSingleItem();
            var busy = Cards(RenderTab(renderer,
                [Card("A", running: true, prompt: new RigCardPrompt("Manual flat panel", TimeSpan.FromMinutes(40), true))]))
                .ShouldHaveSingleItem();

            busy.Width.ShouldBe(idle.Width, 0.5f);
            busy.Height.ShouldBe(idle.Height, 0.5f);
        }

        [Fact]
        public void TheDeviceLinkRowStaysInsideTheFixedCardBox()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);

            // Connect All has no visible effect on this screen without the plug row, but the row must not
            // change the card's size either -- a rig connecting would otherwise reflow the whole board.
            var bare = Cards(RenderTab(renderer, [Card("A")])).ShouldHaveSingleItem();
            var linked = Cards(RenderTab(renderer, [Card("A", devices: new RigDeviceLink(4, 6))]))
                .ShouldHaveSingleItem();

            linked.Height.ShouldBe(bare.Height, 0.5f);
            linked.Width.ShouldBe(bare.Width, 0.5f);
        }

        [Fact]
        public void APartlyConnectedRigReadsDifferentlyFromAFullyConnectedOne()
        {
            // The count is what distinguishes them; a bare "connected" would look identical either way.
            new RigDeviceLink(6, 6).AllConnected.ShouldBeTrue();
            new RigDeviceLink(4, 6).AllConnected.ShouldBeFalse();
            new RigDeviceLink(0, 0).AllConnected.ShouldBeFalse("nothing assigned is not the same as all up");

            // Reads as a sentence, and the cases stay distinguishable at a glance.
            new RigDeviceLink(6, 6).Describe().ShouldEndWith("All 6 devices connected");
            new RigDeviceLink(4, 6).Describe().ShouldEndWith("4 of 6 devices connected");
            new RigDeviceLink(0, 6).Describe().ShouldEndWith("No devices connected");
            // A lone device must not read as "All 1 devices".
            new RigDeviceLink(1, 1).Describe().ShouldEndWith("1 device connected");

            // The socket leads the label, in the same run as the text -- which only renders because the
            // painter resolves emoji runs to the emoji font per run.
            new RigDeviceLink(4, 6).Describe().ShouldStartWith("🔌");
        }

        [Fact]
        public void TheSameTreeArrangesOnACellSurface()
        {
            // The point of the axis-aware unit mapping: this is HomeBoardLayout.Build -- the very tree the GPU
            // tab renders -- arranged for a terminal. No TUI-specific card, rows, or metrics.
            var cards = ImmutableArray.Create(Card("This computer"), Card("Backyard"), Card("Roof"));
            // An 80-column terminal in design units (8 per cell), which resolves to the two columns this asserts.
            var tree = HomeBoardLayout.Build(cards, HomeBoardStyle.Default, width: 80 * 8f, Now);

            // An 80x24 terminal.
            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, 80, 24), CellMeasureContext.PixelAuthored);

            var cardRects = arranged
                .Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("HomeRig:"))
                .Select(a => a.Bounds)
                .OrderBy(r => r.Y).ThenBy(r => r.X)
                .ToArray();

            cardRects.Length.ShouldBe(3);

            // A card is a sane number of CELLS, not the raw design-unit count: 132 units tall is ~8 rows at
            // 16 units per row, not 132 rows. Before the axis split this arranged into nonsense.
            foreach (var rect in cardRects)
            {
                rect.Height.ShouldBeInRange(6, 10);
                rect.Width.ShouldBeInRange(20, 40);
            }

            // Two per row, third wraps -- the grid behaves the same way it does on pixels.
            cardRects[0].Y.ShouldBe(cardRects[1].Y);
            cardRects[2].Y.ShouldBeGreaterThan(cardRects[0].Y);

            // And it fits the terminal rather than overflowing it.
            foreach (var rect in cardRects)
            {
                (rect.X + rect.Width).ShouldBeLessThanOrEqualTo(80);
            }
        }

        [Fact]
        public void ACellAuthoredContextWouldArrangeTheSharedTreeIntoNonsense()
        {
            // Why the convention has to be told to the context rather than assumed. The default cell context
            // reads one design unit as one cell, so the same tree claims a 132-ROW card -- which is what made
            // sharing a tree across surface kinds type-correct and geometrically meaningless.
            var tree = HomeBoardLayout.Build(
                ImmutableArray.Create(Card("This computer")), HomeBoardStyle.Default, width: 80 * 8f, Now);

            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, 80, 24), CellMeasureContext.CellAuthored);

            var card = arranged
                .Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("HomeRig:"))
                .Select(a => a.Bounds)
                .ShouldHaveSingleItem();

            card.Height.ShouldBeGreaterThan(24, "one unit per cell reads CardHeight as rows, not pixels");
        }

        [Fact]
        public void RepaintingDoesNotAccumulateClickableRegions()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = RenderTab(renderer, [Card("This computer"), Card("Backyard")], frames: 30);

            Cards(tab).Length.ShouldBe(2);
        }
    

        /// <summary>A card with every optional row populated -- the worst case for the card's height.</summary>
        private static RigCard FullCard(string title) =>
            Card(title,
                running: true,
                devices: new RigDeviceLink(6, 6),
                progress: new RigCardProgress(2, 3, 23, 100),
                cooling: new RigCardCooling(-9.9, -10.0, 38.0, 1, 1, IsRamping: false),
                hfd: 3.14,
                flipUtc: Now + TimeSpan.FromMinutes(35),
                note: new RigCardNote(NotificationSeverity.Warning, "Guide star lost", TimeSpan.FromMinutes(4)),
                prompt: new RigCardPrompt("Manual flat panel", TimeSpan.FromMinutes(40), true));

        [Fact]
        public void AFullyLoadedCardGrowsRatherThanClippingItsLastRow()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);

            // Every row populated exceeds the old fixed 132-unit box. When the height was a constant, the
            // rows past it were simply not drawn -- which is invisible in a build and looks like a missing
            // feature on screen.
            var loaded = Cards(RenderTab(renderer, [FullCard("A")])).ShouldHaveSingleItem();

            loaded.Height.ShouldBeGreaterThan(HomeBoardLayout.CardHeight);
        }

        [Fact]
        public void EveryCardSharesOneBoxSizedToTheBusiestRig()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);

            // A board is scanned, so cards must line up: one busy rig sizes the box and the quiet ones match
            // it, rather than each card taking its own height and leaving the rows ragged.
            var cards = Cards(RenderTab(renderer, [Card("A"), FullCard("B"), Card("C")]));

            cards.Length.ShouldBe(3);
            foreach (var card in cards)
            {
                card.Height.ShouldBe(cards[0].Height, 0.5f);
                card.Height.ShouldBeGreaterThan(HomeBoardLayout.CardHeight);
            }
        }

        [Fact]
        public void AFullCardCostsAKnownNumberOfTerminalRows()
        {
            // A cell row is indivisible, so a 14-design-unit row costs a whole one: the card that is ~9.6
            // rows' worth of pixels is 13 rows in a terminal. Pinned because it is the number that decides
            // how many rigs fit an 80x24 window, and it is not obvious from the design units.
            var tree = HomeBoardLayout.Build(
                ImmutableArray.Create(FullCard("This computer")), HomeBoardStyle.Default, width: 80 * 8f, Now);

            var card = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 80, 60), CellMeasureContext.PixelAuthored)
                .Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("HomeRig:"))
                .Select(a => a.Bounds)
                .ShouldHaveSingleItem();

            card.Height.ShouldBe(13);
        }

        [Fact]
        public void ANarrowColumnDropsTheTwoCollapsibleRowsRatherThanTheEssentialOnes()
        {
            // Width decides, and it is the COLUMN's width, not the window's: six rigs on a wide monitor give
            // narrow columns and want the compact card.
            HomeBoardLayout.DetailFor(HomeBoardLayout.FullDetailCardWidth).ShouldBe(RigCardDetail.Full);
            HomeBoardLayout.DetailFor(HomeBoardLayout.MinCardWidth).ShouldBe(RigCardDetail.Compact);

            var style = HomeBoardStyle.Default;
            var full = Layout.Engine.Arrange(
                HomeBoardLayout.Card(FullCard("A"), style, Now, RigCardDetail.Full),
                new Rect<int>(0, 0, 60, 40), CellMeasureContext.PixelAuthored);
            var compact = Layout.Engine.Arrange(
                HomeBoardLayout.Card(FullCard("A"), style, Now, RigCardDetail.Compact),
                new Rect<int>(0, 0, 60, 40), CellMeasureContext.PixelAuthored);

            // The note line comes off, and HFD comes off the stats line -- the RMS stays, because a rig
            // guiding badly is something you act on.
            Texts(full).ShouldContain(t => t.Contains("Guide star lost"));
            Texts(compact).ShouldNotContain(t => t.Contains("Guide star lost"));
            Texts(full).ShouldContain(t => t.Contains("HFD"));
            Texts(compact).ShouldNotContain(t => t.Contains("HFD"));
            Texts(compact).ShouldContain(t => t.Contains("RMS"));

            // And nothing essential went with them.
            Texts(compact).ShouldContain(t => t.Contains("target 2/3"));
            Texts(compact).ShouldContain(t => t.Contains("flip in"));
            Texts(compact).ShouldContain(t => t.Contains("WAITING"));
            Texts(compact).ShouldContain(t => t.Contains("-10.0"));
        }

        [Fact]
        public void TheFlipCountdownIsDrawnFromTheClockPassedInNotStoredOnTheCard()
        {
            var card = Card("A", running: true, flipUtc: Now + TimeSpan.FromHours(2));
            var style = HomeBoardStyle.Default;

            // Same card, two clocks: the row has to move, which is what the instant-on-the-wire design buys.
            var early = Layout.Engine.Arrange(
                HomeBoardLayout.Card(card, style, Now),
                new Rect<int>(0, 0, 60, 40), CellMeasureContext.PixelAuthored);
            var later = Layout.Engine.Arrange(
                HomeBoardLayout.Card(card, style, Now + TimeSpan.FromMinutes(115)),
                new Rect<int>(0, 0, 60, 40), CellMeasureContext.PixelAuthored);

            Texts(early).ShouldContain(t => t.StartsWith("flip in 2"));
            Texts(later).ShouldContain(t => t.StartsWith("flip in 5"));

            // Past due drops the row rather than counting backwards.
            var due = Layout.Engine.Arrange(
                HomeBoardLayout.Card(card, style, Now + TimeSpan.FromHours(3)),
                new Rect<int>(0, 0, 60, 40), CellMeasureContext.PixelAuthored);
            Texts(due).ShouldNotContain(t => t.StartsWith("flip in"));
        }

        /// <summary>Every text run the tree emitted, for asserting WHICH rows a card built.</summary>
        // -----------------------------------------------------------------------------------------
        // The two shapes, and the selector that chooses between them
        // -----------------------------------------------------------------------------------------

        /// <summary>Arranges a board into a terminal of the given size and returns its text runs + card hits.</summary>
        private static (string[] Texts, int RigRows) Board(
            ImmutableArray<RigCard> cards, int cols, int rows, HomeBoardView view = HomeBoardView.Auto)
        {
            var tree = HomeBoardLayout.Build(
                cards, HomeBoardStyle.Default, cols * 8f, Now, rows * 16f, view,
                onSelect: _ => _ => { },
                onSelectView: _ => _ => { });

            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, cols, rows), CellMeasureContext.PixelAuthored);

            var rigHits = arranged.Count(a =>
                a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("HomeRig:"));

            return (Texts(arranged), rigHits);
        }

        [Fact]
        public void AShrunkWindowSwapsTheCardsForATableRatherThanRunningOffTheBottom()
        {
            var rigs = ImmutableArray.Create(FullCard("A"), FullCard("B"), FullCard("C"), FullCard("D"));

            // Roomy: cards, one row of four.
            var roomy = Board(rigs, cols: 200, rows: 56);
            roomy.Texts.ShouldContain(t => t.Contains("Guide star lost"), "a wide board shows the full card");
            roomy.Texts.ShouldNotContain("Rig", "the table's column headings are not drawn in card mode");

            // Cramped: four 13-row cards in two columns need 27 rows and there are 20. The table is the
            // answer, and every rig keeps a row -- nothing is hidden behind anything.
            var cramped = Board(rigs, cols: 100, rows: 20);
            cramped.Texts.ShouldContain("Rig");
            cramped.RigRows.ShouldBe(4);
        }

        [Fact]
        public void TheHeaderSaysWhyTheShapeChangedRatherThanJustChanging()
        {
            // A board that silently turns into a different thing reads as a glitch; naming it makes the same
            // event the nudge that the window wants enlarging.
            var cramped = Board(ImmutableArray.Create(FullCard("A"), FullCard("B"), FullCard("C"), FullCard("D")),
                cols: 100, rows: 20);

            cramped.Texts.ShouldContain(t => t.Contains("window too small for cards"));
        }

        [Fact]
        public void AnExplicitChoiceIsNotSecondGuessedByTheWindowSize()
        {
            var rigs = ImmutableArray.Create(FullCard("A"), FullCard("B"), FullCard("C"), FullCard("D"));

            // Cards in a window too small for them: the user asked for cards and gets cards. Overriding a
            // choice somebody just made is worse than an overflowing board.
            var forcedCards = Board(rigs, cols: 100, rows: 20, view: HomeBoardView.Cards);
            forcedCards.Texts.ShouldNotContain("Rig");
            forcedCards.Texts.ShouldNotContain(t => t.Contains("window too small"));

            // And the table in a window with room to spare.
            var forcedTable = Board(rigs, cols: 200, rows: 56, view: HomeBoardView.Table);
            forcedTable.Texts.ShouldContain("Rig");
        }

        [Fact]
        public void TheSelectorOffersEveryShapeAndMarksTheCurrentOne()
        {
            var tree = HomeBoardLayout.Build(
                ImmutableArray.Create(Card("A")), HomeBoardStyle.Default, 200 * 8f, Now,
                view: HomeBoardView.Table, onSelectView: _ => _ => { });

            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, 200, 56), CellMeasureContext.PixelAuthored);

            var options = arranged
                .Select(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("HomeView:")
                    ? action["HomeView:".Length..]
                    : null)
                .Where(a => a is not null)
                .ToArray();

            options.ShouldBe(["Auto", "Cards", "Table"]);
        }

        /// <summary>
        /// The segments are <see cref="Layout.Content.Icon"/> leaves, not text runs, which is what lets the
        /// same tree paint rectangles on the GPU board and a block-element glyph in the terminal. Asserting
        /// the CONTENT rather than a pixel keeps this a statement about the tree both surfaces read.
        /// </summary>
        [Fact]
        public void EachSegmentCarriesItsShapesIcon()
        {
            var tree = HomeBoardLayout.Build(
                ImmutableArray.Create(Card("A")), HomeBoardStyle.Default, 200 * 8f, Now,
                view: HomeBoardView.Table, onSelectView: _ => _ => { });

            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, 200, 56), CellMeasureContext.PixelAuthored);

            var icons = arranged
                .Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var action }
                    && action.StartsWith("HomeView:"))
                .Select(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Icon icon }
                    ? icon.Kind
                    : (Layout.IconKind?)null)
                .ToArray();

            icons.ShouldBe([Layout.IconKind.Auto, Layout.IconKind.Grid, Layout.IconKind.List]);
        }

        /// <summary>
        /// The segments are the fixed width they ask for, not a third of the header each.
        /// <para>
        /// This is the <c>RowH</c> trap in its second costume, and it is why this file exists: <c>RowH</c>
        /// means "a full-width row of fixed height", so it sets <c>Width = Star</c> and silently discards a
        /// <c>WFixed</c> before it. The selector had been built that way from the start, which compiled,
        /// rendered, and spread three buttons across the whole bar -- only an arranged rect says so.
        /// </para>
        /// </summary>
        [Fact]
        public void TheSegmentsKeepTheirFixedWidthInsteadOfSharingTheHeader()
        {
            var tree = HomeBoardLayout.Build(
                ImmutableArray.Create(Card("A")), HomeBoardStyle.Default, 200 * 8f, Now,
                view: HomeBoardView.Auto, onSelectView: _ => _ => { },
                theme: UiThemeState.Dark, onCycleTheme: _ => { });

            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, 200, 56), CellMeasureContext.PixelAuthored);

            var widths = arranged
                .Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var action }
                    && action.StartsWith("HomeView:"))
                .Select(a => a.Bounds.Width)
                .ToArray();

            widths.Length.ShouldBe(3);
            foreach (var w in widths)
            {
                // 26 design units is 3.25 cells at PixelAuthored's 8px cell; a Star segment on this board
                // came out at ~47.
                w.ShouldBeLessThanOrEqualTo(5, "a segment is fixed-width, not Star");
                w.ShouldBeGreaterThan(1);
            }
        }

        [Fact]
        public void ABoardWithNowhereToStoreTheChoiceDrawsNoSelector()
        {
            // The callback IS the permission to offer the control: a host that cannot persist a choice must
            // not present one that silently does nothing.
            var tree = HomeBoardLayout.Build(
                ImmutableArray.Create(Card("A")), HomeBoardStyle.Default, 200 * 8f, Now);

            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, 200, 56), CellMeasureContext.PixelAuthored);

            // Counted rather than asserted with a predicate: Shouldly's predicate overload builds an
            // expression tree, which cannot contain an `is` pattern.
            arranged.Count(a =>
                    a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("HomeView:"))
                .ShouldBe(0);
        }

        /// <summary>
        /// The theme control shows the state it is IN, not the one a click reaches. With four states no
        /// single mark can say "what happens next", and the reader's actual question is which scheme they
        /// are looking at.
        /// </summary>
        [Theory]
        [InlineData(UiThemeState.System, "System")]
        [InlineData(UiThemeState.Light, "Light")]
        [InlineData(UiThemeState.Dark, "Dark")]
        [InlineData(UiThemeState.Night, "Night")]
        public void TheThemeControlNamesTheStateItIsIn(UiThemeState state, string label)
        {
            var tree = HomeBoardLayout.Build(
                ImmutableArray.Create(Card("A")), HomeBoardStyle.Default, 200 * 8f, Now,
                theme: state, onCycleTheme: _ => { });

            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, 200, 56), CellMeasureContext.PixelAuthored);

            Texts(arranged).ShouldContain(label);

            arranged.Count(a =>
                    a.Node.Hit is HitResult.ButtonHit { Action: var action }
                    && action == $"HomeTheme:{state}")
                .ShouldBe(1);
        }

        /// <summary>
        /// The three presentations, by the thing that separates them: whether the state's WORD is on screen,
        /// and whether the control carries an icon leaf. The default has both, which is what makes Dark and
        /// Night distinguishable while the marks still read as one family with the segments.
        /// </summary>
        [Theory]
        [InlineData(ThemeControlStyle.IconAndLabel, true, true)]
        [InlineData(ThemeControlStyle.IconOnly, false, true)]
        [InlineData(ThemeControlStyle.LabelOnly, true, false)]
        public void TheThemeControlPresentsItselfHowTheHostAsked(
            ThemeControlStyle presentation, bool expectWord, bool expectIcon)
        {
            var tree = HomeBoardLayout.Build(
                ImmutableArray.Create(Card("A")), HomeBoardStyle.Default, 200 * 8f, Now,
                theme: UiThemeState.Night, onCycleTheme: _ => { }, themeControl: presentation);

            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, 200, 56), CellMeasureContext.PixelAuthored);

            Texts(arranged).Contains("Night").ShouldBe(expectWord);

            arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Icon icon }
                    && icon.Kind is Layout.IconKind.ThemeDark)
                .ShouldBe(expectIcon);
        }

        /// <summary>
        /// Three marks cover four states: Night IS a dark scheme, so it shares the crescent. Pinned because
        /// it is the reason the default keeps the label, and a future fourth mark would have to argue with
        /// this test rather than quietly change the meaning of the control.
        /// </summary>
        [Theory]
        [InlineData(UiThemeState.System, Layout.IconKind.ThemeSystem)]
        [InlineData(UiThemeState.Light, Layout.IconKind.ThemeLight)]
        [InlineData(UiThemeState.Dark, Layout.IconKind.ThemeDark)]
        [InlineData(UiThemeState.Night, Layout.IconKind.ThemeDark)]
        public void EachThemeStateShowsItsMark(UiThemeState state, Layout.IconKind expected)
        {
            var tree = HomeBoardLayout.Build(
                ImmutableArray.Create(Card("A")), HomeBoardStyle.Default, 200 * 8f, Now,
                theme: state, onCycleTheme: _ => { });

            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, 200, 56), CellMeasureContext.PixelAuthored);

            var marks = arranged
                .Select(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Icon icon }
                    ? icon.Kind
                    : (Layout.IconKind?)null)
                .Where(k => k is Layout.IconKind.ThemeSystem or Layout.IconKind.ThemeLight
                    or Layout.IconKind.ThemeDark)
                .ToArray();

            marks.ShouldHaveSingleItem().ShouldBe(expected);
        }

        /// <summary>
        /// Every header control is the same square-or-taller box, sharing one top and one bottom, centred on
        /// the bar's own centre line.
        /// <para>
        /// None of that is automatic. A <c>Layout.Node.Stack</c> places children at the cross-axis START, so
        /// a Fixed-height button in a taller bar hugs the top and sits half the difference ABOVE centre --
        /// which is what it did, visibly, until the bar was padded and the children made Star-height. A test
        /// is the only thing that notices when someone reintroduces a Fixed height here.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryHeaderControlSharesOneTopAndBottom_CentredInTheBar()
        {
            // Rendered through the real tab at DPI 1, so an arranged pixel IS a design unit and the bar's
            // 28 and its 4 of padding read as themselves.
            using var renderer = new RgbaImageRenderer(2000, 800);
            var tab = RenderTab(renderer, [Card("A")]);

            var controls = tab.GetRegisteredRegions()
                .Where(r => r.Result is HitResult.ButtonHit { Action: var action }
                    && (action.StartsWith("HomeView:") || action.StartsWith("HomeTheme:")))
                .ToArray();

            controls.Length.ShouldBe(4, "three shape segments plus the theme control");

            foreach (var c in controls)
            {
                c.Y.ShouldBe(controls[0].Y, 0.5f, "every control starts at the same top");
                c.Height.ShouldBe(controls[0].Height, 0.5f, "every control is the same height");

                // Centred: the gap above equals the gap below, within the bar.
                var above = c.Y;
                var below = HomeBoardLayout.HeaderHeight - (c.Y + c.Height);
                below.ShouldBe(above, 0.5f, "the control is centred in the bar, not hugging its top");
                above.ShouldBeGreaterThan(0f, "it stops short of the bar's edge, which is the separation");
            }
        }

        /// <summary>The three shape segments are square, which is the shape a lone pictogram button takes.</summary>
        [Fact]
        public void TheShapeSegmentsAreSquare()
        {
            using var renderer = new RgbaImageRenderer(2000, 800);
            var tab = RenderTab(renderer, [Card("A")]);

            var segments = tab.GetRegisteredRegions()
                .Where(r => r.Result is HitResult.ButtonHit { Action: var action }
                    && action.StartsWith("HomeView:"))
                .ToArray();

            segments.Length.ShouldBe(3);
            foreach (var seg in segments)
            {
                seg.Width.ShouldBe(seg.Height, 0.5f);
            }
        }

        [Fact]
        public void ABoardWithNoThemeCallbackDrawsNoThemeControl()
        {
            // Same rule as the shape selector: the callback IS the permission to offer the control, so a
            // host with no theme to change does not get a button that silently does nothing.
            var tree = HomeBoardLayout.Build(
                ImmutableArray.Create(Card("A")), HomeBoardStyle.Default, 200 * 8f, Now,
                onSelectView: _ => _ => { });

            var arranged = Layout.Engine.Arrange(
                tree, new Rect<int>(0, 0, 200, 56), CellMeasureContext.PixelAuthored);

            arranged.Count(a =>
                    a.Node.Hit is HitResult.ButtonHit { Action: var action }
                    && action.StartsWith("HomeTheme:"))
                .ShouldBe(0);
        }

        [Fact]
        public void ANarrowTableDropsWholeColumnsInsteadOfTruncatingEveryCell()
        {
            var rigs = ImmutableArray.Create(FullCard("A"), FullCard("B"));

            var wide = Board(rigs, cols: 200, rows: 40, view: HomeBoardView.Table);
            var narrow = Board(rigs, cols: 60, rows: 40, view: HomeBoardView.Table);

            // Wide enough for every column.
            wide.Texts.ShouldContain("Rig");
            wide.Texts.ShouldContain("Cooling");
            wide.Texts.ShouldContain("RMS");

            // Squeezed: the detail columns come off entirely rather than every cell being truncated into
            // ambiguity, and what remains is what a row means -- which rig, and what it is doing.
            narrow.Texts.ShouldContain("Rig");
            narrow.Texts.ShouldContain("Status");
            narrow.Texts.ShouldNotContain("RMS");
            narrow.Texts.ShouldNotContain("Cooling");
            narrow.RigRows.ShouldBe(2);
        }

        [Fact]
        public void AWaitingRigKeepsItsBadgeInTheTable()
        {
            // The reason the cramped case is a table and not a stack of overlapping cards: every rig stays
            // visible, so a badge can never be behind another rig.
            var waiting = Card("B", running: true,
                prompt: new RigCardPrompt("Manual flat panel", TimeSpan.FromMinutes(40), true));

            var board = Board(ImmutableArray.Create(Card("A"), waiting), cols: 120, rows: 20,
                view: HomeBoardView.Table);

            board.RigRows.ShouldBe(2);
            board.Texts.ShouldContain(t => t.StartsWith("WAITING"));
        }

        private static string[] Texts(ImmutableArray<Layout.ArrangedNode<int>> arranged) =>
            [.. arranged
                .Select(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } ? text.Value : null)
                .Where(t => t is not null)
                .Select(t => t!)];
    }
}
