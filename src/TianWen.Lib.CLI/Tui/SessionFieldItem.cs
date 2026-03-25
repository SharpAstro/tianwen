using Console.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// A row in the session config scrollable list. Either a group header or a config field.
/// </summary>
internal sealed class SessionFieldItem : IRowFormatter
{
    /// <summary>Group header (non-editable separator row).</summary>
    public string? GroupName { get; init; }

    /// <summary>Field descriptor (null for group headers).</summary>
    public ConfigFieldDescriptor? Field { get; init; }

    /// <summary>Flat index among editable fields (for selection tracking). -1 for headers.</summary>
    public int FieldIndex { get; init; } = -1;

    /// <summary>Whether this field is currently selected.</summary>
    public bool IsSelected { get; init; }

    /// <summary>Current formatted value (precomputed from SessionConfiguration).</summary>
    public string FormattedValue { get; init; } = "";

    public string FormatRow(int width, ColorMode colorMode)
    {
        if (GroupName is not null)
        {
            // Group header
            var header = $"\u2500\u2500 {GroupName} \u2500\u2500";
            var style = new VtStyle(SgrColor.BrightBlue, SgrColor.Black);
            return $"{style.Apply(colorMode)}{header.PadRight(width)}{VtStyle.Reset}";
        }

        if (Field is null)
        {
            return "".PadRight(width);
        }

        // Field row: "  Label            [-] value [+]  unit"
        var label = Field.Label;
        var value = FormattedValue;
        var unit = Field.Unit.Length > 0 ? $" {Field.Unit}" : "";
        var controlStr = Field.Kind switch
        {
            ConfigFieldKind.BoolToggle => $"  [{value}]",
            ConfigFieldKind.EnumCycle => $"  [{value}]",
            _ => $"  [\u2190] {value}{unit} [\u2192]",
        };

        var labelWidth = Math.Max(18, width / 2);
        var paddedLabel = label.Length > labelWidth ? label[..(labelWidth - 1)] + "." : label;
        var line = $"  {paddedLabel.PadRight(labelWidth)}{controlStr}";

        if (line.Length > width)
        {
            line = line[..width];
        }

        if (IsSelected)
        {
            var style = new VtStyle(SgrColor.BrightWhite, SgrColor.Blue);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }

        return line.PadRight(width);
    }
}
