using Console.Lib;
using DIR.Lib;
using TianWen.Cli.Tui;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Plan
{
    /// <summary>
    /// A target entry for the planner's scrollable target list.
    /// Constructed from a <see cref="PlannerTargetRow"/> (shared content model).
    /// </summary>
    internal sealed class TargetListItem(PlannerTargetRow row) : IRowLayout
    {
        public PlannerTargetRow Row { get; } = row;

        /// <summary>Type code column, and the trailing space that separates it from the info column.</summary>
        private const int TypeColumns = 4;

        /// <summary>Info column, with a leading space (the value is right-aligned within it).</summary>
        private const int InfoColumns = 5;

        /// <summary>Rating column, with a leading space.</summary>
        private const int RatingColumns = 6;

        /// <summary>
        /// Selection and pinning are the row's OWN facts, not the list cursor's: the planner's selected
        /// index lives in <see cref="PlannerState"/> (shared with the GUI), and the pin state comes from
        /// the proposal list. So this reads <see cref="Row"/> and ignores <paramref name="context"/>.
        /// </summary>
        public Layout.Node BuildRow(in RowContext context)
        {
            // A selected pin keeps the blue selection bar; a selected unpinned row is grey, so the two
            // stay distinguishable while the cursor is on either.
            var pen = (Row.IsSelected, Row.IsPinned) switch
            {
                (true, true) => TuiRowPalette.Selected,
                (true, false) => new RowPen(SgrColor.BrightWhite, SgrColor.BrightBlack),
                (false, true) => new RowPen(SgrColor.BrightCyan, SgrColor.Black),
                _ => TuiRowPalette.Body,
            };

            var objType = Row.ObjectType.Length > TypeColumns ? Row.ObjectType[..TypeColumns] : Row.ObjectType;

            // The name column was `width - 19` with the 19 spelled out in a comment that had already
            // drifted from the columns below it (it read 2+5+4+6+2). It is a Star now, so the fixed
            // columns are the only arithmetic and they cannot disagree with themselves.
            return Layout.Builder.HStack(
                pen.Cell(Row.IsPinned ? "* " : "  ", 2),
                pen.Text(Row.Name),
                pen.Gap(1),
                pen.Cell(objType, TypeColumns),
                pen.Cell(Row.Info, InfoColumns, TextAlign.Far),
                pen.Cell($"{Row.Rating:F1}★", RatingColumns, TextAlign.Far))
                .RowH(1).Bg(pen.Background);
        }
    }
}
