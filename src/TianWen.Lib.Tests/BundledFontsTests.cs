using System.IO;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The one font-role resolve both UI hosts share.
    /// </summary>
    /// <remarks>
    /// Both the GUI chrome and the FITS viewer used to resolve the roles themselves, in the same order,
    /// and only the chrome went on to build the coverage chain -- so the viewer could not ask whether a
    /// mark was drawable, and the two hosts could disagree about which marks existed. These pin the two
    /// properties that make one shared resolve viable.
    /// </remarks>
    public class BundledFontsTests
    {
        [Fact]
        public void EveryResolvedPathEitherExistsOrIsEmpty()
        {
            var fonts = BundledFonts.Resolve();

            // Empty is a normal answer for either role: a host may bundle nothing and sit on a platform
            // that ships nothing. What must never happen is a path that does not exist, because the text
            // helpers no-op on an empty path but a bad one is a load failure.
            if (fonts.Text.Length > 0)
            {
                File.Exists(fonts.Text).ShouldBeTrue(fonts.Text);
            }

            if (fonts.Emoji.Length > 0)
            {
                File.Exists(fonts.Emoji).ShouldBeTrue(fonts.Emoji);
            }
        }

        /// <summary>
        /// The chain exists exactly when there is a primary face to hang it off.
        /// </summary>
        /// <remarks>
        /// A resolver over no primary face would answer questions about coverage misleadingly, and a host
        /// with a face but no chain is the bug this consolidation fixed: any codepoint the primary lacks
        /// draws NOTHING, which is what left the GUI search box blank for Chinese input.
        /// </remarks>
        [Fact]
        public void TheFallbackChainAccompaniesAPrimaryFace()
        {
            var fonts = BundledFonts.Resolve();

            if (fonts.Text.Length > 0)
            {
                fonts.Fallback.ShouldNotBeNull();
                fonts.Fallback.PrimaryFontPath.ShouldBe(fonts.Text);
            }
            else
            {
                fonts.Fallback.ShouldBeNull();
            }
        }

        /// <summary>
        /// Resolving twice yields the SAME chain instance, not an equal one.
        /// </summary>
        /// <remarks>
        /// This is what makes per-widget resolution affordable, and therefore what makes a single entry
        /// point usable from both hosts. The script half of the probe looks up ~14 font family names, which
        /// means enumerating installed fonts; the viewer is constructed several times over (preview,
        /// guide-cam, planetary), so an uncached resolve would multiply that by the widget count. Reference
        /// equality is the assertion because the record struct would compare equal either way -- only the
        /// shared reference proves nothing re-probed.
        /// </remarks>
        [Fact]
        public void ResolveIsComputedOncePerProcess()
        {
            var first = BundledFonts.Resolve();
            var second = BundledFonts.Resolve();

            if (first.Fallback is null)
            {
                Assert.Skip("No text face on this host, so there is no chain instance to compare.");
            }

            ReferenceEquals(first.Fallback, second.Fallback).ShouldBeTrue();
        }
    }
}
