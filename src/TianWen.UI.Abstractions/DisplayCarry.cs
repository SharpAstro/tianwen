using System;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The properties of a frame that decide whether two frames can be shown the SAME way -- geometry,
    /// plane count, container depth, CFA, and the filter where both frames name one.
    /// </summary>
    /// <remarks>
    /// Deliberately not the whole <see cref="ImageMeta"/>: exposure, gain and temperature all differ
    /// between frames a blink is FOR, and the frame's own pixel statistics differ by definition (that
    /// difference is what the carry exists to hold still). What is listed here is what would make one
    /// display mapping meaningless on the other frame.
    /// </remarks>
    public readonly record struct FrameShape(
        int Width,
        int Height,
        int ChannelCount,
        BitDepth BitDepth,
        SensorType SensorType,
        string FilterKey)
    {
        public static FrameShape Of(Image image) => new FrameShape(
            image.Width,
            image.Height,
            image.ChannelCount,
            image.BitDepth,
            image.ImageMeta.SensorType,
            FilterKeyOf(image.ImageMeta));

        /// <summary>
        /// Whether a display mapping solved for <c>this</c> frame is meaningful on <paramref name="other"/>.
        /// </summary>
        /// <remarks>
        /// Not plain record equality, and not symmetric-transitive either, because of the filter: a frame
        /// that names no filter is comparable to one that does. A folder where only some frames carry a
        /// FILTER card is the common case (a mono rig writes it, an OSC often does not), and refusing the
        /// carry there would disable the feature on exactly the archives it was asked for. Every
        /// comparison is against ONE anchor, so the missing transitivity never has to hold.
        /// </remarks>
        public bool IsComparableTo(FrameShape other)
            => Width == other.Width
            && Height == other.Height
            && ChannelCount == other.ChannelCount
            && BitDepth == other.BitDepth
            && SensorType == other.SensorType
            && FiltersAgree(FilterKey, other.FilterKey);

        private static bool FiltersAgree(string a, string b)
            => a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Filter is a struct on ImageMeta, so a frame whose header carried no FILTER card leaves it at
        // default -- whose Name is null, which IdentityKey would hand straight back.
        private static string FilterKeyOf(in ImageMeta meta)
            => meta.Filter.Name is null ? string.Empty : meta.Filter.IdentityKey;
    }

    /// <summary>
    /// Decides which frame's display statistics a document is rendered with while the user steps
    /// through a folder (P19): the frame the run STARTED on, for as long as the frames match it.
    /// </summary>
    /// <remarks>
    /// <para>Without this each frame solves its own auto-stretch from its own median and MAD, so a
    /// sequence of subs of one field flickers in brightness and colour -- the difference between frames
    /// is exactly what a blink is looking for, and re-solving per frame is what hides it. Holding one
    /// mapping is also the cheap half of "they load faster": a follower inherits the anchor's colour
    /// calibration, so the SPCC fit runs once for the run instead of once per file.</para>
    /// <para>The anchor is a DOCUMENT rather than a snapshot of its numbers on purpose. Its calibration
    /// arrives seconds after the load and star detection refines its background later still, so a
    /// snapshot taken at adoption would be stale in two ways at once -- and the anchor, rendering from
    /// its own live values, would then look different from the frames following it. Reading through the
    /// anchor means there is one set of numbers by construction.</para>
    /// </remarks>
    public static class DisplayCarry
    {
        /// <summary>Whether two loaded documents can share one display mapping.</summary>
        public static bool AreComparable(AstroImageDocument a, AstroImageDocument b)
            => FrameShape.Of(a.UnstretchedImage).IsComparableTo(FrameShape.Of(b.UnstretchedImage));

        /// <summary>
        /// Points <paramref name="document"/> at the anchor it should display with, and returns the
        /// anchor that stands afterwards -- <paramref name="document"/> itself when it starts a new run.
        /// </summary>
        /// <param name="carry">The user's toggle. When off, nothing is anchored and every frame solves
        /// its own mapping, which is the behaviour before P19.</param>
        /// <remarks>
        /// Idempotent, so it can run from the per-frame reconcile rather than from a load-completion
        /// path: a document revisited from the cache is re-pointed by the same rule that pointed it the
        /// first time, and switching the toggle off clears what it set.
        /// </remarks>
        public static AstroImageDocument? Apply(AstroImageDocument document, AstroImageDocument? anchor, bool carry)
        {
            if (!carry)
            {
                document.DisplayAnchor = null;
                return null;
            }

            if (anchor is null || ReferenceEquals(anchor, document) || !AreComparable(anchor, document))
            {
                // A frame the anchor cannot describe starts a run of its own rather than being forced
                // through a mapping solved for a different sensor, depth or field.
                document.DisplayAnchor = null;
                return document;
            }

            document.DisplayAnchor = anchor;
            return anchor;
        }
    }
}
