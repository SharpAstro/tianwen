using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
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

    private static async Task<SessionTestContext> TrackingRigAsync(
        ITestOutputHelper output, MountLimitConfiguration? limits, double hourAngleHours, CancellationToken ct)
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, mountLimits: limits, cancellationToken: ct);

        // SYNC rather than slew. A slew only BEGINS here, and the fake advances with the fake clock
        // which this test never pumps -- so a slewed mount stays exactly where it was and every
        // assertion below would pass for the wrong reason. Sync places it instantly.
        var lst = await ctx.Mount.GetSiderealTimeAsync(ct);
        var ra = ((lst - hourAngleHours) % 24.0 + 24.0) % 24.0;
        await ctx.Mount.SyncRaDecAsync(ra, 45.0, ct);
        await ctx.Mount.SetTrackingAsync(true, ct);

        (await ctx.Mount.GetHourAngleAsync(ct)).ShouldBe(hourAngleHours, tolerance: 1e-3);
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
