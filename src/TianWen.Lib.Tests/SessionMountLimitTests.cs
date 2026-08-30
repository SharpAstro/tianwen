using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins that a configured mechanical limit actually stops the mount, which is what turns
/// <see cref="MountLimits"/> from a tested pure function into a protected rig.
/// </summary>
/// <remarks>
/// <para>Enforcement lives on <c>PollDeviceStatesAsync</c>, not on the imaging tick, because the
/// poll is what every slew wait and focus routine already calls. A limit that fired only between
/// exposures would watch a mount drive into a pier during a goto and say nothing, so these tests
/// drive the poll directly rather than a whole run.</para>
///
/// <para><b>Tracking is switched on explicitly in every case.</b> A freshly built test session has
/// not initialised a mount, so tracking starts OFF -- asserting "still tracking" without turning it
/// on first passes for the wrong reason and would keep passing with enforcement deleted.</para>
/// </remarks>
[Collection("Session")]
public class SessionMountLimitTests(ITestOutputHelper output)
{
    /// <summary>Warn at 5 min past the meridian, act at 10 by stopping tracking.</summary>
    private static MountLimitConfiguration StopTrackingPastTheMeridian => new MountLimitConfiguration(
        Enabled: true,
        MeridianWarnMinutes: 5.0,
        MeridianActionExtraMinutes: 5.0,
        MeridianResponse: MountLimitResponse.StopTracking);

