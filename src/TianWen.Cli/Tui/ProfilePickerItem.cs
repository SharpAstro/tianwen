using Console.Lib;
using TianWen.Lib.Devices;

namespace TianWen.Cli.Tui;

/// <summary>
/// A row in the profile picker list. Shows profile name with active indicator.
/// </summary>
internal sealed class ProfilePickerItem : IRowFormatter
{
    /// <summary>The profile.</summary>
    public required Profile Profile { get; init; }

    /// <summary>Whether this profile is the currently active one.</summary>
    public bool IsActive { get; init; }

    /// <summary>Whether this row is highlighted (keyboard selection).</summary>
    public bool IsSelected { get; init; }

    public string FormatRow(int width, ColorMode colorMode)
    {
        var marker = IsActive ? "\u25b6 " : "  ";
        var name = Profile.DisplayName;
        var line = $" {marker}{name}";

        if (line.Length > width)
        {
            line = line[..(width - 1)] + "\u2026";
        }

        if (IsSelected)
        {
            var style = new VtStyle(SgrColor.BrightWhite, SgrColor.Blue);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }

        if (IsActive)
        {
            var style = new VtStyle(SgrColor.BrightGreen, SgrColor.Black);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }

        return line.PadRight(width);
    }
}
