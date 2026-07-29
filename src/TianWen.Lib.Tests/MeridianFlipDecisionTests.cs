using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pure-function tests for <see cref="MeridianFlipDecision"/>. No devices, no time, no async —
/// the helper is supposed to make every flip / obstruction-zone decision a transparent input -> output.
/// </summary>
public class MeridianFlipDecisionTests
{
    // Default config matches SessionConfiguration defaults: no obstruction zone (0 min before),
    // earliest flip 5 min after meridian, latest 10 min after.
    private static SessionConfiguration MakeConfig(
        double obstructionMinutesBefore = 0,
        double earliestMinutesAfter = 5,
        double latestMinutesAfter = 10)
    {
        return new SessionConfiguration(
            SetpointCCDTemperature: new SetpointTemp(0, SetpointTempKind.CCD),
            CooldownRampInterval: System.TimeSpan.FromMinutes(1),
            WarmupRampInterval: System.TimeSpan.FromMinutes(1),
            MinHeightAboveHorizon: 30,
            DitherPixel: 5,
            SettlePixel: 1,
            DitherEveryNthFrame: 3,
            SettleTime: System.TimeSpan.FromSeconds(10),
            GuidingTries: 3,
            MeridianFlipObstructionZoneMinutesBefore: obstructionMinutesBefore,
            MeridianFlipEarliestMinutesAfter: earliestMinutesAfter,
            MeridianFlipLatestMinutesAfter: latestMinutesAfter
        );
    }

    [Theory]
    // Default zone (zero): everything east of meridian is healthy until HA crosses 0.
    [InlineData(-1.0, 0, 5, 10, HourAngleZone.EastOfMeridian)]
    [InlineData(-0.0001, 0, 5, 10, HourAngleZone.EastOfMeridian)]  // just east, zone disabled
    [InlineData(0.05, 0, 5, 10, HourAngleZone.InObstructionZone)]  // 3 min past, before earliest
    [InlineData(0.0834, 0, 5, 10, HourAngleZone.InFlipWindow)]      // 5.004 min, just inside
    [InlineData(0.15, 0, 5, 10, HourAngleZone.InFlipWindow)]        // 9 min
    [InlineData(0.20, 0, 5, 10, HourAngleZone.PastFlipWindow)]      // 12 min
    // 5-min obstruction zone: everything east of -5min is healthy.
    [InlineData(-0.10, 5, 5, 10, HourAngleZone.EastOfMeridian)]    // 6 min east
    [InlineData(-0.0833, 5, 5, 10, HourAngleZone.InObstructionZone)] // exactly -5 min
    [InlineData(-0.05, 5, 5, 10, HourAngleZone.InObstructionZone)]  // 3 min east
    [InlineData(0.05, 5, 5, 10, HourAngleZone.InObstructionZone)]   // 3 min west
    [InlineData(0.10, 5, 5, 10, HourAngleZone.InFlipWindow)]        // 6 min west
    public void GivenHourAngleAndConfigWhenClassifyThenZoneIsCorrect(
        double hourAngleHours, double obsMin, double earliestMin, double latestMin, HourAngleZone expected)
    {
        var config = MakeConfig(obsMin, earliestMin, latestMin);

        var zone = MeridianFlipDecision.ClassifyHourAngle(hourAngleHours, config);

        zone.ShouldBe(expected);
    }

    [Fact]
    public void GivenPierSideChangedWhenDecideThenAlreadyFlippedRegardlessOfHA()
    {
        // Even if HA still says east-of-meridian (firmware just flipped without us), we observe
        // the pier-side change and skip the re-slew.
        var config = MakeConfig();

        var action = MeridianFlipDecision.DecideFlipAction(hourAngleHours: -0.5, pierSideChanged: true, alreadyOnCorrectSide: false, hasFlipped: false, config);

        action.ShouldBe(FlipAction.AlreadyFlipped);
    }

    [Fact]
    public void GivenEastOfMeridianWhenDecideThenContinue()
    {
        var config = MakeConfig();

        var action = MeridianFlipDecision.DecideFlipAction(hourAngleHours: -1.0, pierSideChanged: false, alreadyOnCorrectSide: false, hasFlipped: false, config);

        action.ShouldBe(FlipAction.Continue);
    }

    [Fact]
    public void GivenInObstructionZoneWhenDecideThenWaitForObstructionClear()
    {
        var config = MakeConfig(obstructionMinutesBefore: 5);

        // 3 min east of meridian, inside the 5-min obstruction zone
        var action = MeridianFlipDecision.DecideFlipAction(hourAngleHours: -0.05, pierSideChanged: false, alreadyOnCorrectSide: false, hasFlipped: false, config);

        action.ShouldBe(FlipAction.WaitForObstructionClear);
    }

    [Fact]
    public void GivenInFlipWindowWhenDecideThenCommandFlip()
    {
        var config = MakeConfig();

        // 7 min past meridian — inside [5, 10] window
        var action = MeridianFlipDecision.DecideFlipAction(hourAngleHours: 7.0 / 60.0, pierSideChanged: false, alreadyOnCorrectSide: false, hasFlipped: false, config);

        action.ShouldBe(FlipAction.CommandFlip);
    }