    /// <param name="pointingState">
    /// Forced onto the fake AFTER the sync (a sync clears it). Defaults to through-the-pole -- counterweight
    /// down while looking east, the state a GEM is in until it flips -- because that is the state in which
    /// tracking past the meridian carries the tube toward the pier. The fake left to itself reports
    /// Normal for any HA &gt;= 0, i.e. a mount that flipped the instant it crossed, in which the meridian
    /// limit is rightly silent; every "it stops the mount" case below would fail against that default.
    /// </param>
    private static async Task<SessionTestContext> TrackingRigAsync(
        ITestOutputHelper output, MountLimitConfiguration? limits, double hourAngleHours, CancellationToken ct,
        PointingState pointingState = PointingState.ThroughThePole)
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, mountLimits: limits, cancellationToken: ct);

        // SYNC rather than slew. A slew only BEGINS here, and the fake advances with the fake clock
        // which this test never pumps -- so a slewed mount stays exactly where it was and every
        // assertion below would pass for the wrong reason. Sync places it instantly.
        var lst = await ctx.Mount.GetSiderealTimeAsync(ct);
        var ra = ((lst - hourAngleHours) % 24.0 + 24.0) % 24.0;
        await ctx.Mount.SyncRaDecAsync(ra, 45.0, ct);
        await ctx.Mount.SetSideOfPierAsync(pointingState, ct);
        await ctx.Mount.SetTrackingAsync(true, ct);

        (await ctx.Mount.GetHourAngleAsync(ct)).ShouldBe(hourAngleHours, tolerance: 1e-3);
        (await ctx.Mount.GetSideOfPierAsync(ct)).ShouldBe(pointingState);
        return ctx;
    }

    [Fact]
    public async Task AMountWithinItsLimitsIsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        // 1 min east of the meridian: approaching, but not even at the warn threshold.
        var ctx = await TrackingRigAsync(output, StopTrackingPastTheMeridian, -1.0 / 60.0, ct);

        await ctx.Session.PollDeviceStatesAsync(ct);

        ctx.Session.MountLimitVerdict.IsBreached.ShouldBeFalse();
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task PastTheWarnThresholdItWarnsAndDoesNotTouchTheMount()
    {
        var ct = TestContext.Current.CancellationToken;
        // 7 min past the meridian: over the 5 min warn threshold, under the 10 min action one.
        var ctx = await TrackingRigAsync(output, StopTrackingPastTheMeridian, 7.0 / 60.0, ct);

        await ctx.Session.PollDeviceStatesAsync(ct);

        var verdict = ctx.Session.MountLimitVerdict;
        verdict.IsBreached.ShouldBeTrue();
        verdict.IsWarningOnly.ShouldBeTrue();
        verdict.Kind.ShouldBe(MountLimitKind.Meridian);

        // A warning is a warning: the mount keeps imaging.
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task PastTheActionThresholdTheMountIsStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        // 15 min past the meridian, comfortably beyond the 10 min action threshold.
        var ctx = await TrackingRigAsync(output, StopTrackingPastTheMeridian, 1.0, ct);

        await ctx.Session.PollDeviceStatesAsync(ct);

        var verdict = ctx.Session.MountLimitVerdict;
        verdict.IsBreached.ShouldBeTrue();
        verdict.IsWarningOnly.ShouldBeFalse();
        verdict.Kind.ShouldBe(MountLimitKind.Meridian);

        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeFalse("the limit must actually stop the mount");
    }

    [Fact]
    public async Task AMountThatHasFlippedIsLeftAloneHoweverFarPastTheMeridianItTracks()
    {
        var ct = TestContext.Current.CancellationToken;
        // 1 h past the meridian in the NORMAL pointing state: ASCOM pierEast, counterweight down while
        // looking west, which is where a GEM is AFTER its meridian flip. Tracking west from there moves
        // the tube AWAY from the pier, so no hour angle is too large. This is the case the first cut got
        // wrong: it read the hour angle alone and stopped every rig ~30 min after a successful flip.
        var ctx = await TrackingRigAsync(output, StopTrackingPastTheMeridian, 1.0, ct, PointingState.Normal);

        await ctx.Session.PollDeviceStatesAsync(ct);

        ctx.Session.MountLimitVerdict.IsBreached.ShouldBeFalse("a flipped mount tracking west is moving away from the pier");
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task AFlippedMountPointingEastIsStoppedTheSameAsAnUnflippedOnePointingWest()
    {
        var ct = TestContext.Current.CancellationToken;
        // The mirror case: Normal (the looking-west, counterweight-down state) but 1 h EAST of the
        // meridian is counterweight-up by the same amount as through-the-pole 1 h west. A wrong-way goto
        // puts a rig here, and "east = rising = safe" must not wave it through.
        var ctx = await TrackingRigAsync(output, StopTrackingPastTheMeridian, -1.0, ct, PointingState.Normal);

        await ctx.Session.PollDeviceStatesAsync(ct);

        var verdict = ctx.Session.MountLimitVerdict;
        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.IsWarningOnly.ShouldBeFalse();
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task ASkyWatcherVerdictIsMeasuredOnTheAxisNotEstimatedFromTheSky()
    {
        var ct = TestContext.Current.CancellationToken;
        // The SkyWatcher driver models its RA axis, so the session reads the mechanical tier. From home
        // (Normal) a sync 1 h EAST keeps the Normal solution -- the axis at (HA - 6 h) x 15 = -105 deg,
        // counterweight 15 deg above horizontal -- which is 60 min of hour angle past the meridian limit
        // in BOTH tiers; the verdict must say it came from the axis.
        var ctx = await SessionTestHelper.CreateSessionAsync(
            output, mountPort: "SkyWatcher", mountLimits: StopTrackingPastTheMeridian, cancellationToken: ct);
        await ctx.Mount.SetSiteLatitudeAsync(48.2, ct);
        await ctx.Mount.SetSiteLongitudeAsync(16.3, ct);
        var lst = await ctx.Mount.GetSiderealTimeAsync(ct);
        await ctx.Mount.SyncRaDecAsync(((lst + 1.0) % 24.0 + 24.0) % 24.0, 45.0, ct);
        await ctx.Mount.SetTrackingAsync(true, ct);
        (await ctx.Mount.GetAxisAngleAsync(TelescopeAxis.Primary, ct)).ShouldNotBeNull().ShouldBe(-105.0, 0.01, "premise: the driver models its axis");

        await ctx.Session.PollDeviceStatesAsync(ct);

        var verdict = ctx.Session.MountLimitVerdict;
        verdict.Kind.ShouldBe(MountLimitKind.Meridian);
        verdict.Basis.ShouldBe(MountLimitBasis.AxisAngle);
        verdict.ExceededBy.ShouldBe(50.0, 0.05); // 60 min past, action at 10
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task APlainFakeHasNoAxisModelSoItsVerdictIsEstimatedFromTheSky()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await TrackingRigAsync(output, StopTrackingPastTheMeridian, 1.0, ct);
        (await ctx.Mount.GetAxisAngleAsync(TelescopeAxis.Primary, ct)).ShouldBeNull("premise: FakeMountDriver keeps the interface default");

        await ctx.Session.PollDeviceStatesAsync(ct);

        ctx.Session.MountLimitVerdict.Basis.ShouldBe(MountLimitBasis.HourAngle);
    }

    [Fact]
    public async Task AHomedSkyWatcherProducesNoVerdict()
    {
        var ct = TestContext.Current.CancellationToken;
        // The one position every session passes through before its first slew, on the driver whose
        // pointing state comes from the Dec encoder rather than from the hour angle. The first poll of a
        // run happens right here, so a false verdict at home would end every night before it began.
        // Asserts its premise: the mount is actually at the pole, not wherever a sync left it.
        var ctx = await SessionTestHelper.CreateSessionAsync(
            output, mountPort: "SkyWatcher", mountLimits: StopTrackingPastTheMeridian, cancellationToken: ct);
        // What InitialisationAsync does before the first poll (Session.Lifecycle.cs): push the site to
        // the mount. Until it lands, the SkyWatcher driver reports raw home as HA = +6 h, through the
        // pole -- which the meridian test would read as 6 h past the limit -- and on landing it re-syncs
        // home to (LST, pole), HA = 0. A poll before the site is set cannot build a J2000 transform
        // either, so the session never issues one; a mount connected with NO site at all is the
        // residual edge only the sessionless watcher can see.
        await ctx.Mount.SetSiteLatitudeAsync(48.2, ct);
        await ctx.Mount.SetSiteLongitudeAsync(16.3, ct);
        await ctx.Mount.SetTrackingAsync(true, ct);
        (await ctx.Mount.GetDeclinationAsync(ct)).ShouldBe(90.0, tolerance: 0.01, "premise: parked at the pole");
        (await ctx.Mount.GetHourAngleAsync(ct)).ShouldBe(0.0, tolerance: 1e-3, "premise: home re-synced to the meridian once the site landed");

        await ctx.Session.PollDeviceStatesAsync(ct);

        ctx.Session.MountLimitVerdict.IsBreached.ShouldBeFalse();
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task TheActionFiresOnceAndThenDowngradesToAWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await TrackingRigAsync(output, StopTrackingPastTheMeridian, 1.0, ct);

        await ctx.Session.PollDeviceStatesAsync(ct);
        ctx.Session.MountLimitVerdict.IsWarningOnly.ShouldBeFalse();

        // The mount is still past the limit, and the poll runs continuously. Without the latch the
        // action re-fires every tick -- which for a Park response re-commands the park slew forever
        // so it never arrives (GSS's SlewState != SlewType.SlewPark guard). It must DOWNGRADE to a
        // warning rather than clear, so the user goes on being told they are still in the limit.
        await ctx.Session.PollDeviceStatesAsync(ct);

        var second = ctx.Session.MountLimitVerdict;
        second.IsBreached.ShouldBeTrue("still past the limit, so still reported");
        second.Response.ShouldBe(MountLimitResponse.Warn, "the action is latched to fire once per entry");
    }

    [Fact]
    public async Task LimitsThatAreNotConfiguredDoNothingAtAll()
    {
        var ct = TestContext.Current.CancellationToken;

        // A profile that never configured limits deserialises to null. Well past where the limit
        // above would have acted, and nothing happens -- opt-in is the point, because a limit
        // firing on a rig nobody measured is worse than no limit.
        var ctx = await TrackingRigAsync(output, null, 1.0, ct);

        await ctx.Session.PollDeviceStatesAsync(ct);

        ctx.Session.MountLimitVerdict.IsBreached.ShouldBeFalse();
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task ADisabledConfigurationIsAlsoInert()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await TrackingRigAsync(
            output, StopTrackingPastTheMeridian with { Enabled = false }, 1.0, ct);

        await ctx.Session.PollDeviceStatesAsync(ct);

        ctx.Session.MountLimitVerdict.IsBreached.ShouldBeFalse();
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeTrue();
    }
}
