using System;

namespace TianWen.Lib.Devices.Skywatcher;

/// <summary>
/// The five guide rates a Synta motor board accepts, as the <c>:P</c> index it is sent as.
/// </summary>
/// <remarks>
/// <para>The enum value IS the wire index, so <c>((int)rate)</c> is what <c>:P</c> carries. The set is
/// closed by the firmware, not by us: the hand controller offers exactly these five and there is no
/// encoding for anything between them.</para>
///
/// <para><b>Why this is a type and not an <c>int</c> plus a lookup.</b> RA pulse guiding OFFSETS the
/// sidereal tracking rate rather than replacing it, so a pulse commands <c>(1 +/- f) x sidereal</c> and
/// changes only the step period (<c>:I</c>) while the axis keeps running. <c>:I</c> sets the MAGNITUDE
/// of the rate; the DIRECTION lives in the motion mode (<c>:G</c>), which a live pulse deliberately does
/// not touch. So an East pulse whose combined rate went negative would need the axis to reverse, and
/// sending <c>:I</c> alone would run it at the right speed in the WRONG direction.
/// <see cref="SkywatcherGuideRateEx.EastRateFactor"/> can never be negative because
/// <see cref="SkywatcherGuideRateEx.Fraction"/> is bounded above by 1.0 across the whole enum, which is
/// what makes the live-<c>:I</c> pulse correct rather than merely lucky -- and the bound is a property
/// of a closed set of five members, checkable by enumerating them, instead of a comment about an
/// <c>int</c>.</para>
///
/// <para>GSServer carries the reversing case because its rate is an arbitrary <c>double</c> from ASCOM;
/// it fixed the resulting wrong-direction pulse in "Fix RA pulse guiding for GEM mounts" (#89) by
/// stopping the axis, re-issuing <c>:G</c> with a flipped direction bit and restarting. We do not need
/// that, and this type is the reason. <b>Reintroducing an unbounded rate reintroduces the bug</b>, which
/// is why <see cref="SkywatcherGuideRateEx.Nearest"/> is the only way in from a <c>double</c>.</para>
/// </remarks>
internal enum SkywatcherGuideRate
{
    /// <summary>1.0x sidereal. An East pulse cancels tracking exactly; see
    /// <see cref="SkywatcherGuideRateEx.EastPulseHaltsTheAxis"/>.</summary>
    Sidereal1_0 = 0,

    /// <summary>0.75x sidereal.</summary>
    Sidereal0_75 = 1,

    /// <summary>0.5x sidereal. The power-on default, and what the hand controller ships with.</summary>
    Sidereal0_5 = 2,

    /// <summary>0.25x sidereal.</summary>
    Sidereal0_25 = 3,

    /// <summary>0.125x sidereal.</summary>
    Sidereal0_125 = 4,
}

internal static class SkywatcherGuideRateEx
{
    /// <summary>Every member, for tests that need to assert a property holds across the whole set.</summary>
    internal static readonly SkywatcherGuideRate[] All =
    [
        SkywatcherGuideRate.Sidereal1_0,
        SkywatcherGuideRate.Sidereal0_75,
        SkywatcherGuideRate.Sidereal0_5,
        SkywatcherGuideRate.Sidereal0_25,
        SkywatcherGuideRate.Sidereal0_125,
    ];

    extension(SkywatcherGuideRate rate)
    {
        /// <summary>
        /// The rate as a fraction of sidereal, in <c>(0, 1]</c>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not one of the five the firmware
        /// defines. Deliberately a throw rather than a silent default: the previous
        /// <c>_ =&gt; 0.5</c> turned any out-of-range index into a plausible-looking half-sidereal
        /// pulse, which guides at the wrong rate and looks like poor seeing.</exception>
        public double Fraction => rate switch
        {
            SkywatcherGuideRate.Sidereal1_0 => 1.0,
            SkywatcherGuideRate.Sidereal0_75 => 0.75,
            SkywatcherGuideRate.Sidereal0_5 => 0.5,
            SkywatcherGuideRate.Sidereal0_25 => 0.25,
            SkywatcherGuideRate.Sidereal0_125 => 0.125,
            _ => throw new ArgumentOutOfRangeException(nameof(rate), rate, "not a Synta guide-rate index")
        };

        /// <summary>
        /// Multiplier on sidereal for a WEST pulse, <c>1 + Fraction</c>: the axis runs faster than
        /// tracking, in the tracking direction. Range <c>[1.125, 2.0]</c>.
        /// </summary>
        public double WestRateFactor => 1.0 + rate.Fraction;

        /// <summary>
        /// Multiplier on sidereal for an EAST pulse, <c>1 - Fraction</c>: the axis runs SLOWER than
        /// tracking, still in the tracking direction. Range <c>[0, 0.875]</c> -- <b>never negative, which
        /// is the whole point of this type</b> (see the remarks on <see cref="SkywatcherGuideRate"/>).
        /// </summary>
        public double EastRateFactor => 1.0 - rate.Fraction;

        /// <summary>
        /// True for <see cref="SkywatcherGuideRate.Sidereal1_0"/> alone, where an East pulse cancels
        /// tracking exactly and <see cref="EastRateFactor"/> is 0.
        /// </summary>
        /// <remarks>
        /// The motor boards cannot encode a zero step period, so this case commands sidereal/1000 --
        /// the axis looks stopped without the decel/accel transient a real stop would cost. Named rather
        /// than expressed as a <c>Math.Max(factor, 1e-3)</c> clamp at the call site, because a clamp
        /// reads as defence against negatives and would go on silently "working" if the bound above
        /// were ever lifted, which is exactly the failure this type exists to prevent.
        /// </remarks>
        public bool EastPulseHaltsTheAxis => rate.Fraction >= 1.0;

        /// <summary>The <c>:P</c> payload for this rate.</summary>
        public string WireIndex => ((int)rate).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    extension(SkywatcherGuideRate)
    {
        /// <summary>
        /// The firmware rate nearest <paramref name="fractionOfSidereal"/>, snapping at the midpoints.
        /// The only way in from a caller-supplied rate.
        /// </summary>
        /// <remarks>
        /// ASCOM lets a client set <c>GuideRateRightAscension</c> to anything, including rates above
        /// sidereal that no Synta board can encode. Snapping is therefore mandatory, and the caller is
        /// entitled to know it happened: <see cref="WasSnapped"/> answers that so the driver can log it
        /// rather than quietly guiding at a rate nobody asked for.
        /// </remarks>
        public static SkywatcherGuideRate Nearest(double fractionOfSidereal) => fractionOfSidereal switch
        {
            >= 0.875 => SkywatcherGuideRate.Sidereal1_0,
            >= 0.625 => SkywatcherGuideRate.Sidereal0_75,
            >= 0.375 => SkywatcherGuideRate.Sidereal0_5,
            >= 0.1875 => SkywatcherGuideRate.Sidereal0_25,
            _ => SkywatcherGuideRate.Sidereal0_125
        };

        /// <summary>
        /// Whether <see cref="Nearest"/> had to move <paramref name="fractionOfSidereal"/> by more than
        /// a thousandth of sidereal to reach <paramref name="snapped"/> -- i.e. whether the client asked
        /// for something the mount cannot do.
        /// </summary>
        public static bool WasSnapped(double fractionOfSidereal, SkywatcherGuideRate snapped)
            => Math.Abs(fractionOfSidereal - snapped.Fraction) > 0.001;
    }
}
