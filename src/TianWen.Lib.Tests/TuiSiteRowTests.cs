using System.IO;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using TianWen.Cli.Tui;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The TUI site row, asserted from the cells it actually paints.
    /// <para>
    /// <b>This used to test string arithmetic, and that is the point of the change.</b> The row composed one
    /// joined string and derived the caret's column from the indent, every earlier field's rendered length,
    /// its separator, and the edited field's own label prefix -- so the tests pinned that derivation against
    /// the string it indexed. Every one of those terms had to be re-derived whenever the row's shape moved,
    /// which is a cost the arranged rect was already paying on the row's behalf.
    /// </para>
    /// <para>
    /// The row is now three <see cref="Layout.Content.TextInput"/> leaves, the same leaf the GUI declares. So
    /// these arrange and PAINT it and read the result back: what the caret sits on is answered by the
    /// terminal cells and the caret request, not by indexing a string the test built its own expectation
    /// from. That is a strictly stronger assertion -- it would catch a painter that drew the row correctly
    /// and put the caret somewhere else, which the old shape could not express.
    /// </para>
    /// </summary>
    public class TuiSiteRowTests
    {
        private const int Width = 80;

        /// <summary>Records the caret request alongside the painted cells; the row's two outputs.</summary>
        private sealed class RowViewport(CellBuffer buffer) : ITerminalViewport
        {
            public (int Column, int Row) Offset => (0, 0);
            public (int Width, int Height) Size => (Width, 1);
            public TermCell CellSize => new TermCell(10, 20);
            public ColorMode ColorMode => Console.Lib.ColorMode.TrueColor;

            public (int Column, int Row, CaretStyle Style)? Caret { get; private set; }

            public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
            public void Write(string text) => buffer.Write(text);
            public void WriteLine(string? text = null) { }
            public void Flush() { }
            public Stream OutputStream => Stream.Null;
            public void SetCaret(int column, int row, CaretStyle style) => Caret = (column, row, style);
        }

        private static TextInputState Field(string text) => new TextInputState { Text = text };

        /// <summary>Arranges and paints the row, returning the painted line and where the caret was parked.</summary>
        private static (string Row, int? Caret) Paint(int focusedIndex, int cursorPos,
            string lat = "33.8", string lon = "151.2", string elev = "58")
        {
            var fields = new[] { Field(lat), Field(lon), Field(elev) };
            fields[focusedIndex].Activate();
            fields[focusedIndex].CursorPos = cursorPos;

            var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
            buffer.Resize(Width, 1);
            var viewport = new RowViewport(buffer);

            var arranged = Layout.Engine.Arrange(
                TuiEquipmentTab.SiteEditRow(fields[0], fields[1], fields[2]),
                new Rect<int>(0, 0, Width, 1), CellMeasureContext.CellAuthored);
            CellLayout.Paint(viewport, arranged);

            var chars = new char[Width];
            for (var i = 0; i < Width; i++)
            {
                chars[i] = buffer.BackAt(i, 0).Glyph;
            }

            return (new string(chars), viewport.Caret?.Column);
        }

        /// <summary>
        /// The invariant the whole feature rests on, unchanged in meaning and stronger in evidence: the
        /// caret sits on the character the cursor is logically on. Asserting the CHARACTER rather than a
        /// column number is what makes it survive a relabelling -- get the row's shape wrong and the caret
        /// lands on a label or a neighbouring field instead.
        /// </summary>
        [Theory]
        [InlineData(0, 0, '3')]  // first char of "33.8"
        [InlineData(0, 2, '.')]  // "33|.8"
        [InlineData(1, 0, '1')]  // first char of "151.2"
        [InlineData(1, 3, '.')]  // "151|.2"
        [InlineData(2, 1, '8')]  // "5|8"
        public void TheCaretSitsOnTheCharacterTheCursorIsOn(int focusedIndex, int cursorPos, char expected)
        {
            var (row, caret) = Paint(focusedIndex, cursorPos);

            caret.ShouldNotBeNull();
            row[caret.Value].ShouldBe(expected);
        }

        /// <summary>
        /// A caret at the end of a value needs a cell of its own past the last character -- and unlike the
        /// old row, that cell is the field's own padding rather than a bracket the row had to draw to give
        /// the caret somewhere to stand.
        /// </summary>
        [Theory]
        [InlineData(0, "33.8")]
        [InlineData(1, "151.2")]
        [InlineData(2, "58")]
        public void ACaretAtTheEndOfAValue_LandsJustPastIt(int focusedIndex, string value)
        {
            var (row, caret) = Paint(focusedIndex, value.Length);

            caret.ShouldNotBeNull();
            row[caret.Value].ShouldBe(' ');
            row[caret.Value - 1].ShouldBe(value[^1], "the caret follows the value, so it sits right after it");
        }

        [Fact]
        public void AnEmptyFocusedField_PutsTheCaretRightAfterItsLabel()
        {
            var (row, caret) = Paint(0, 0, lat: "");

            row.ShouldStartWith(" Lat: ");
            caret.ShouldBe(" Lat: ".Length);
        }

        [Fact]
        public void EveryFieldIsLabelled_AndTheHintsAreOnTheRow()
        {
            var (row, _) = Paint(1, 0);

            row.ShouldContain(" Lat: 33.8");
            row.ShouldContain(" Lon: 151.2");
            row.ShouldContain(" Elev: 58");
            row.ShouldContain("Tab:next  Enter:save  Esc:cancel");
        }

        /// <summary>
        /// Only the focused field gets the caret. There is one keyboard, so a row that parked two would be
        /// showing the user a choice that does not exist.
        /// </summary>
        [Fact]
        public void OnlyTheFocusedFieldIsMarked()
        {
            var lat = Field("33.8");
            var lon = Field("151.2");
            var elev = Field("58");
            lon.Activate();

            var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
            buffer.Resize(Width, 1);
            var viewport = new RowViewport(buffer);
            CellLayout.Paint(viewport, Layout.Engine.Arrange(
                TuiEquipmentTab.SiteEditRow(lat, lon, elev),
                new Rect<int>(0, 0, Width, 1), CellMeasureContext.CellAuthored));

            lat.IsActive.ShouldBeFalse();
            elev.IsActive.ShouldBeFalse();
            viewport.Caret.ShouldNotBeNull();
        }

        /// <summary>
        /// A stale cursor index arriving from a field that shrank must not park the caret off the row.
        /// <see cref="TextInputState"/> clamps its own, but the painter is what has to survive one that got
        /// through -- previously this was arithmetic that could produce a column past the string.
        /// </summary>
        [Fact]
        public void ACursorPastTheEndOfItsValue_StaysOnTheRow()
        {
            var (row, caret) = Paint(2, 99);

            caret.ShouldNotBeNull();
            caret.Value.ShouldBeInRange(0, row.Length - 1);
        }
    }
}
