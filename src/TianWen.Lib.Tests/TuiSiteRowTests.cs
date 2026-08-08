using Shouldly;
using TianWen.Cli.Tui;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The site row packs three editable fields into one bar, so the terminal's real caret has to be
    /// placed by an offset into the JOINED string rather than derived from a single field's state. These
    /// pin that arithmetic against the string it indexes: the row and the column are produced together, so
    /// the assertion can ask what character the caret is actually sitting on.
    /// <para>
    /// The row previously spliced reverse-video escapes around the cursor character instead, which is why
    /// the edited field had to be re-formatted per keystroke and why a cursor at end-of-value needed a
    /// padding space to have a cell to invert. A parked caret needs neither.
    /// </para>
    /// </summary>
    public class TuiSiteRowTests
    {
        private static string[] Values(string lat = "33.8", string lon = "151.2", string elev = "58")
            => [lat, lon, elev];

        /// <summary>
        /// The invariant the whole feature rests on: the caret column indexes the character the cursor is
        /// logically on. Asserting the CHARACTER rather than a hard-coded number is what makes this survive
        /// a relabelling -- get the prefix arithmetic wrong and the caret lands on a bracket or a digit of
        /// the label instead.
        /// </summary>
        [Theory]
        [InlineData(0, 0, '3')]  // first char of "33.8"
        [InlineData(0, 2, '.')]  // "33|.8"
        [InlineData(1, 0, '1')]  // first char of "151.2"
        [InlineData(1, 3, '.')]  // "151|.2"
        [InlineData(2, 1, '8')]  // "5|8"
        public void CaretColumn_IndexesTheCharacterTheCursorIsOn(int editIndex, int cursorPos, char expected)
        {
            var (row, caretColumn) = TuiEquipmentTab.ComposeSiteRow(Values(), editIndex, cursorPos);

            row[caretColumn].ShouldBe(expected);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void CaretAtEndOfValue_LandsOnTheClosingBracket(int editIndex)
        {
            // A thin bar draws at the left edge of its cell, so parking on "]" puts it exactly between the
            // last character and the bracket -- where the next typed character will go.
            var values = Values();
            var (row, caretColumn) = TuiEquipmentTab.ComposeSiteRow(values, editIndex, values[editIndex].Length);

            row[caretColumn].ShouldBe(']');
        }

        [Fact]
        public void EmptyEditedValue_CaretSitsInsideTheEmptyBrackets()
        {
            var (row, caretColumn) = TuiEquipmentTab.ComposeSiteRow(Values(lat: ""), 0, 0);

            row.ShouldStartWith(" Lat: []");
            row[caretColumn].ShouldBe(']');
        }

        [Fact]
        public void OnlyTheEditedFieldIsBracketed()
        {
            var (row, _) = TuiEquipmentTab.ComposeSiteRow(Values(), 1, 0);

            row.ShouldBe(" Lat: 33.8  Lon: [151.2]  Elev: 58");
        }

        [Fact]
        public void EmptyNonEditedValueShowsEllipsisNotEmptyBrackets()
        {
            var (row, _) = TuiEquipmentTab.ComposeSiteRow(Values(elev: ""), 0, 0);

            row.ShouldBe(" Lat: [33.8]  Lon: 151.2  Elev: ...");
        }

        [Fact]
        public void CursorPastEndOfValue_ClampsToTheBracketRatherThanRunningOffTheRow()
        {
            // TextInputState clamps its own cursor, but a stale index arriving from a field that shrank
            // must not produce a column past the string the caller is about to index.
            var (row, caretColumn) = TuiEquipmentTab.ComposeSiteRow(Values(), 2, 99);

            caretColumn.ShouldBeLessThan(row.Length);
            row[caretColumn].ShouldBe(']');
        }
    }
}
