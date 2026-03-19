using Console.Lib;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.CLI.Plan;

/// <summary>
/// A target entry for the planner's scrollable target list.
/// </summary>
internal sealed class TargetListItem(ScoredTarget scored, bool isProposed, bool isSelected) : IRowFormatter
{
    public ScoredTarget Scored { get; } = scored;
    public bool IsProposed { get; } = isProposed;
    public bool IsSelected { get; } = isSelected;

    public string FormatRow(int width, ColorMode colorMode)
    {
        var marker = IsProposed ? "* " : "  ";
        var name = Scored.Target.Name;
        var nameWidth = width - 14; // marker(2) + alt(4) + score(6) + spaces(2)
        if (name.Length > nameWidth)
        {
            name = name[..(nameWidth - 1)] + ".";
        }

        var alt = $"{Scored.OptimalAltitude:F0}°";
        var score = $"{Scored.CombinedScore:F0}";

        var line = $"{marker}{name.PadRight(nameWidth)} {alt,4} {score,5}";

        if (IsSelected && IsProposed)
        {
            var style = new VtStyle(SgrColor.BrightWhite, SgrColor.Blue);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }
        if (IsSelected)
        {
            var style = new VtStyle(SgrColor.BrightWhite, SgrColor.BrightBlack);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }
        if (IsProposed)
        {
            var style = new VtStyle(SgrColor.BrightCyan, SgrColor.Black);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }

        return line.PadRight(width);
    }
}
