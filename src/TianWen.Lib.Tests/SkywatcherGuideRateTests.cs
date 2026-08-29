using System;
using Shouldly;
using TianWen.Lib.Devices.Skywatcher;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the bound that makes live-<c>:I</c> RA pulse guiding correct.
/// </summary>
/// <remarks>
/// An RA pulse offsets the sidereal tracking rate rather than replacing it, and changes only the step
/// period (<c>:I</c>) while the axis keeps running. <c>:I</c> sets MAGNITUDE; DIRECTION lives in the
/// motion mode (<c>:G</c>), which a live pulse does not touch. So if an East pulse's combined rate ever
/// went negative the axis would need to reverse, and we would be commanding the right speed in the
/// wrong direction -- which is the bug GSServer fixed in "Fix RA pulse guiding for GEM mounts" (#89) by
/// stopping the axis, re-issuing <c>:G</c> with a flipped direction bit and restarting.
/// <para>We do not need that fix because <c>Fraction &lt;= 1.0</c> holds across the whole enum. That is
/// only a guarantee if it is checked over the whole enum, which is what these tests do -- a property of
/// a closed set, not a comment about an <c>int</c>.</para>
/// </remarks>
public class SkywatcherGuideRateTests
{
    /// <summary>
    /// The wire indices rather than the enum itself: <c>SkywatcherGuideRate</c> is internal to the
    /// driver and an xUnit theory signature has to be public. The index IS the enum value, so nothing
    /// is lost and the cast in each theory is total over the declared set.
    /// </summary>
    public static TheoryData<int> AllRates()
    {
        var data = new TheoryData<int>();
        foreach (var rate in SkywatcherGuideRateEx.All)
        {
            data.Add((int)rate);
        }
        return data;
    }

    [Fact]
    public void AllCoversEveryDeclaredMember()
    {
        // Or the theories below silently stop covering a member somebody added.
        SkywatcherGuideRateEx.All.ShouldBe(Enum.GetValues<SkywatcherGuideRate>(), ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(AllRates))]
    public void FractionIsBoundedToUnitInterval(int wireIndex)
    {
        var rate = (SkywatcherGuideRate)wireIndex;
        rate.Fraction.ShouldBeGreaterThan(0.0);
        rate.Fraction.ShouldBeLessThanOrEqualTo(1.0);
    }

    [Theory]
    [MemberData(nameof(AllRates))]
    public void AnEastPulseNeverReversesTheAxis(int wireIndex)
    {
        var rate = (SkywatcherGuideRate)wireIndex;
        // THE invariant. A negative factor means the axis must turn the other way, which a live :I
        // cannot express.
        rate.EastRateFactor.ShouldBeGreaterThanOrEqualTo(0.0);
        rate.WestRateFactor.ShouldBeGreaterThan(1.0);
    }

    [Theory]
    [MemberData(nameof(AllRates))]
    public void OnlyTheFullSiderealRateHaltsTheAxisOnAnEastPulse(int wireIndex)
    {
        var rate = (SkywatcherGuideRate)wireIndex;
        var halts = rate.EastPulseHaltsTheAxis;
        halts.ShouldBe(rate == SkywatcherGuideRate.Sidereal1_0);

        // The flag and the factor must agree, or the call site picks the sidereal/1000 branch for a
        // rate that actually wanted a real one (or the reverse, commanding an unencodable zero period).
        halts.ShouldBe(rate.EastRateFactor <= 0.0);
    }

    [Theory]
    [MemberData(nameof(AllRates))]
    public void WireIndexRoundTripsToTheEnumValue(int wireIndex)
    {
        var rate = (SkywatcherGuideRate)wireIndex;
        // The :P payload IS the enum value; a mismatch sets the mount's ST-4 port to a different rate
        // than the one we pulse at, which only shows up as guiding that disagrees with itself.
        rate.WireIndex.ShouldBe(((int)rate).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnUndeclaredValueThrowsRatherThanGuessing()
    {
        // The previous lookup answered 0.5x for any out-of-range index, which guides at the wrong rate
        // and presents as poor seeing rather than as a fault.
        Should.Throw<ArgumentOutOfRangeException>(() => ((SkywatcherGuideRate)99).Fraction);
    }

    [Theory]
    [InlineData(1.0, (int)/*Sidereal1_0*/ 0)]
    [InlineData(0.9, (int)/*Sidereal1_0*/ 0)]
    [InlineData(0.75, (int)/*Sidereal0_75*/ 1)]
    [InlineData(0.5, (int)/*Sidereal0_5*/ 2)]
    [InlineData(0.25, (int)/*Sidereal0_25*/ 3)]
    [InlineData(0.125, (int)/*Sidereal0_125*/ 4)]
    [InlineData(0.0, (int)/*Sidereal0_125*/ 4)]
    public void NearestSnapsToTheFirmwaresOwnFiveRates(double fraction, int expectedWireIndex)
        => ((int)SkywatcherGuideRate.Nearest(fraction)).ShouldBe(expectedWireIndex);

    [Theory]
    [InlineData(2.0)]
    [InlineData(1.5)]
    [InlineData(1.1)]
    public void ARateAboveSiderealSnapsDownAndIsNeverAllowedToReverse(double fraction)
    {
        // ASCOM lets a client ask for this; no Synta board can encode it. Snapping is what keeps
        // EastRateFactor non-negative for a rate that, taken literally, would demand a reversal.
        var snapped = SkywatcherGuideRate.Nearest(fraction);

        ((int)snapped).ShouldBe((int)SkywatcherGuideRate.Sidereal1_0);
        snapped.EastRateFactor.ShouldBeGreaterThanOrEqualTo(0.0);
        SkywatcherGuideRate.WasSnapped(fraction, snapped).ShouldBeTrue("the client must be told it did not get what it asked for");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.75)]
    [InlineData(0.5)]
    [InlineData(0.25)]
    [InlineData(0.125)]
    public void AnExactlyEncodableRateIsNotReportedAsSnapped(double fraction)
        => SkywatcherGuideRate.WasSnapped(fraction, SkywatcherGuideRate.Nearest(fraction)).ShouldBeFalse();
}
