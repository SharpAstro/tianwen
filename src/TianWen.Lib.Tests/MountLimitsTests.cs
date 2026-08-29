using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="MountLimits"/>, the pure safety-limit decider ported from GSServer's
/// <c>CheckAxisLimits</c>.
/// </summary>
/// <remarks>
/// The properties that matter are the ones a poll loop can violate: warn strictly before acting,
/// never act twice for one breach, never act on a target that is climbing away from the limit, and
/// never invent a verdict from a coordinate we do not have.
/// </remarks>
public class MountLimitsTests
{
    private static MountLimitConfiguration Enabled(
        double meridianWarnDeg = 5.0,
        double meridianActionExtraDeg = 5.0,
        MountLimitResponse meridianResponse = MountLimitResponse.StopTracking,
        double horizonActionDeg = 10.0,
        double horizonWarnExtraDeg = 5.0,
        MountLimitResponse horizonResponse = MountLimitResponse.Park)
        => new MountLimitConfiguration(
            Enabled: true,
            MeridianWarnDeg: meridianWarnDeg,
            MeridianActionExtraDeg: meridianActionExtraDeg,
            MeridianResponse: meridianResponse,
            HorizonActionDeg: horizonActionDeg,
            HorizonWarnExtraDeg: horizonWarnExtraDeg,
            HorizonResponse: horizonResponse);

    [Fact]
    public void LimitsAreOffByDefault()
    {
        // A limit that fires when nobody asked for one ends a session. GSServer's LimitsOn.
        new MountLimitConfiguration().Enabled.ShouldBeFalse();
    }

    [Fact]
    public void NeitherLimitParksByDefault()
    {
        // Parking is MOTION, across a path nothing has checked. A mount stopped at 8 deg altitude may
        // be a hand's width from a tripod leg, and a park slew from there is the command most likely
        // to find it. Stopping tracking is the minimal intervention that stops things getting worse;
        // stowing the rig is a different want and belongs to whoever asks for it.
        var config = new MountLimitConfiguration();

        config.MeridianResponse.ShouldBe(MountLimitResponse.StopTracking);
        config.HorizonResponse.ShouldBe(MountLimitResponse.StopTracking);
    }

    [Fact]
    public void TheDefaultThresholdsWarnBeforeTheyAct()
    {
        var config = new MountLimitConfiguration();

        config.MeridianActionDeg.ShouldBeGreaterThan(config.MeridianWarnDeg);
        config.HorizonWarnDeg.ShouldBeGreaterThan(config.HorizonActionDeg);
    }

    [Fact]
    public void ADisabledConfigurationNeverBreachesHoweverBadTheGeometry()
    {
        // 8 h past the meridian and 40 deg below the horizon: physically impossible on a real rig,
        // and still silent, because the master switch is the master switch.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 8.0, altitudeDeg: -40.0, isTracking: true, alreadyActed: false,
            new MountLimitConfiguration());

