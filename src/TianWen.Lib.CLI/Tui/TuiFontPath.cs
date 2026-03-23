using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// Shared font path resolution for TUI tabs that render pixel content via Sixel.
/// Delegates to <see cref="FontResolver"/> in TianWen.UI.Abstractions.
/// </summary>
internal static class TuiFontPath
{
    public static string Resolve() => FontResolver.ResolveSystemFont();
}
