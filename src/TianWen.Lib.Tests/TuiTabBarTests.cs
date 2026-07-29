using Console.Lib;
using DIR.Lib;
using Shouldly;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using TianWen.Cli.Tui;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the TUI tab bar's geometry (issue #125). Arranged without a terminal, so these assert the drawn
    /// rects directly.
    /// <para>
    /// The bar used to be a pre-joined string in a <see cref="TextBar"/> plus a static hit test that
    /// re-derived the column ranges from the label array. <c>TextBar</c> gives its right-hand text priority
    /// and ellipsizes the LEFT text, and the hit test knew nothing about that -- so on a narrow terminal the
    /// bar stopped drawing the later tabs while still reporting them, and clicking the profile name or the
    /// clock switched to Notifications.
    /// </para>
    /// </summary>
    public class TuiTabBarTests
    {
        private const string Status = "My Observatory  22:14:03 ";

        /// <summary>
        /// A viewport that writes nowhere. The bar needs one to construct but not to arrange, which is the
        /// point of these tests: the geometry is assertable with no terminal at all.
        /// </summary>
        private sealed class NullViewport(int width) : ITerminalViewport
        {
            public (int Column, int Row) Offset => (0, 0);
            public (int Width, int Height) Size => (width, 1);
            public TermCell CellSize => new TermCell(8, 16);
            public Stream OutputStream => Stream.Null;
            public void SetCursorPosition(int left, int top) { }
            public void Write(string text) { }
            public void WriteLine(string? text = null) { }
            public void Flush() { }
        }

        private static TuiTabBar Bar() => new TuiTabBar(new NullViewport(200));

        private static ImmutableArray<Layout.ArrangedNode<int>> Arrange(TuiTabBar bar, int width) =>
            bar.Arrange(GuiTab.Equipment, Status, width);

        private static (string Text, Rect<int> Rect)[] TabRects(ImmutableArray<Layout.ArrangedNode<int>> arranged) =>
            [.. arranged
                .Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("Tab:"))
                .Select(a => (Text: (a.Node as Layout.Node.Leaf)?.Content is Layout.Content.Text t ? t.Value : "", a.Bounds))
                .OrderBy(x => x.Bounds.X)];

        /// <summary>Paints into a real <see cref="CellBuffer"/>, so what the diff would emit is assertable.</summary>
        private sealed class BufferedViewport(CellBuffer buffer, int width) : ITerminalViewport
        {
            public (int Column, int Row) Offset => (0, 0);
            public (int Width, int Height) Size => (width, 1);
            public TermCell CellSize => new TermCell(8, 16);
            public Stream OutputStream => Stream.Null;
            public ColorMode ColorMode => ColorMode.Sgr16;
            public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
            public void Write(string text) => buffer.Write(text);
            public void WriteLine(string? text = null) { }
            public void Flush() { }
        }

        /// <summary>Records each emitted run's position and text, so a failure names the exact cells.</summary>
        private sealed class RunRecordingSink : ICellSink
        {
            private (int Column, int Row) _at;
            public readonly List<(int Column, int Row, string Text)> Runs = [];
            public void MoveTo(int column, int row) => _at = (column, row);
            public void SetPen(VtStyle style, bool reverse) { }
            public void Write(System.ReadOnlySpan<char> run) => Runs.Add((_at.Column, _at.Row, run.ToString()));
        }

        /// <summary>
        /// The reason the TUI has a cell buffer at all: the clock ticks once a second, and the ONLY cells
        /// that may reach the terminal for it are the digits that flipped. This paints the REAL bar for two
        /// consecutive seconds and asserts the second flush — anything more re-emitted here is what the user
        /// sees as a once-per-second flicker of the top bar, which is exactly how the regression this pins
        /// was reported.
        /// </summary>
        [Fact]
        public void AClockTick_EmitsOnlyTheFlippedDigits()
        {
            const int Width = 200;
            var buffer = new CellBuffer { ColorMode = ColorMode.Sgr16 };
            buffer.Resize(Width, 1);
            var viewport = new BufferedViewport(buffer, Width);
            var bar = new TuiTabBar(viewport);

            CellLayout.Paint(viewport, bar.Arrange(GuiTab.Equipment, "My Observatory  22:14:03 ", Width));
            buffer.Flush(new RunRecordingSink());

            CellLayout.Paint(viewport, bar.Arrange(GuiTab.Equipment, "My Observatory  22:14:04 ", Width));
            var sink = new RunRecordingSink();
            var emitted = buffer.Flush(sink);

            var runs = string.Join("; ", sink.Runs.Select(r => $"({r.Column},{r.Row})='{r.Text}'"));
            emitted.ShouldBe(1, $"one digit flipped, so one cell may go out — emitted runs: {runs}");
        }

        [Fact]
        public void EveryTabsClickRegionIsExactlyTheCellsItsLabelOccupies()
        {
            // Draw == hit by construction: the region IS the arranged rect of the text node, so a change to
            // the separator or the active-tab decoration cannot desynchronise them any more.
            var arranged = Arrange(Bar(), width: 120);

            foreach (var (text, rect) in TabRects(arranged))
            {
                rect.Width.ShouldBe(text.Length);
                rect.Height.ShouldBe(1);
                rect.Y.ShouldBe(0);
            }
        }

        [Fact]
        public void ClickingTheStatusTextDoesNotSwitchTabs()
        {
            // The reported defect, on the reported width. At 80 columns the status reserves 25, so the later
            // tabs do not fit -- and must therefore not be hit-testable at the columns the status occupies.
            var bar = Bar();
            var arranged = Arrange(bar, width: 80);

            var statusStart = 80 - Status.Length;
            foreach (var (_, rect) in TabRects(arranged))
            {
                (rect.X + rect.Width).ShouldBeLessThanOrEqualTo(statusStart,
                    "a tab drawn under the status text is a tab that mis-hits");
            }
        }

        [Fact]
        public void ATabThatDoesNotFitIsAbsentRatherThanDrawnUnderTheStatus()
        {
            // Dropping is what makes the fix structural: an absent tab cannot be hit, whereas a truncated
            // string leaves a region that is hit but not visible.
            // Derived, not hardcoded: a bar wide enough for everything defines the total, so adding a tab
            // does not make this test wrong.
            var everything = TabRects(Arrange(Bar(), width: 400));
            var wide = TabRects(Arrange(Bar(), width: 120));
            var narrow = TabRects(Arrange(Bar(), width: 60));

            wide.Length.ShouldBe(everything.Length);
            narrow.Length.ShouldBeLessThan(everything.Length);
            narrow.Length.ShouldBeGreaterThan(0, "the bar should still show what it can");
        }

        [Fact]
        public void AVeryNarrowBarShowsNoTabsAtAllRatherThanUnclickableOnes()
        {
            // Previously padWidth <= 1 blanked the tab text while every column still resolved to a tab.
            TabRects(Arrange(Bar(), width: Status.Length)).ShouldBeEmpty();
        }

        [Fact]
        public void HitTestingBelowTheBarMisses()
        {
            // The bar is one row tall, so the row is part of the test -- no caller has to hardcode row 0.
            var bar = Bar();
            var arranged = Arrange(bar, width: 120);
            var firstTab = TabRects(arranged)[0];

            // Sanity: the same column on the bar's own row does hit.
            arranged.Any(a => a.Node.Hit is not null && a.Bounds.Contains(firstTab.Rect.X, 0)).ShouldBeTrue();
            arranged.Any(a => a.Node.Hit is not null && a.Bounds.Contains(firstTab.Rect.X, 3)).ShouldBeFalse();
        }
    }
}
