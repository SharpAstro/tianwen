namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// Shared font path resolution for TUI tabs that render pixel content via Sixel.
/// </summary>
internal static class TuiFontPath
{
    public static string Resolve()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Windows\Fonts\consola.ttf", @"C:\Windows\Fonts\cour.ttf"]
            : OperatingSystem.IsMacOS()
                ? ["/System/Library/Fonts/Menlo.ttc", "/System/Library/Fonts/Monaco.dfont"]
                : ["/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", "/usr/share/fonts/TTF/DejaVuSansMono.ttf"];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return "";
    }
}
