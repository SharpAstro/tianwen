using System.IO;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Resolves system font paths for rendering. Shared across GPU and TUI.
    /// </summary>
    public static class FontResolver
    {
        private static readonly string[] WindowsCandidates =
            [@"C:\Windows\Fonts\consola.ttf", @"C:\Windows\Fonts\cour.ttf"];

        private static readonly string[] MacOSCandidates =
            ["/System/Library/Fonts/Menlo.ttc", "/System/Library/Fonts/Monaco.dfont"];

        private static readonly string[] LinuxCandidates =
            ["/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", "/usr/share/fonts/TTF/DejaVuSansMono.ttf"];

        /// <summary>
        /// Resolves a system monospace font path. Returns empty string if none found.
        /// </summary>
        public static string ResolveSystemFont()
        {
            var candidates = System.OperatingSystem.IsWindows() ? WindowsCandidates
                : System.OperatingSystem.IsMacOS() ? MacOSCandidates
                : LinuxCandidates;

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
}
