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

        var action = MeridianFlipDecision.DecideFlipAction(hourAngleHours: -0.5, pierSideChanged: true, config);

        action.ShouldBe(FlipAction.AlreadyFlipped);
    }

    [Fact]
    public void GivenEastOfMeridianWhenDecideThenContinue()
    {
        var config = MakeConfig();

        var action = MeridianFlipDecision.DecideFlipAction(hourAngleHours: -1.0, pierSideChanged: false, config);

        action.ShouldBe(FlipAction.Continue);
    }

    [Fact]
    public void GivenInObstructionZoneWhenDecideThenWaitForObstructionClear()
    {
        var config = MakeConfig(obstructionMinutesBefore: 5);

        // 3 min east of meridian, inside the 5-min obstruction zone
        var action = MeridianFlipDecision.DecideFlipAction(hourAngleHours: -0.05, pierSideChanged: false, config);

        action.ShouldBe(FlipAction.WaitForObstructionClear);
    }

    [Fact]
    public void GivenInFlipWindowWhenDecideThenCommandFlip()
    {
        var config = MakeConfig();

        // 7 min past meridian — inside [5, 10] window
        var action = MeridianFlipDecision.DecideFlipAction(hourAngleHours: 7.0 / 60.0, pierSideChanged: false, config);

        action.ShouldBe(FlipAction.CommandFlip);
    }

    [Fact]
    public void GivenPastFlipWindowWhenDecideThenStillCommandFlip()
    {
        // Even when the latest acceptable flip time has passed, we still try to flip — better
        // late than stuck on the wrong side. The mount will fail the slew if it's actually
        // past its tracking limit.
        var config = MakeConfig();

        var action = MeridianFlipDecision.DecideFlipAction(hourAngleHours: 0.5, pierSideChanged: false, config);

        action.ShouldBe(FlipAction.CommandFlip);
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
}
