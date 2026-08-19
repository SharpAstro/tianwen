using System;
using System.Collections.Generic;
using System.IO;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Resolves a colour-emoji face, for the surfaces that draw emoji marks (the GUI's tab icons and
    /// Home board, the FITS viewer's toolbar).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists at all:</b> DIR.Lib's <c>FontResolver</c> is the single source of truth
    /// for font-path resolution and has entry points for the default face, installed faces and script
    /// fallbacks -- but never grew an emoji one. So the order was hand-rolled inline in
    /// <c>VkGuiRenderer.ResolveFontPath</c>, and then hand-rolled a SECOND time in
    /// <c>ImageRendererBase</c> when the viewer needed the same thing. Two copies of a probe list is how
    /// one host silently supports an emoji the other cannot draw.</para>
    /// <para><b>This belongs in DIR.Lib beside the rest of FontResolver</b> and should move there on its
    /// next release; it lives here only because both TianWen consumers reference this project, so the
    /// duplication can be removed today without waiting on a sibling publish.</para>
    /// <para>The inline versions were Windows-only, which is the other thing one path fixes: a Linux or
    /// macOS host had no emoji face at all, so every emoji mark silently drew nothing.</para>
    /// </remarks>
    public static class EmojiFonts
    {
        // Colour-emoji faces by platform. Windows ships Segoe UI Emoji; macOS Apple Color Emoji; Linux
        // distros vary, so both common Noto paths are probed.
        private static readonly string[] WindowsCandidates =
            [@"C:\Windows\Fonts\seguiemj.ttf"];

        private static readonly string[] MacOSCandidates =
            ["/System/Library/Fonts/Apple Color Emoji.ttc"];

        private static readonly string[] LinuxCandidates =
            ["/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf",
             "/usr/share/fonts/noto/NotoColorEmoji.ttf",
             "/usr/share/fonts/truetype/noto/NotoColorEmoji-Regular.ttf"];

        /// <summary>The app-bundled face, probed before anything installed.</summary>
        /// <remarks>
        /// A bundled font is preferred because it is the only one whose COVERAGE and metrics are known:
        /// a system face that lacks a codepoint draws nothing, and "nothing" is indistinguishable from a
        /// broken control. Not every host project bundles it, so this is a probe and not a guarantee.
        /// </remarks>
        public const string BundledRelativePath = "Fonts/Noto-COLRv1.ttf";

        /// <summary>
        /// The first colour-emoji face that exists, or <c>""</c> when the host has none.
        /// </summary>
        /// <param name="extra">
        /// Paths to try before the bundled and system candidates, highest priority first. Mirrors
        /// <c>FontResolver.ResolveSystemScriptFonts(extra)</c>, so a caller with its own asset can state
        /// it without this class knowing about that caller.
        /// </param>
        /// <returns>
        /// An absolute path, or <c>""</c> (never null) -- matching <c>FontResolver.ResolveSystemFont</c>,
        /// whose callers do a length check.
        /// </returns>
        public static string Resolve(IEnumerable<string>? extra = null)
        {
            if (extra is not null)
            {
                foreach (var candidate in extra)
                {
                    if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            var bundled = Path.Combine(AppContext.BaseDirectory,
                BundledRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(bundled))
            {
                return bundled;
            }

            var candidates = OperatingSystem.IsWindows() ? WindowsCandidates
                : OperatingSystem.IsMacOS() ? MacOSCandidates
                : LinuxCandidates;

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }
    }
}
