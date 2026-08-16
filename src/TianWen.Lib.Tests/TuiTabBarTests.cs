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

        /// <summary>
        /// The arranged tab regions, paired with the label drawn inside each.
        /// </summary>
        /// <remarks>
        /// A tab is a <see cref="HitResult.ListItemHit"/> on the tab's CONTAINER since the strip became
        /// <see cref="TabStripTree"/>'s shared description -- it was a ButtonHit on the text leaf itself.
        /// The label therefore comes from the Text leaf arranged INSIDE the region rather than from the hit
        /// node, which is also why every caller here asserts against it rather than against a literal.
        /// </remarks>
        private static (string Text, Rect<int> Rect)[] TabRects(ImmutableArray<Layout.ArrangedNode<int>> arranged) =>
            [.. arranged
                .Where(a => a.Node.Hit is HitResult.ListItemHit { ListId: TabBarRegions.Tabs })
                .Select(a => (Text: LabelIn(arranged, a.Bounds), Rect: a.Bounds))
                .OrderBy(x => x.Rect.X)];

        /// <summary>The text drawn inside <paramref name="bounds"/>, or "" if none was.</summary>
        private static string LabelIn(ImmutableArray<Layout.ArrangedNode<int>> arranged, Rect<int> bounds)
        {
            foreach (var node in arranged)
            {
                if (node.Node is Layout.Node.Leaf { Content: Layout.Content.Text text }
                    && node.Bounds.X >= bounds.X
                    && node.Bounds.X + node.Bounds.Width <= bounds.X + bounds.Width
                    && node.Bounds.Y >= bounds.Y)
                {
                    return text.Value;
                }
            }

            return "";
        }

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
            // ICellSink makes this required rather than defaulted so a real sink cannot silently
            // drop every hyperlink in a frame. This sink measures cell churn, not link fidelity,
            // and the tab bar emits no links at all, so dropping it here loses nothing.
            public void SetLink(string? url) { }
            public void Write(System.ReadOnlySpan<char> run) => Runs.Add((_at.Column, _at.Row, run.ToString()));
        }

        /// <summary>
        /// The reason the TUI has a cell buffer at all: the clock ticks once a second, and the ONLY cells
        /// that may reach the terminal for it are the digits that flipped. This paints the REAL bar for two
        /// consecutive seconds and asserts the second flush; anything more re-emitted here is what the user
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
            emitted.ShouldBe(1, $"one digit flipped, so one cell may go out, emitted runs: {runs}");
        }

        [Fact]
        public void EveryTabsClickRegionIsExactlyTheCellsItsLabelOccupies()
        {
            // Draw == hit by construction: the region IS the arranged rect of the text node, so a change to
            // the separator or the active-tab decoration cannot desynchronise them any more.
            var arranged = Arrange(Bar(), width: 120);
            var tabs = TabRects(arranged);

            // Or the loop below asserts nothing at all -- which is how this test kept passing when the
            // strip's region identity changed underneath it and TabRects started returning nothing.
            tabs.ShouldNotBeEmpty();

            foreach (var (text, rect) in tabs)
            {
                rect.Width.ShouldBe(text.Length, $"tab '{text}' arranged at x={rect.X} w={rect.Width}");
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
            var tabs = TabRects(arranged);
            tabs.ShouldNotBeEmpty();

            foreach (var (_, rect) in tabs)
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
