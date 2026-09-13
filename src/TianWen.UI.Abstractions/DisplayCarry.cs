using System;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The properties of a frame that decide whether two frames can be shown the SAME way -- geometry,
    /// plane count, container depth, CFA, and the filter and OBJECT where both frames name one.
    /// </summary>
    /// <remarks>
    /// Deliberately not the whole <see cref="ImageMeta"/>: exposure, gain and temperature all differ
    /// between frames a blink is FOR, and the frame's own pixel statistics differ by definition (that
    /// difference is what the carry exists to hold still). What is listed here is what would make one
    /// display mapping meaningless on the other frame.
    /// <para>
    /// <b>The object name is part of that, and geometry alone is not enough.</b> Two masters off the
    /// same rig have the same width, height, planes, depth and CFA whatever they are pointed at, so
    /// shape called them comparable and a step from one target to another was shown with the other
    /// one's stretch. A blink is for the SAME scene; two different objects are not one, however alike
    /// their sensors.
    /// </para>
    /// </remarks>
    public readonly record struct FrameShape(
        int Width,
        int Height,
        int ChannelCount,
        BitDepth BitDepth,
        SensorType SensorType,
        string FilterKey,
        string ObjectKey)
    {
        public static FrameShape Of(Image image) => new FrameShape(
            image.Width,
            image.Height,
            image.ChannelCount,
            image.BitDepth,
            image.ImageMeta.SensorType,
            FilterKeyOf(image.ImageMeta),
            ObjectKeyOf(image.ImageMeta));

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
            && NamesAgree(FilterKey, other.FilterKey)
            && NamesAgree(ObjectKey, other.ObjectKey);

        /// <summary>
        /// Two optional names agree when they match, or when either frame does not give one. The
        /// permissive half is why this is not record equality: a folder where only some frames carry
        /// the card is the common case, not a corner, and refusing there would disable the feature on
        /// exactly the archives it was asked for. Every comparison is against ONE anchor, so the
        /// missing transitivity never has to hold.
        /// </summary>
        private static bool NamesAgree(string a, string b)
            => a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Filter is a struct on ImageMeta, so a frame whose header carried no FILTER card leaves it at
        // default -- whose Name is null, which IdentityKey would hand straight back.
        private static string FilterKeyOf(in ImageMeta meta)
            => meta.Filter.Name is null ? string.Empty : meta.Filter.IdentityKey;

        // Compared verbatim apart from case and surrounding space, so "M 8" and "M8" read as different
        // targets. That is the SAFE direction: the cost of refusing is one frame solved from its own
        // statistics, and the cost of accepting wrongly is a frame shown with another target's stretch.
        private static string ObjectKeyOf(in ImageMeta meta)
            => meta.ObjectName is { Length: > 0 } name ? name.Trim() : string.Empty;
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
        /// Gives a colour calibration just solved for <paramref name="document"/> to the run it belongs
        /// to, so every comparable frame reads the same triple. A no-op for a frame that anchors its own
        /// run, or one with no calibration to give.
        /// </summary>
        /// <remarks>
        /// <para><b>The read direction was always here and the write direction was not.</b> A follower
        /// inherits the anchor's calibration through <c>Basis</c>, which is what the remarks above mean
        /// by the fit running once for the run -- but a fit solved while looking at a FOLLOWER landed on
        /// that follower alone, where nothing else could see it. So calibrating a sub and blinking to
        /// the next showed the next one uncalibrated, which is the opposite of what a blink is for.</para>
        /// <para>It cannot leak across targets: an anchor only exists between frames
        /// <see cref="AreComparable"/> accepted, so a different object has no anchor in common to
        /// publish to and starts uncalibrated.</para>
        /// <para><b>Sharing one fit across a set is a COST decision, not a claim that the frames have
        /// identical colour.</b> A photometric calibration is per frame in principle. It is also a
        /// catalogue init plus a match against a few thousand stars -- seconds each, where a blink is
        /// meant to be instant -- so paying it once per run and holding the set to that answer is the
        /// trade being made. It is a safe one because the frames sharing an anchor are the same target
        /// through the same filter on the same sensor, where the per-frame answers differ by very
        /// little; it would NOT be safe across anything <see cref="AreComparable"/> rejects, which is
        /// why the two rules are the same rule. Anyone tempted to make this per frame again should
        /// know they are buying exactness with the responsiveness the carry exists to provide.</para>
        /// </remarks>
        public static void PublishCalibration(AstroImageDocument document)
        {
            if (document.DisplayAnchor is { } anchor && document.ColorCalibration is { } wb)
            {
                anchor.InheritColorCalibration(wb, document.ColorCalibrationSummary,
                    document.IsNarrowbandColorCalibration);
            }
        }

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
