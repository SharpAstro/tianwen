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
        private static RigCard Card(
            string title, bool running = false, RigCardPrompt? prompt = null, RigDeviceLink? devices = null) =>
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
                IsViewed: false);

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
                tab.Render(appState, new RectF32(0, 0, renderer.Width, renderer.Height));
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
            HomeBoardLayout.ColumnsFor(HomeBoardLayout.MinCardWidth * 3 + HomeBoardLayout.CardGap * 2).ShouldBe(3);
            HomeBoardLayout.ColumnsFor(HomeBoardLayout.MinCardWidth).ShouldBe(1);

            // Never zero, however little room there is -- a zero-column grid would drop every card silently.
            HomeBoardLayout.ColumnsFor(0f).ShouldBe(1);
            HomeBoardLayout.ColumnsFor(-50f).ShouldBe(1);
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

            new RigDeviceLink(6, 6).Describe().ShouldNotBe(new RigDeviceLink(4, 6).Describe());
            new RigDeviceLink(4, 6).Describe().ShouldContain("4/6");

            // No emoji: the layout painter has no per-run font fallback, so a glyph here would render
            // as blank space rather than a socket. See RigDeviceLink.Describe.
            new RigDeviceLink(4, 6).Describe().ShouldAllBe(c => c < 0x2000);
        }

        [Fact]
        public void RepaintingDoesNotAccumulateClickableRegions()
        {
            using var renderer = new RgbaImageRenderer(1600, 1000);
            var tab = RenderTab(renderer, [Card("This computer"), Card("Backyard")], frames: 30);

            Cards(tab).Length.ShouldBe(2);
        }
    }
}
