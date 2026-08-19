using System;
using System.Collections.Generic;
using System.IO;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Resolves the app's TEXT and colour-EMOJI faces: bundled first, then whatever the platform ships.
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
    /// <para><b>The TEXT half was added for the same reason, one host later.</b> The GUI preferred the
    /// bundled DejaVu Sans and the FITS viewer went straight to the system face, so the viewer's text
    /// coverage was whatever the host happened to install -- measured on Windows that is Consolas, which
    /// has no check mark and no ballot box, so a tick in a label rendered as NOTHING. Same failure shape
    /// as the emoji probe: a difference that is invisible on the machine you develop on.</para>
    /// </remarks>
    public static class BundledFonts
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
        public const string BundledEmojiRelativePath = "Fonts/Noto-COLRv1.ttf";

        /// <summary>The app-bundled text face, probed before the system default.</summary>
        public const string BundledTextRelativePath = "Fonts/DejaVuSans.ttf";

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
        public static string ResolveEmoji(IEnumerable<string>? extra = null)
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
                BundledEmojiRelativePath.Replace('/', Path.DirectorySeparatorChar));
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

        /// <summary>
        /// The text face to draw labels with: the bundled one when the host ships it, else the system
        /// default, else <c>""</c>.
        /// </summary>
        /// <remarks>
        /// Bundled first because it is the only face whose COVERAGE is known. The system default varies
        /// per host, and a codepoint it lacks draws nothing at all -- which is how a tick in the FITS
        /// viewer's plate-solve label came out blank on a box whose monospace default is Consolas.
        /// Falling back is still right: a host that bundles nothing must draw SOMETHING, and losing an
        /// occasional symbol beats losing every label.
        /// </remarks>
        public static string ResolveText(IEnumerable<string>? extra = null)
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
                BundledTextRelativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(bundled) ? bundled : FontResolver.ResolveSystemFont();
        }
    }
}
