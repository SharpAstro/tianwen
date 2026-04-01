using Console.Lib;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// A row in the equipment settings scrollable list.
/// Either a section header or an interactive device setting.
/// </summary>
internal sealed class EquipmentFieldItem : IRowFormatter
{
    /// <summary>Section header (non-editable separator row).</summary>
    public string? SectionName { get; init; }

    /// <summary>Device setting descriptor (null for section headers).</summary>
    public DeviceSettingDescriptor? Setting { get; init; }

    /// <summary>URI being edited — used to format current value.</summary>
    public Uri? DeviceUri { get; init; }

    /// <summary>Flat index among editable fields (for selection tracking). -1 for headers.</summary>
    public int FieldIndex { get; init; } = -1;

    /// <summary>Whether this field is currently selected.</summary>
    public bool IsSelected { get; init; }

    /// <summary>Increment callback.</summary>
    public Action? Increment { get; init; }

    /// <summary>Decrement callback.</summary>
    public Action? Decrement { get; init; }

    public string FormatRow(int width, ColorMode colorMode)
    {
        if (SectionName is not null)
        {
            var header = $"\u2500\u2500 {SectionName} \u2500\u2500";
            var style = new VtStyle(SgrColor.BrightBlue, SgrColor.Black);
            return $"{style.Apply(colorMode)}{header.PadRight(width)}{VtStyle.Reset}";
        }

        if (Setting is not { } setting || DeviceUri is null)
        {
            return "".PadRight(width);
        }

        var label = setting.Label;
        var value = setting.FormatValue(DeviceUri);
        var controlStr = setting.Kind switch
        {
            DeviceSettingKind.BoolToggle => $"  [{value}]",
            DeviceSettingKind.EnumCycle => $"  [{value}]",
            _ => $"  [\u2190] {value} [\u2192]",
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
