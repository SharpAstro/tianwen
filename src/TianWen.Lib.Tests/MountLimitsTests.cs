using Shouldly;
using TianWen.Lib.Devices;
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
    /// <summary>
    /// An hour angle that is descending (HA &gt; 0) but still clear of the meridian warn threshold,
    /// so a horizon case reports the horizon and nothing else.
    /// </summary>
    /// <remarks>
    /// 3 minutes past. Load-bearing: the meridian and horizon verdicts are ranked against each
    /// other, so a horizon test run at an hour angle that also trips the meridian limit asserts on
    /// whichever won, not on the horizon. This value moved once already when the meridian threshold
    /// changed units -- if these tests start failing together, check this first.
    /// </remarks>
    private const double DescendingClearOfMeridian = 0.05;

    private static MountLimitConfiguration Enabled(
        double meridianWarnMinutes = 5.0,
        double meridianActionExtraMinutes = 5.0,
        MountLimitResponse meridianResponse = MountLimitResponse.StopTracking,
        double horizonActionDeg = 10.0,
        double horizonWarnExtraDeg = 5.0,
        MountLimitResponse horizonResponse = MountLimitResponse.Park)
        => new MountLimitConfiguration(
            Enabled: true,
            MeridianWarnMinutes: meridianWarnMinutes,
            MeridianActionExtraMinutes: meridianActionExtraMinutes,
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

        config.MeridianActionMinutes.ShouldBeGreaterThan(config.MeridianWarnMinutes);
        config.HorizonWarnDeg.ShouldBeGreaterThan(config.HorizonActionDeg);
    }

    [Fact]
    public void ADisabledConfigurationNeverBreachesHoweverBadTheGeometry()
    {
        // 8 h past the meridian and 40 deg below the horizon: physically impossible on a real rig,
        // and still silent, because the master switch is the master switch.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 8.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: -40.0, isTracking: true, alreadyActed: false,
            new MountLimitConfiguration());

        verdict.IsBreached.ShouldBeFalse();
        verdict.ShouldBe(MountLimitVerdict.Clear);
    }

    #region Meridian

    [Theory]
    [InlineData(-6.0)]  // 6 h east, rising
    [InlineData(-0.5)]
    [InlineData(0.0)]   // exactly on the meridian
    [InlineData(0.08)]  // ~4.8 min west, just inside the 5 min warn threshold
    public void EastOfTheWarnThresholdIsClear(double hourAngleHours)
        => MountLimits.Evaluate(hourAngleHours, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();

    [Fact]
    public void BetweenWarnAndActionItWarnsAndDoesNotAct()
    {
        // 7 min past the meridian: warn is 5, action is 5 + 5 = 10.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 7.0 / 60.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.IsWarningOnly.ShouldBeTrue();
        // Negative = still this far from acting. 7 - 10 = -3.
        verdict.ExceededBy.ShouldBe(-3.0, tolerance: 1e-9);
        verdict.Describe().ShouldContain("3 min");
    }

    [Fact]
    public void PastTheActionThresholdItActs()
    {
        // 12 min past: 2 min beyond the 10 min action point.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 12.0 / 60.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.IsWarningOnly.ShouldBeFalse();
        verdict.Response.ShouldBe(MountLimitResponse.StopTracking);
        verdict.ExceededBy.ShouldBe(2.0, tolerance: 1e-9);
    }

    [Fact]
    public void TheMeridianLimitDoesNotCareWhetherTheMountIsTracking()
    {
        // Being past the limit is a fact about where the tube IS, not about the motors. A mount that
        // was stopped inside the limit is still inside it.
        MountLimits.Evaluate(12.0 / 60.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, 60.0, isTracking: false, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Meridian);
    }

    [Fact]
    public void AnUnknownHourAngleDoesNotProduceAMeridianVerdict()
    {
        // NaN compares false against everything, so a naive `haDeg >= warn` would answer "clear" by
        // accident rather than by decision. Make it a decision.
        MountLimits.Evaluate(double.NaN, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    #endregion

    #region Trusting the pointing state

    [Theory]
    [InlineData(PointingStateSource.Measured, PointingState.Normal, PointingState.Normal)]
    [InlineData(PointingStateSource.Measured, PointingState.ThroughThePole, PointingState.ThroughThePole)]
    [InlineData(PointingStateSource.Computed, PointingState.Normal, PointingState.Unknown)]
    [InlineData(PointingStateSource.Computed, PointingState.ThroughThePole, PointingState.Unknown)]
    [InlineData(PointingStateSource.None, PointingState.Normal, PointingState.Unknown)]
    public void OnlyAMeasuredPointingStateIsTrusted(PointingStateSource source, PointingState reported, PointingState expected)
        => MountLimits.TrustedPointingState(source, reported).ShouldBe(expected);

    [Fact]
    public void AComputedNormalWestOfTheMeridianDoesNotSilenceTheLimit()
    {
        // The LX200-base case: the driver derives Normal from HA >= 0 -- "the firmware will have flipped"
        // -- while the real mount may be tracking through the pole into its pier. Untrusted, the state
        // becomes Unknown and the hour-angle tier fires; trusted, it would read as post-flip and stay silent.
        var trusted = MountLimits.TrustedPointingState(PointingStateSource.Computed, PointingState.Normal);
        MountLimits.Evaluate(1.0, trusted, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Meridian);
    }

    // The three-argument overload: a caller that verified the state independently of the driver.
    // Measured still wins outright -- a latched value only moves on a slew edge and would be stale
    // between them, while a measured driver reads its own mechanics every poll.
    [Theory]
    [InlineData(PointingStateSource.Measured, PointingState.Normal, PointingState.ThroughThePole, PointingState.Normal)]
    [InlineData(PointingStateSource.Computed, PointingState.Normal, PointingState.ThroughThePole, PointingState.ThroughThePole)]
    [InlineData(PointingStateSource.Computed, PointingState.Normal, PointingState.Unknown, PointingState.Unknown)]
    [InlineData(PointingStateSource.None, PointingState.Unknown, PointingState.Unknown, PointingState.Unknown)]
    public void AVerifiedStateBeatsAComputedReportButNeverAMeasuredOne(
        PointingStateSource source, PointingState reported, PointingState verified, PointingState expected)
        => MountLimits.TrustedPointingState(source, reported, verified).ShouldBe(expected);

    [Fact]
    public void AVerifiedFlipCatchesTheMirrorHazardTheHourAngleTierCannotSee()
    {
        // The gap this closes, and it has to be the MIRROR case to show anything: west of the meridian
        // an Unknown state and a ThroughThePole one read the same, so both fire and nothing is proven.
        // East is where they part. A rig that HAS flipped (verified Normal) and is then pointed east --
        // a wrong-way goto, a bad sync -- is swinging its tube toward the pier again, and the meridian
        // test only sees that if it knows the mount is on the far side. A computed driver reports
        // ThroughThePole here (it derives the state from HA < 0), which the two-argument overload
        // rightly refuses; Unknown then takes the hour-angle approximation and reads CLEAR.
        const double eastOfMeridian = -1.0;

        var withoutVerification = MountLimits.Evaluate(
            eastOfMeridian,
            MountLimits.TrustedPointingState(PointingStateSource.Computed, PointingState.ThroughThePole),
            primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled());
        withoutVerification.Kind.ShouldBe(MountLimitKind.None,
            "the premise: without a verified state this hazard is invisible");

        var withVerification = MountLimits.Evaluate(
            eastOfMeridian,
            MountLimits.TrustedPointingState(
                PointingStateSource.Computed, PointingState.ThroughThePole, PointingState.Normal),
            primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled());
        withVerification.Kind.ShouldBe(MountLimitKind.Meridian);
    }

    [Fact]
    public void ADriverEnforcedStopDescribesItselfAsALimitNotAFault()
    {
        var verdict = MountLimitVerdict.DriverEnforcedStop;
        verdict.Kind.ShouldBe(MountLimitKind.DriverEnforced);
        verdict.IsBreached.ShouldBeTrue();
        verdict.Response.ShouldBe(MountLimitResponse.Warn, "the driver already acted; there is nothing left to do to the mount");
        verdict.Describe().ShouldContain("not a fault");
    }

    #endregion

    #region Axis angle (the mechanical tier)

    [Fact]
    public void AnAxisAngleBeyondHorizontalIsTheMeridianOffsetWhateverTheSkySays()
    {
        // 100 deg from the counterweight-down home = counterweight 10 deg above horizontal = 40 min of
        // hour angle. The sky inputs say "on the meridian, not flipped", i.e. clear -- and are not asked.
        var verdict = MountLimits.Evaluate(
            0.0, PointingState.ThroughThePole, primaryAxisAngleDeg: 100.0, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.Basis.ShouldBe(MountLimitBasis.AxisAngle);
        verdict.IsWarningOnly.ShouldBeFalse();
        verdict.ExceededBy.ShouldBe(30.0, tolerance: 1e-9); // 40 past, action at 10
        verdict.Describe().ShouldContain("measured on the RA axis");
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(-100.0)]
    public void TheAxisSignDoesNotMatterOnlyHowFarPastHorizontal(double axisDeg)
        => MountLimits.Evaluate(0.0, PointingState.Unknown, axisDeg, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Meridian);

    [Theory]
    [InlineData(0.0)]    // counterweight straight down
    [InlineData(-90.0)]  // horizontal, the meridian in the Normal state
    [InlineData(91.0)]   // 1 deg = 4 min above horizontal, inside the 5 min warn threshold
    public void AnAxisAtOrBelowHorizontalIsClear(double axisDeg)
        => MountLimits.Evaluate(1.0, PointingState.ThroughThePole, axisDeg, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse("the hour angle says 60 min past, the axis says counterweight down -- the axis wins");

    [Fact]
    public void TheAxisWinsOverAnHourAngleThatDisagreesInEitherDirection()
    {
        // Fallback, never cross-check. An unsynced mount is the case the limit exists for, so the two
        // are not required to agree before acting -- the axis is the thing with a pier in its way.
        MountLimits.Evaluate(1.0, PointingState.ThroughThePole, 45.0, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse("sky says 60 min past, axis says safe");
        MountLimits.Evaluate(-6.0, PointingState.ThroughThePole, 105.0, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Meridian, "sky says deep in the east, axis says counterweight up");
    }

    [Fact]
    public void TheAxisNeedsNoHourAngle()
    {
        // The whole point of the tier: a wrong longitude or a stale clock shifts the hour angle by hours
        // and the axis has not moved. With no hour angle at all the meridian test still answers.
        MountLimits.Evaluate(double.NaN, PointingState.Unknown, 100.0, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Meridian);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    public void WithoutAnAxisAngleTheHourAngleTierAnswersAndSaysSo(double? axisDeg)
    {
        var verdict = MountLimits.Evaluate(12.0 / 60.0, PointingState.ThroughThePole, axisDeg, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.Basis.ShouldBe(MountLimitBasis.HourAngle);
        verdict.Describe().ShouldContain("estimated from the hour angle");
    }

    [Fact]
    public void TheAxisDoesNotTouchTheHorizonTest()
    {
        // The horizon is a sky fact and keeps needing the hour angle to know it is descending.
        MountLimits.Evaluate(double.NaN, PointingState.Unknown, 45.0, altitudeDeg: 2.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
        MountLimits.Evaluate(DescendingClearOfMeridian, PointingState.Unknown, 45.0, altitudeDeg: 6.0, isTracking: true, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Horizon);
    }

    #endregion

    #region Pointing state

    [Theory]
    [InlineData(0.5)]
    [InlineData(3.0)]
    [InlineData(11.0)]
    public void AFlippedMountIsClearHoweverFarWestItTracks(double hourAngleHours)
    {
        // Normal = ASCOM pierEast, counterweight down while looking west: where a GEM is AFTER its flip.
        // Tracking west from there carries the tube AWAY from the pier, so no hour angle is too large.
        // This is the bug the state parameter exists to fix: reading the hour angle alone stopped every
        // rig that had flipped the moment its HA reached the action threshold, ~30 min after a good flip.
        MountLimits.Evaluate(hourAngleHours, PointingState.Normal, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    [Fact]
    public void AFlippedMountPointingEastIsTheMirrorHazard()
    {
        // The same axis 12 h round: a Normal mount 12 min EAST of the meridian is counterweight-up by
        // exactly as much as a through-the-pole mount 12 min west of it. A wrong-way goto or a bad sync
        // puts a rig here; the limit must read it, not wave it through as "east = rising = safe".
        var verdict = MountLimits.Evaluate(
            -12.0 / 60.0, PointingState.Normal, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.IsWarningOnly.ShouldBeFalse();
        verdict.ExceededBy.ShouldBe(2.0, tolerance: 1e-9);
    }

    [Fact]
    public void AThroughThePoleMountEastOfTheMeridianIsClear()
    {
        // Counterweight down, looking east, rising toward the meridian: the ordinary start of a night.
        MountLimits.Evaluate(-3.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 30.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    [Fact]
    public void AnUnknownPointingStateReadsTheHourAngleAsThePreFlipOffset()
    {
        // A driver that cannot say leaves nothing better than the sky-coordinate approximation this
        // limit shipped with: right for a mount that has not flipped, wrong after one. Pinned so the
        // fallback stays deliberate, and stays labelled as the weaker tier in the plan.
        MountLimits.Evaluate(12.0 / 60.0, PointingState.Unknown, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Meridian);
        MountLimits.Evaluate(-12.0 / 60.0, PointingState.Unknown, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    [Fact]
    public void ThePointingStateDoesNotTouchTheHorizonTest()
    {
        // Altitude is a sky quantity: HA > 0 is descending whichever side of the pier the tube is on.
        MountLimits.Evaluate(DescendingClearOfMeridian, PointingState.Normal, primaryAxisAngleDeg: null, altitudeDeg: 6.0, isTracking: true, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Horizon);
    }

    #endregion

    #region Horizon

    [Fact]
    public void ADescendingTargetBelowTheFloorActs()
    {
        // HA > 0 = past upper transit = descending. 6 deg altitude against a 10 deg floor.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: DescendingClearOfMeridian, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 6.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Horizon);
        verdict.IsWarningOnly.ShouldBeFalse();
        verdict.Response.ShouldBe(MountLimitResponse.Park);
        verdict.ExceededBy.ShouldBe(4.0, tolerance: 1e-9);
    }

    [Fact]
    public void ADescendingTargetBetweenWarnAndFloorOnlyWarns()
    {
        // Floor 10, warn 15. At 13 deg the user is told and nothing is taken away.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: DescendingClearOfMeridian, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 13.0, isTracking: true, alreadyActed: false, Enabled());

        verdict.Kind.ShouldBe(MountLimitKind.Horizon);
        verdict.IsWarningOnly.ShouldBeTrue();
        verdict.ExceededBy.ShouldBe(-3.0, tolerance: 1e-9);
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
        MountLimits.Evaluate(hourAngleHours, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 4.0, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    [Fact]
    public void TheHorizonLimitIsGatedOnTracking()
    {
        // A parked or stowed mount routinely sits below the floor. Alarming there would alarm forever,
        // at exactly the times nothing is at risk.
        MountLimits.Evaluate(DescendingClearOfMeridian, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 2.0, isTracking: false, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    [Fact]
    public void AnUnknownAltitudeDoesNotProduceAHorizonVerdict()
        => MountLimits.Evaluate(DescendingClearOfMeridian, PointingState.ThroughThePole, primaryAxisAngleDeg: null, double.NaN, isTracking: true, alreadyActed: false, Enabled())
            .IsBreached.ShouldBeFalse();

    [Fact]
    public void AnUnknownHourAngleDeclinesTheHorizonTestRatherThanGuessingItIsDescending()
    {
        // Without an HA we cannot establish that the pointing is getting worse, and a horizon verdict
        // whose whole justification is "it will keep falling" must not be issued on an assumption.
        MountLimits.Evaluate(double.NaN, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 2.0, isTracking: true, alreadyActed: false, Enabled())
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
            hourAngleHours: 12.0 / 60.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: true, Enabled());

        acted.IsBreached.ShouldBeTrue();
        acted.Kind.ShouldBe(MountLimitKind.Meridian);
        acted.Response.ShouldBe(MountLimitResponse.Warn);
        acted.Latched.ShouldBeTrue("the downgrade is a latch, and the sentence must say so rather than \"warn only\"");
        acted.Describe().ShouldContain("already acted");
    }

    [Fact]
    public void AlreadyActedDoesNotManufactureABreachWhereThereIsNone()
    {
        // A stale latch must not keep alarming once the mount is back inside the limits, or the
        // caller can never clear it.
        MountLimits.Evaluate(0.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: true, Enabled())
            .IsBreached.ShouldBeFalse();
    }

    [Fact]
    public void AlreadyActedLeavesAWarningAloneSoTheCountdownKeepsRunning()
    {
        var warning = MountLimits.Evaluate(
            hourAngleHours: 7.0 / 60.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: true, Enabled());

        warning.IsWarningOnly.ShouldBeTrue();
        warning.ExceededBy.ShouldBe(-3.0, tolerance: 1e-9);
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
            hourAngleHours: 12.0 / 60.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 13.0, isTracking: true, alreadyActed: false,
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
            hourAngleHours: 12.0 / 60.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 2.0, isTracking: true, alreadyActed: false,
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
            hourAngleHours: 12.0 / 60.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 2.0, isTracking: true, alreadyActed: false,
            Enabled(meridianResponse: MountLimitResponse.Park,
                    horizonResponse: MountLimitResponse.Park));

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
    }

    [Fact]
    public void WhenOnlyTheHorizonIsDueTheHorizonWins()
        => MountLimits.Evaluate(DescendingClearOfMeridian, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 2.0, isTracking: true, alreadyActed: false, Enabled())
            .Kind.ShouldBe(MountLimitKind.Horizon);

    #endregion

    #region Threshold ordering

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]     // a negative extra must not invert the ordering
    [InlineData(-100.0)]
    public void ANonPositiveExtraStillLeavesActionAtOrAfterWarn(double extra)
    {
        var config = Enabled(meridianWarnMinutes: 5.0, meridianActionExtraMinutes: extra,
            horizonActionDeg: 10.0, horizonWarnExtraDeg: extra);

        // Warn-before-action is what the "extra" shape exists to guarantee; a second absolute
        // threshold could be edited into the wrong order and silently act before warning.
        config.MeridianActionMinutes.ShouldBeGreaterThanOrEqualTo(config.MeridianWarnMinutes);
        config.HorizonWarnDeg.ShouldBeGreaterThanOrEqualTo(config.HorizonActionDeg);
    }

    [Fact]
    public void WithNoExtraTheWarningAndActionCoincideAndItActsImmediately()
    {
        // A user who wants no grace period gets none, rather than an unreachable action threshold.
        var verdict = MountLimits.Evaluate(
            hourAngleHours: 5.0 / 60.0, PointingState.ThroughThePole, primaryAxisAngleDeg: null, altitudeDeg: 60.0, isTracking: true, alreadyActed: false,
            Enabled(meridianWarnMinutes: 5.0, meridianActionExtraMinutes: 0.0));

        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.IsWarningOnly.ShouldBeFalse();
    }

    #endregion

    [Fact]
    public void AClearVerdictDescribesItselfWithoutThrowing()
        => MountLimitVerdict.Clear.Describe().ShouldNotBeNullOrWhiteSpace();
}
