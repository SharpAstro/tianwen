using Console.Lib;

namespace TianWen.Lib.CLI.View;

/// <summary>
/// A file name entry for the interactive viewer's scrollable file list.
/// </summary>
internal sealed class FileListItem(string fileName, bool isSelected) : IRowFormatter
{
    public string FileName { get; } = fileName;
    public bool IsSelected { get; set; } = isSelected;

    public string FormatRow(int width, ColorMode colorMode)
    {
        var marker = IsSelected ? "> " : "  ";
        var maxNameLen = width - marker.Length;
        var display = FileName.Length > maxNameLen
            ? FileName[..(maxNameLen - 2)] + ".."
            : FileName;

        var line = $"{marker}{display}";

        if (IsSelected)
        {
            var style = new VtStyle(SgrColor.BrightWhite, SgrColor.Blue);
            return $"{style.Apply(colorMode)}{line.PadRight(width)}{VtStyle.Reset}";
        }

        return line.PadRight(width);
    }
}
