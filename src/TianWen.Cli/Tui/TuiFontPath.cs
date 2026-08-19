using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui;

/// <summary>
/// Shared font path resolution for TUI tabs that render pixel content via Sixel.
/// </summary>
/// <remarks>
/// Goes through <see cref="BundledFonts"/> rather than straight to DIR.Lib's <c>FontResolver</c>, so all
/// three hosts answer "which face do we draw with" the same way. It used to resolve the platform default
/// directly, which is the same shape as the bug that left the FITS viewer's plate-solve tick blank: the
/// Windows monospace default is Consolas, which carries no check mark, so a Sixel-rendered label loses any
/// symbol the platform face happens to lack.
/// <para>Behaviour-neutral as things stand -- this project bundles no font, so the probe falls through to
/// the platform face exactly as before. The point is that it stops being a separate decision: bundle a
/// face with the CLI later and the TUI picks it up with no change here.</para>
/// </remarks>
internal static class TuiFontPath
{
    public static string Resolve() => BundledFonts.Resolve().Text;
}
