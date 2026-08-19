using System;
using System.IO;
using System.Threading;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Resolves the app's font roles: the app-bundled face first, then whatever the platform ships.
    /// </summary>
    /// <remarks>
    /// <para><b>What is left here is POLICY, not probing.</b> Finding a platform's face is
    /// <c>FontResolver</c>'s job -- it owns that for the monospace default, the per-script chain and (as
    /// of DIR.Lib 8.7) the colour-emoji face, which this class used to carry its own platform tables for.
    /// What remains app-specific is the one decision this class makes: prefer the file WE ship over
    /// anything installed, per role, and then build the coverage chain over the result.
    /// </para>
    /// <para><b>Bundled first because a bundled face is the only one whose COVERAGE is known.</b> A
    /// system face that lacks a codepoint draws nothing at all, and nothing is indistinguishable from a
    /// broken control. Measured: the Windows monospace default is Consolas, which carries no check mark,
    /// so the viewer's plate-solve tick rendered as empty space. Falling back to the platform is still
    /// right -- a host that bundles nothing must draw SOMETHING, and losing an occasional symbol beats
    /// losing every label.</para>
    /// <para><b>One entry point on purpose.</b> Both hosts used to resolve the roles themselves, in the
    /// same order, from the same two probes -- and only one of them went on to build the fallback chain,
    /// so the viewer had no coverage chain at all and could not have asked whether a mark was drawable.
    /// A single <see cref="Resolve"/> returning the whole set is what makes the two hosts agree by
    /// construction rather than by both being remembered.</para>
    /// </remarks>
    public static class BundledFonts
    {
        /// <summary>The app-bundled colour-emoji face, probed before anything installed.</summary>
        public const string BundledEmojiRelativePath = "Fonts/Noto-COLRv1.ttf";

        /// <summary>The app-bundled text face, probed before the platform default.</summary>
        public const string BundledTextRelativePath = "Fonts/DejaVuSans.ttf";

        /// <summary>
        /// Every font role this app draws with, plus the coverage-aware chain over them.
        /// </summary>
        /// <param name="Text">
        /// The face for labels, or <c>""</c> when the host bundles none and the platform ships none.
        /// Empty is a normal answer: the text helpers no-op without a face rather than throwing.
        /// </param>
        /// <param name="Emoji">The colour-emoji face, or <c>""</c> when the host has none.</param>
        /// <param name="Fallback">
        /// The chain that answers which face can draw a given rune, or <c>null</c> when there is no
        /// primary face to build it over. Roles are ordered emoji-before-script deliberately: several CJK
        /// faces incidentally carry the odd pictograph, and drawing one out of a multi-megabyte face when
        /// a dedicated colour font is present is both wrong and heavier.
        /// </param>
        public readonly record struct FontSet(string Text, string Emoji, FontFallbackResolver? Fallback);

        // Resolved once per process. Every widget that needs a face would otherwise repeat the whole probe,
        // and the script half of it is not cheap: ResolveSystemScriptFonts looks up ~14 family NAMES, which
        // means enumerating installed fonts. The GUI paid that once because one chrome object resolved for
        // every tab; the viewer is instantiated several times over (preview, guide-cam, planetary), so
        // sharing the answer is what lets it resolve at all rather than being left without a chain.
        // Installed fonts can change while a process runs, and this deliberately does not notice -- both
        // hosts resolve at startup and hold the paths for the process lifetime regardless.
        private static readonly Lazy<FontSet> Resolved =
            new(ResolveUncached, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Resolves all roles together, so no caller can resolve a subset inconsistently.</summary>
        /// <remarks>
        /// <para><b>The chain is not optional decoration.</b> Without one, ANY codepoint the primary face
        /// lacks renders as nothing at all -- which is what made the GUI search box look broken for Chinese
        /// input: the IME committed correctly and the field held the right characters, but DejaVu Sans has
        /// no CJK cmap entry, so there was nothing to draw and the field simply stayed blank.</para>
        /// <para>The per-OS script faces come from <see cref="FontResolver.ResolveSystemScriptFonts"/>, so
        /// this app carries no font-name knowledge of its own -- every DIR.Lib consumer that draws
        /// user-supplied text needs the same list, and each working it out separately would get a
        /// different, quietly incomplete answer. Nothing is bundled for CJK on purpose: a Noto CJK face is
        /// ~17 MB each, a full set is ~68 MB on every one of six AOT publishes, and binary releases here
        /// are already manual specifically to stay inside the 1 GB/month LFS budget. Anyone who can TYPE
        /// Chinese has a Chinese face installed.</para>
        /// </remarks>
        public static FontSet Resolve() => Resolved.Value;

        private static FontSet ResolveUncached()
        {
            var text = ResolveText();
            var emoji = ResolveEmoji();

            // No primary face means no chain to hang fallbacks off, which is not a failure -- it is a host
            // that will draw no text at all, and a resolver over nothing would answer misleadingly.
            var fallback = text.Length == 0
                ? null
                : FontFallbackResolver.FromRoles(
                    text,
                    emojiFontPath: emoji.Length == 0 ? null : emoji,
                    scriptFontPaths: FontResolver.ResolveSystemScriptFonts());

            return new FontSet(text, emoji, fallback);
        }

        private static string ResolveText()
            => Bundled(BundledTextRelativePath) is { Length: > 0 } bundled
                ? bundled
                : FontResolver.ResolveSystemFont();

        private static string ResolveEmoji()
            => Bundled(BundledEmojiRelativePath) is { Length: > 0 } bundled
                ? bundled
                : FontResolver.ResolveEmojiFont();

        /// <summary>
        /// The app's own copy of a face, or <c>""</c> when this host did not ship it.
        /// </summary>
        /// <remarks>
        /// Not every host project bundles every role, so this is a probe and not a guarantee. Shared by
        /// both roles because it was written out once per role, identically, and the roles must not drift
        /// on where "our own file" lives.
        /// </remarks>
        private static string Bundled(string relativePath)
        {
            var path = Path.Combine(AppContext.BaseDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? path : string.Empty;
        }
    }
}
