using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// A row in the equipment settings scrollable list.
/// Supports section headers, device slot rows, OTA headers, property steppers,
/// filter rows, device settings, and action rows.
/// </summary>
internal sealed class EquipmentFieldItem : IRowFormatter
{
    // --- Common ---

    /// <summary>Section header (non-editable separator row).</summary>
    public string? SectionName { get; init; }

    /// <summary>Flat index among editable fields (for selection tracking). -1 for headers.</summary>
    public int FieldIndex { get; init; } = -1;

    /// <summary>Whether this field is currently selected.</summary>
    public bool IsSelected { get; init; }

    /// <summary>Increment callback.</summary>
    public Action? Increment { get; init; }

    /// <summary>Decrement callback.</summary>
    public Action? Decrement { get; init; }

    // --- Device setting rows (original) ---

    /// <summary>Device setting descriptor (null for non-setting rows).</summary>
    public DeviceSettingDescriptor? Setting { get; init; }

    /// <summary>URI being edited — used to format current value.</summary>
    public Uri? DeviceUri { get; init; }

    // --- Device slot rows ---

    /// <summary>Assignment target for device slot rows.</summary>
    public AssignTarget? Slot { get; init; }

    /// <summary>Label for the slot (e.g. "Mount", "Camera").</summary>
    public string? SlotLabel { get; init; }

    /// <summary>Display name of the currently assigned device.</summary>
    public string? SlotDeviceName { get; init; }

    /// <summary>Whether this slot has a device assigned (not NoneDevice).</summary>
    public bool IsSlotActive { get; init; }

    // --- OTA header rows ---

    /// <summary>OTA index for OTA headers (-1 for non-OTA rows).</summary>
    public int OtaIndex { get; init; } = -1;

    /// <summary>Whether this row is an OTA header (with Add/Delete actions).</summary>
    public bool IsOtaHeader { get; init; }

    // --- Property stepper rows ---

    /// <summary>Label for property rows (FL, Aperture, Design).</summary>
    public string? PropertyLabel { get; init; }

    /// <summary>Formatted value for property/stepper rows.</summary>
    public string? PropertyValue { get; init; }

    /// <summary>Whether this is a toggle/cycle field (no ←/→ arrows).</summary>
    public bool IsCycleField { get; init; }

    // --- Filter rows ---

    /// <summary>Filter slot index (1-based), or -1 for non-filter rows.</summary>
    public int FilterIndex { get; init; } = -1;

    /// <summary>Display name of the filter.</summary>
    public string? FilterName { get; init; }

    /// <summary>Focus offset value.</summary>
    public int FilterOffset { get; init; }

    // --- Inline text input ---

    /// <summary>Active text input state (for filter name inline editing).</summary>
    public TextInputState? InlineInput { get; init; }

    // --- Action rows ---

    /// <summary>Action label (e.g. "+ Add OTA").</summary>
    public string? ActionLabel { get; init; }

    public string FormatRow(int width, ColorMode colorMode)
    {
        // Section header
        if (SectionName is not null)
        {
            var header = $"\u2500\u2500 {SectionName} \u2500\u2500";
            if (IsOtaHeader)
            {
                // OTA headers show [A]dd [X] actions
                var actions = "  [A]dd [X]";
                var maxHeader = width - actions.Length;
                if (header.Length > maxHeader)
                {
                    header = header[..maxHeader];
                }
                header = header.PadRight(maxHeader) + actions;
            }
            var headerStyle = new VtStyle(SgrColor.BrightBlue, SgrColor.Black);
            return $"{headerStyle.Apply(colorMode)}{header.PadRight(width)}{VtStyle.Reset}";
        }

        // Action row (e.g. "+ Add OTA")
        if (ActionLabel is not null)
        {
            var actionLine = $"  {ActionLabel}";
            if (IsSelected)
            {
                var style = new VtStyle(SgrColor.BrightGreen, SgrColor.Black);
                return $"{style.Apply(colorMode)}{actionLine.PadRight(width)}{VtStyle.Reset}";
            }
            var dimStyle = new VtStyle(SgrColor.Green, SgrColor.Black);
            return $"{dimStyle.Apply(colorMode)}{actionLine.PadRight(width)}{VtStyle.Reset}";
        }

        // Device slot row
        if (SlotLabel is not null && Slot is not null)
        {
            return FormatSlotRow(width, colorMode);
        }

        // Filter row
        if (FilterIndex > 0 && FilterName is not null)
        {
            return FormatFilterRow(width, colorMode);
        }

        // Property stepper row
        if (PropertyLabel is not null)
        {
            return FormatPropertyRow(width, colorMode);
        }

        // Device setting row (original)
        if (Setting is { } setting && DeviceUri is not null)
        {
            return FormatSettingRow(setting, width, colorMode);
        }

        return "".PadRight(width);
    }

    private string FormatSlotRow(int width, ColorMode colorMode)
    {
        var labelWidth = Math.Max(14, width / 3);
        var paddedLabel = SlotLabel!.Length > labelWidth ? SlotLabel[..(labelWidth - 1)] + "." : SlotLabel;
        var marker = IsSlotActive ? "\u2705" : "\u274c";
        var name = SlotDeviceName ?? "(none)";
        var line = $"  {paddedLabel.PadRight(labelWidth)} {marker} {name}  [>]";

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

    private string FormatFilterRow(int width, ColorMode colorMode)
    {
        var offsetStr = FilterOffset >= 0 ? $"+{FilterOffset}" : $"{FilterOffset}";
        var line = $"    {FilterIndex,2}  {FilterName!.PadRight(16)} [\u2190] {offsetStr,5} [\u2192]";

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

    private string FormatPropertyRow(int width, ColorMode colorMode)
    {
        var controlStr = IsCycleField
            ? $"  [{PropertyValue}]"
            : $"  [\u2190] {PropertyValue} [\u2192]";

        var labelWidth = Math.Max(18, width / 2);
        var paddedLabel = PropertyLabel!.Length > labelWidth ? PropertyLabel[..(labelWidth - 1)] + "." : PropertyLabel;
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

    private string FormatSettingRow(DeviceSettingDescriptor setting, int width, ColorMode colorMode)
    {
        var label = setting.Label;
        var value = setting.FormatValue(DeviceUri!);
        var controlStr = setting.Kind switch
        {
            DeviceSettingKind.BoolToggle => $"  [{value}]",
            DeviceSettingKind.EnumCycle => $"  [{value}]",
            DeviceSettingKind.StringEditor => setting.Mask && value.Length > 0
                ? $"  [{new string('*', Math.Min(value.Length, 8))}{value[Math.Max(0, value.Length - 4)..]}]"
                : $"  [{(value.Length > 0 ? value : setting.Placeholder ?? "(empty)")}]",
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
