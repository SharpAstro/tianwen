using Console.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Plan;

/// <summary>
/// A target entry for the planner's scrollable target list.
/// Constructed from a <see cref="PlannerTargetRow"/> (shared content model).
/// </summary>
internal sealed class TargetListItem(PlannerTargetRow row) : IRowFormatter
{
    public PlannerTargetRow Row { get; } = row;

    public string FormatRow(int width, ColorMode colorMode)
    {
        var marker = Row.IsPinned ? "* " : "  ";
        var name = Row.Name;
        var objType = Row.ObjectType.Length > 4 ? Row.ObjectType[..4] : Row.ObjectType;
        var nameWidth = width - 19; // marker(2) + type(5) + info(4) + rating(6) + spaces(2)
        if (name.Length > nameWidth)
        {
            name = name[..(nameWidth - 1)] + ".";
        }

        var ratingStr = $"{Row.Rating:F1}\u2605";

        var line = $"{marker}{name.PadRight(nameWidth)} {objType,-4} {Row.Info,4} {ratingStr,5}";

        if (Row.IsSelected && Row.IsPinned)
        {
            var style = new VtStyle(SgrColor.BrightWhite, SgrColor.Blue);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }
        if (Row.IsSelected)
        {
            var style = new VtStyle(SgrColor.BrightWhite, SgrColor.BrightBlack);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }
        if (Row.IsPinned)
        {
            var style = new VtStyle(SgrColor.BrightCyan, SgrColor.Black);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }

        return line.PadRight(width);
    }
}
