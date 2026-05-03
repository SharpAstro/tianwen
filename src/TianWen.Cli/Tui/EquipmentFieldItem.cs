using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui;

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

    /// <summary>URI of the currently-assigned device (null for unassigned slots).
    /// Used by the connect/disconnect toggle — distinct from <see cref="DeviceUri"/>
    /// which drives device-setting rows.</summary>
    public Uri? SlotDeviceUri { get; init; }

    /// <summary>Whether the assigned device is currently connected via the hub.
    /// Meaningless when <see cref="SlotDeviceUri"/> is null.</summary>
    public bool IsConnected { get; init; }

    /// <summary>Whether a connect/disconnect transition is in flight — shown as
    /// "..." on the target segment so the user gets visible feedback.</summary>
    public bool IsPending { get; init; }

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

    public string FormatRow(int width, ColorMode colorMode) => FormatRow(width, colorMode, isSelected: false);

    public string FormatRow(int width, ColorMode colorMode, bool isSelected)
    {
        // Section header
        if (SectionName is not null)
        {
            var header = $"\u2500\u2500 {SectionName} \u2500\u2500";
            if (IsOtaHeader)
            {
                // OTA headers show only [X] (delete THIS OTA) -- global Add is already
                // surfaced by the "+ Add OTA" action row at the bottom and the `A` key
                // hint in the status bar, so repeating [A]dd per-OTA is just clutter.
                var actions = "  [X]";
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
            if (isSelected)
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
            return FormatSlotRow(width, colorMode, isSelected);
        }

        // Filter row
        if (FilterIndex > 0 && FilterName is not null)
        {
            return FormatFilterRow(width, colorMode, isSelected);
        }

        // Property stepper row
        if (PropertyLabel is not null)
        {
            return FormatPropertyRow(width, colorMode, isSelected);
        }

        // Device setting row (original)
        if (Setting is { } setting && DeviceUri is not null)
        {
            return FormatSettingRow(setting, width, colorMode, isSelected);
        }

        return "".PadRight(width);
    }

    private string FormatSlotRow(int width, ColorMode colorMode, bool isSelected)
    {
        // Layout: "  Label[padded]  DeviceName  [On|Off]  [>]"
        // The On/Off strip only appears for assigned slots — unassigned rows just
        // show the assign affordance. Segments are rendered with different SGR
        // attributes so the "active" side (current connection state) stands out.
        var labelWidth = Math.Max(14, width / 3);
        var paddedLabel = SlotLabel!.Length > labelWidth ? SlotLabel[..(labelWidth - 1)] + "." : SlotLabel;
        var name = SlotDeviceName ?? "(none)";

        // Reserve space for the right-hand action strip so names never collide with
        // it AND the trailing [>] lines up across rows regardless of whether the
        // toggle strip is rendered. Breakdown:
        //   " [On|Off] " -> 10 visible chars (always reserved; filled with spaces
        //                   when the slot has no device assigned)
        //   " [>]"        -> 4 visible chars
        // Total: 14. Without this unified reserve the `[>]` column shifted by ~8
        // chars between toggle rows and non-toggle rows.
        const int TogglePadWidth = 10;
        const int RightReserve = TogglePadWidth + 4;
        var nameWidth = Math.Max(4, width - 2 - labelWidth - 1 - RightReserve);
        var paddedName = name.Length > nameWidth ? name[..(nameWidth - 1)] + "." : name.PadRight(nameWidth);

        // The outer row style is what StyleSegment must re-apply after each nested
        // segment -- otherwise its closing `\e[0m` wipes the blue selection background
        // mid-line and leaves a visible gap until the row ends. Pass the outer style
        // down so each segment restores both fg (BrightWhite / White) and bg (Blue /
        // default) on exit.
        var outerStyle = isSelected
            ? new VtStyle(SgrColor.BrightWhite, SgrColor.Blue)
            : new VtStyle(SgrColor.White, SgrColor.Black);

        string onSeg, offSeg;
        if (!IsSlotActive)
        {
            onSeg = string.Empty;
            offSeg = string.Empty;
        }
        else if (IsPending)
        {
            // Pending: target segment shows "…", "you are here" segment stays inert.
            onSeg = IsConnected ? StyleSegment("On", colorMode, SgrColor.BrightGreen, outerStyle) : StyleSegment("...", colorMode, SgrColor.Yellow, outerStyle);
            offSeg = IsConnected ? StyleSegment("...", colorMode, SgrColor.Yellow, outerStyle) : StyleSegment("Off", colorMode, SgrColor.BrightRed, outerStyle);
        }
        else
        {
            onSeg = IsConnected ? StyleSegment("On", colorMode, SgrColor.BrightGreen, outerStyle) : StyleSegment("On", colorMode, SgrColor.White, outerStyle);
            offSeg = IsConnected ? StyleSegment("Off", colorMode, SgrColor.White, outerStyle) : StyleSegment("Off", colorMode, SgrColor.BrightRed, outerStyle);
        }

        // When there's no device assigned, emit TogglePadWidth spaces so the [>]
        // lands in the same column as on active rows.
        var toggleStrip = IsSlotActive
            ? $" [{onSeg}|{offSeg}] "
            : new string(' ', TogglePadWidth);
        var line = $"  {paddedLabel.PadRight(labelWidth)} {paddedName}{toggleStrip} [>]";

        // Pad the line to produce exactly `width` visible characters by adding
        // VisibleOverhead(line) extra chars to the pad target (compensating for the
        // SGR escapes embedded in StyleSegment). The outer-style wrapping below adds
        // further invisible bytes but does NOT change the visible width, so it must
        // happen AFTER padding -- otherwise the visible row ends short by the outer
        // style's byte count and stale content from the previous frame peeks through
        // at the far right of the viewport (the "ghost [>]" bug).
        var padded = line.PadRight(width + VisibleOverhead(line));
        if (isSelected)
        {
            return $"{outerStyle.Apply(colorMode)}{padded}{VtStyle.Reset}";
        }

        return padded;
    }

    /// <summary>
    /// Wraps text in an SGR style that overrides the foreground while keeping the
    /// caller's outer background, then restores the full outer style on exit. The
    /// caller inlines the return value so the surrounding line still reads naturally;
    /// counting visible characters in a styled line requires <see cref="VisibleOverhead"/>.
    /// </summary>
    private static string StyleSegment(string text, ColorMode colorMode, SgrColor fg, VtStyle restore)
        => $"{new VtStyle(fg.ToRgba(), restore.Background).Apply(colorMode)}{text}{restore.Apply(colorMode)}";

    /// <summary>
    /// Returns the number of "invisible" characters (SGR escape bytes) in <paramref name="line"/>
    /// so PadRight can be compensated to target a given visible width.
    /// </summary>
    private static int VisibleOverhead(string line)
    {
        var overhead = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\x1b')
            {
                // Skip to end of SGR sequence (terminated by 'm')
                var j = i + 1;
                while (j < line.Length && line[j] != 'm') j++;
                overhead += j - i + 1;
                i = j;
            }
        }
        return overhead;
    }

    private string FormatFilterRow(int width, ColorMode colorMode, bool isSelected)
    {
        var offsetStr = FilterOffset >= 0 ? $"+{FilterOffset}" : $"{FilterOffset}";
        var line = $"    {FilterIndex,2}  {FilterName!.PadRight(16)} [\u2190] {offsetStr,5} [\u2192]";

        if (line.Length > width)
        {
            line = line[..width];
        }

        if (isSelected)
        {
            var style = new VtStyle(SgrColor.BrightWhite, SgrColor.Blue);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }

        return line.PadRight(width);
    }

    private string FormatPropertyRow(int width, ColorMode colorMode, bool isSelected)
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

        if (isSelected)
        {
            var style = new VtStyle(SgrColor.BrightWhite, SgrColor.Blue);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }

        return line.PadRight(width);
    }

    private string FormatSettingRow(DeviceSettingDescriptor setting, int width, ColorMode colorMode, bool isSelected)
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

        if (isSelected)
        {
            var style = new VtStyle(SgrColor.BrightWhite, SgrColor.Blue);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }

        return line.PadRight(width);
    }
}
