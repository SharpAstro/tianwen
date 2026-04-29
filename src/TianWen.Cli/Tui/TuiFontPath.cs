using DIR.Lib;

namespace TianWen.Cli.Tui;

/// <summary>
/// Shared font path resolution for TUI tabs that render pixel content via Sixel.
/// Delegates to <see cref="FontResolver"/> in DIR.Lib.
/// </summary>
internal static class TuiFontPath
{
    public static string Resolve() => FontResolver.ResolveSystemFont();
}