        verdict.IsBreached.ShouldBeFalse();
        verdict.ShouldBe(MountLimitVerdict.Clear);
    }

    #region Meridian

    [Theory]
    [InlineData(-6.0)]  // 6 h east, rising
    [InlineData(-0.5)]
    [InlineData(0.0)]   // exactly on the meridian
    [InlineData(0.32)]  // ~4.8 deg west, just inside the 5 deg warn threshold
    public void EastOfTheWarnThresholdIsClear(double hourAngleHours)
        => MountLimits.Evaluate(hourAngleHours, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();

    [Fact]
    public void BetweenWarnAndActionItWarnsAndDoesNotAct()
    {
        // 7 deg past the meridian: warn is 5, action is 5 + 5 = 10.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 7.0 / 15.0, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.IsWarningOnly.ShouldBeTrue();
        // Negative = still this far from acting. 7 - 10 = -3.
        verdict.ExceededByDeg.ShouldBe(-3.0, tolerance: 1e-9);
        verdict.Describe().ShouldContain("3.0 deg");
    }

    [Fact]
    public void PastTheActionThresholdItActs()
    {
        // 12 deg past: 2 deg beyond the 10 deg action point.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 12.0 / 15.0, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.IsWarningOnly.ShouldBeFalse();
        verdict.Response.ShouldBe(MountLimitResponse.StopTracking);
        verdict.ExceededByDeg.ShouldBe(2.0, tolerance: 1e-9);
    }

    [Fact]
    public void TheMeridianLimitDoesNotCareWhetherTheMountIsTracking()
    {
        // Being past the limit is a fact about where the tube IS, not about the motors. A mount that
        // was stopped inside the limit is still inside it.
        MountLimits.Evaluate(12.0 / 15.0, 60.0, isTracking: false, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Meridian);
    }

    [Fact]
    public void AnUnknownHourAngleDoesNotProduceAMeridianVerdict()
    {
        // NaN compares false against everything, so a naive `haDeg >= warn` would answer "clear" by
        // accident rather than by decision. Make it a decision.
        MountLimits.Evaluate(double.NaN, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    #endregion

    #region Horizon

    [Fact]
    public void ADescendingTargetBelowTheFloorActs()
    {
        // HA > 0 = past upper transit = descending. 6 deg altitude against a 10 deg floor.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 0.2, altitudeDeg: 6.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Horizon);
        verdict.IsWarningOnly.ShouldBeFalse();
        verdict.Response.ShouldBe(MountLimitResponse.Park);
        verdict.ExceededByDeg.ShouldBe(4.0, tolerance: 1e-9);
    }

    [Fact]
    public void ADescendingTargetBetweenWarnAndFloorOnlyWarns()
    {
        // Floor 10, warn 15. At 13 deg the user is told and nothing is taken away.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 0.2, altitudeDeg: 13.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Horizon);
        verdict.IsWarningOnly.ShouldBeTrue();
        verdict.ExceededByDeg.ShouldBe(-3.0, tolerance: 1e-9);
    }

    [Theory]
    [InlineData(-6.0)]   // deep in the east
    [InlineData(-0.01)]  // a moment before transit
    [InlineData(0.0)]    // exactly at transit: altitude is at its maximum, not falling
    public void ARisingTargetIsNeverAtTheHorizonLimit(double hourAngleHours)
    {
        // THE asymmetry. A target at 4 deg in the east will be at 10 deg shortly; acting on it would
        // refuse most of a night's early schedule. Altitude is maximal at HA = 0 and falls only
        // afterwards, so HA <= 0 is exactly "not descending" -- in both hemispheres, and for fork and
        // AltAz mounts that have no pier side for GSServer's version of this test to read.
        MountLimits.Evaluate(hourAngleHours, altitudeDeg: 4.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    [Fact]
    public void TheHorizonLimitIsGatedOnTracking()
    {
        // A parked or stowed mount routinely sits below the floor. Alarming there would alarm forever,
        // at exactly the times nothing is at risk.
        MountLimits.Evaluate(0.2, altitudeDeg: 2.0, isTracking: false, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    [Fact]
    public void AnUnknownAltitudeDoesNotProduceAHorizonVerdict()
        => MountLimits.Evaluate(0.2, double.NaN, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();

    [Fact]
    public void AnUnknownHourAngleDeclinesTheHorizonTestRatherThanGuessingItIsDescending()
    {
        // Without an HA we cannot establish that the pointing is getting worse, and a horizon verdict
        // whose whole justification is "it will keep falling" must not be issued on an assumption.
        MountLimits.Evaluate(double.NaN, altitudeDeg: 2.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    #endregion

    #region The latch

    [Fact]
    public void AlreadyActedDowngradesToWarnButStaysBreached()
    {
        // GSServer's `SlewState != SlewType.SlewPark` guard, commented "only hit this once while in
        // limit". Without it the poll loop re-commands the park every tick and the park slew restarts
        // forever, never arriving. Downgrading to Warn rather than Clear is the point: the mount is
        // still in the limit and the user must keep being told.
        var acted = MountLimits.Evaluate(
            hourAngleHours: 12.0 / 15.0, altitudeDeg: 60.0, isTracking: true, alreadyActed: true, Enabled());

        acted.IsBreached.ShouldBeTrue();
        acted.Kind.ShouldBe(MountLimitKind.Meridian);
        acted.Response.ShouldBe(MountLimitResponse.Warn);
    }

    [Fact]
    public void AlreadyActedDoesNotManufactureABreachWhereThereIsNone()
    {
        // A stale latch must not keep alarming once the mount is back inside the limits, or the
        // caller can never clear it.
        MountLimits.Evaluate(0.0, altitudeDeg: 60.0, isTracking: true, alreadyActed: true, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    [Fact]
    public void AlreadyActedLeavesAWarningAloneSoTheCountdownKeepsRunning()
    {
        var warning = MountLimits.Evaluate(
            hourAngleHours: 7.0 / 15.0, altitudeDeg: 60.0, isTracking: true, alreadyActed: true, Enabled());

        warning.IsWarningOnly.ShouldBeTrue();
        warning.ExceededByDeg.ShouldBe(-3.0, tolerance: 1e-9);
    }

    #endregion

    #region Precedence

    [Fact]
    public void AnActionOutranksAWarningEvenWhenTheWarningsResponseIsMoreSevere()
    {
        // The horizon is only WARNING, but its configured response (Park) is the most severe there
        // is; the meridian is actually DUE, but its response (Warn) is the least. Ranking on Response
        // alone picks the horizon and leaves the pier limit unreported.
        //
        // The responses have to be this far apart for the test to bite. Written with the natural
        // StopTracking-vs-Park pair it passes against a rank that ignores the action/warning
        // distinction entirely, because both sides then tie and the meridian wins on the tie-break --
        // the right answer for the wrong reason, and a broken rank would have shipped.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 12.0 / 15.0, altitudeDeg: 13.0, isTracking: true, alreadyActed: false,
            Enabled(meridianResponse: MountLimitResponse.Warn,
                    horizonResponse: MountLimitResponse.Park));

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.IsWarningOnly.ShouldBeFalse();
    }

    [Fact]
    public void WhenBothAreDueTheMoreSevereRESPONSEWins()
    {
        // Both actionable, and the horizon's configured response is the stronger one. Park is a
        // superset of StopTracking -- it satisfies the meridian's need as well -- so taking the
        // stronger action is right even though the meridian is the more dangerous limit. What the
        // verdict then NAMES is the limit driving the action, which is the horizon.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 12.0 / 15.0, altitudeDeg: 2.0, isTracking: true, alreadyActed: false,
            Enabled(meridianResponse: MountLimitResponse.StopTracking,
                    horizonResponse: MountLimitResponse.Park));

        verdict.Response.ShouldBe(MountLimitResponse.Park);
        verdict.Kind.ShouldBe(MountLimitKind.Horizon);
    }

    [Fact]
    public void WhenBothAreDueAtTheSAMEResponseTheMeridianWins()
    {
        // The genuine tie, and the only case the kind precedence decides. The meridian ends with the
        // tube against the pier; the horizon merely ends with it pointed somewhere useless.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 12.0 / 15.0, altitudeDeg: 2.0, isTracking: true, alreadyActed: false,
            Enabled(meridianResponse: MountLimitResponse.Park,
                    horizonResponse: MountLimitResponse.Park));

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
    }

    [Fact]
    public void WhenOnlyTheHorizonIsDueTheHorizonWins()
        => MountLimits.Evaluate(0.2, altitudeDeg: 2.0, isTracking: true, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Horizon);

    #endregion

    #region Threshold ordering

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]     // a negative extra must not invert the ordering
    [InlineData(-100.0)]
    public void ANonPositiveExtraStillLeavesActionAtOrAfterWarn(double extra)
    {
        var config = Enabled(meridianWarnDeg: 5.0, meridianActionExtraDeg: extra,
            horizonActionDeg: 10.0, horizonWarnExtraDeg: extra);

        // Warn-before-action is what the "extra" shape exists to guarantee; a second absolute
        // threshold could be edited into the wrong order and silently act before warning.
        config.MeridianActionDeg.ShouldBeGreaterThanOrEqualTo(config.MeridianWarnDeg);
        config.HorizonWarnDeg.ShouldBeGreaterThanOrEqualTo(config.HorizonActionDeg);
    }

    [Fact]
    public void WithNoExtraTheWarningAndActionCoincideAndItActsImmediately()
    {
        // A user who wants no grace period gets none, rather than an unreachable action threshold.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 5.0 / 15.0, altitudeDeg: 60.0, isTracking: true, alreadyActed: false,
            Enabled(meridianWarnDeg: 5.0, meridianActionExtraDeg: 0.0));

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.IsWarningOnly.ShouldBeFalse();
    }

    #endregion

    [Fact]
    public void AClearVerdictDescribesItselfWithoutThrowing()
        => MountLimitVerdict.Clear.Describe().ShouldNotBeNullOrWhiteSpace();
}