    [Fact]
    public void GivenPastFlipWindowWhenDecideThenStillCommandFlip()
    {
        // Even when the latest acceptable flip time has passed, we still try to flip — better
        // late than stuck on the wrong side. The mount will fail the slew if it's actually
        // past its tracking limit.
        var config = MakeConfig();

        var action = MeridianFlipDecision.DecideFlipAction(hourAngleHours: 0.5, pierSideChanged: false, alreadyOnCorrectSide: false, hasFlipped: false, config);

        action.ShouldBe(FlipAction.CommandFlip);
    }

    [Fact]
    public void GivenPastMeridianButAlreadyOnCorrectSideWhenDecideThenContinue()
    {
        // The regression that caused the infinite flip loop: we joined an AcrossMeridian observation
        // that had already crossed (target +30 min west), and the mount was slewed straight onto its
        // destination pier side. HA is in the flip window, but no flip is needed — keep imaging instead
        // of commanding a no-op flip every tick.
        var config = MakeConfig();

        var action = MeridianFlipDecision.DecideFlipAction(
            hourAngleHours: 0.5, pierSideChanged: false, alreadyOnCorrectSide: true, hasFlipped: false, config);

        action.ShouldBe(FlipAction.Continue);
    }

    [Fact]
    public void GivenAlreadyFlippedWhenStillInFlipWindowThenContinue()
    {
        // Backstop: once we have flipped for this target, never flip again even though HA stays past
        // the meridian (and the mount's reported pier side may not change, e.g. SkyWatcher).
        var config = MakeConfig();

        var action = MeridianFlipDecision.DecideFlipAction(
            hourAngleHours: 7.0 / 60.0, pierSideChanged: false, alreadyOnCorrectSide: false, hasFlipped: true, config);

        action.ShouldBe(FlipAction.Continue);
    }

    [Fact]
    public void GivenAlreadyFlippedTakesPrecedenceOverPierSideChanged()
    {
        // hasFlipped is checked first: a pier-side change we already accounted for must not re-trigger
        // the AlreadyFlipped recenter path.
        var config = MakeConfig();

        var action = MeridianFlipDecision.DecideFlipAction(
            hourAngleHours: 0.5, pierSideChanged: true, alreadyOnCorrectSide: false, hasFlipped: true, config);

        action.ShouldBe(FlipAction.Continue);
    }

    [Fact]
    public void GivenZeroObstructionZoneWhenJustEastOfMeridianThenStillEastOfMeridian()
    {
        // Default behavior preservation: with zone=0, anything HA <= 0 is healthy.
        var config = MakeConfig(obstructionMinutesBefore: 0);

        MeridianFlipDecision.ClassifyHourAngle(-0.001, config).ShouldBe(HourAngleZone.EastOfMeridian);
        MeridianFlipDecision.ClassifyHourAngle(0.0, config).ShouldBe(HourAngleZone.EastOfMeridian);
    }

    [Fact]
    public void GivenEqualEarliestAndLatestWhenJustInsideThenFlipWindow()
    {
        // NINA "fixed flip point" mode: earliest == latest creates a single-tick window.
        var config = MakeConfig(earliestMinutesAfter: 7, latestMinutesAfter: 7);

        // Exactly 7 min past — inclusive on both ends
        MeridianFlipDecision.ClassifyHourAngle(7.0 / 60.0, config).ShouldBe(HourAngleZone.InFlipWindow);
        // 7.5 min — past
        MeridianFlipDecision.ClassifyHourAngle(7.5 / 60.0, config).ShouldBe(HourAngleZone.PastFlipWindow);
    }

    [Fact]
    public void TheCountdownRunsToTheEarliestSanctionedFlipAtTheSiderealRate()
    {
        var config = MakeConfig(earliestMinutesAfter: 5);

        // An hour east of the meridian, with the flip a further 5 min past it, is 65 sidereal minutes of
        // hour angle to cover -- which takes slightly LESS than 65 wall-clock minutes, because HA gains on
        // the clock by about 4 minutes a day.
        var until = MeridianFlipDecision.TimeUntilFlip(-1.0, config).ShouldNotBeNull();

        until.TotalMinutes.ShouldBeLessThan(65.0);
        until.TotalMinutes.ShouldBe(65.0 / 1.00273790935, 0.001);
    }

    [Fact]
    public void ThereIsNothingToCountDownToOnceTheFlipPointIsReached()
    {
        var config = MakeConfig(earliestMinutesAfter: 5);

        // At and past the earliest flip point the flip is due now, not in a negative amount of time.
        MeridianFlipDecision.TimeUntilFlip(5.0 / 60.0, config).ShouldBeNull();
        MeridianFlipDecision.TimeUntilFlip(0.5, config).ShouldBeNull();

        // An unread hour angle is unknown, not "due immediately".
        MeridianFlipDecision.TimeUntilFlip(double.NaN, config).ShouldBeNull();
    }

    [Fact]
    public void TheCountdownFollowsTheConfiguredFlipPointRatherThanTheMeridian()
    {
        // A rig told to flip 20 min after the meridian has 20 more minutes of imaging than one told 5, so
        // reading the countdown off the meridian would cut every rig's target short on the display.
        var early = MeridianFlipDecision.TimeUntilFlip(-0.5, MakeConfig(earliestMinutesAfter: 5)).ShouldNotBeNull();
        var late = MeridianFlipDecision.TimeUntilFlip(-0.5, MakeConfig(earliestMinutesAfter: 20)).ShouldNotBeNull();

        (late - early).TotalMinutes.ShouldBe(15.0 / 1.00273790935, 0.001);
    }
}
