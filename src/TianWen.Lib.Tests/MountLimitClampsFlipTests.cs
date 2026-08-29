using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins that the mechanical limit is the ULTIMATE CLAMP on the meridian flip window.
/// </summary>
/// <remarks>
/// <para>The two settings bound the same axis and, before this, simply raced -- and the limit won,
/// which is the worst outcome: the mount is stopped at the very moment it was about to flip, ending
/// the night instead of continuing it. Worse, they were in different units (flip in minutes past
/// the meridian, limit in degrees of hour angle), so "5 and 10" in each looked like the same
/// numbers and were not: 5 deg is 20 min.</para>
///
/// <para><b>The direction of the dependency is the whole point.</b> How long to keep imaging before
/// flipping is a preference; where the tube meets the pier is a fact. So the fact caps the
/// preference. Deriving the limit from the flip instead -- the same "threshold plus a non-negative
/// EXTRA" trick <see cref="MountLimitConfiguration"/> uses for warn/act -- would be exactly
/// backwards: raise the flip deadline to an hour and the mechanical limit would follow it into the
/// pier.</para>
/// </remarks>
public class MountLimitClampsFlipTests
{
    private static MountLimitConfiguration Limits(bool enabled, double warnMinutes, double actionExtraMinutes)
        => new MountLimitConfiguration(
            Enabled: enabled,
            MeridianWarnMinutes: warnMinutes,
            MeridianActionExtraMinutes: actionExtraMinutes);

    [Fact]
    public void AFlipDeadlineInsideTheLimitIsLeftAlone()
    {
        // Action at 40 min, clearance 5 => anything up to 35 is the user's own business.
        var limits = Limits(enabled: true, warnMinutes: 20.0, actionExtraMinutes: 20.0);

        limits.ClampFlipLatestMinutes(10.0).ShouldBe(10.0);
        limits.ClampFlipLatestMinutes(35.0).ShouldBe(35.0);
    }

    [Fact]
    public void AFlipDeadlinePastTheLimitIsPulledBackInside()
    {
        var limits = Limits(enabled: true, warnMinutes: 20.0, actionExtraMinutes: 20.0);

        // Asking to track an hour past the meridian on a rig whose tube reaches the pier at 40 min.
        // The request loses; it does not silently move the pier.
        limits.ClampFlipLatestMinutes(60.0)
            .ShouldBe(limits.MeridianActionMinutes - MountLimitConfiguration.FlipClearanceMinutes);
        limits.ClampFlipLatestMinutes(60.0).ShouldBe(35.0);
    }

    [Fact]
    public void DisabledLimitsClampNothing()
    {
        // Opt-in throughout: a rig that never configured limits must behave exactly as before.
        Limits(enabled: false, warnMinutes: 20.0, actionExtraMinutes: 20.0)
            .ClampFlipLatestMinutes(60.0).ShouldBe(60.0);
    }

    [Fact]
    public void TheLimitNeverMovesWhenTheFlipPreferenceDoes()
    {
        // The inverse design would have had the limit trail the flip deadline. It must not.
        var limits = Limits(enabled: true, warnMinutes: 20.0, actionExtraMinutes: 20.0);

        var before = limits.MeridianActionMinutes;
        _ = limits.ClampFlipLatestMinutes(600.0);

        limits.MeridianActionMinutes.ShouldBe(before, "a preference must never move a safety bound");
    }

    [Fact]
    public void TheFlipClassifierAppliesTheClampSoNoCallerCanForget()
    {
        var config = SessionTestHelper.DefaultConfiguration with
        {
            MeridianFlipObstructionZoneMinutesBefore = 0,
            MeridianFlipEarliestMinutesAfter = 5,
            MeridianFlipLatestMinutesAfter = 60,
        };
        var limits = Limits(enabled: true, warnMinutes: 20.0, actionExtraMinutes: 20.0);

        // 45 min past: inside the REQUESTED window, outside the clamped one.
        const double ha = 45.0 / 60.0;

        MeridianFlipDecision.ClassifyHourAngle(ha, config)
            .ShouldBe(HourAngleZone.InFlipWindow, "unclamped, the request stands");

        MeridianFlipDecision.ClassifyHourAngle(ha, config, limits)
            .ShouldBe(HourAngleZone.PastFlipWindow, "clamped, the flip was already overdue at 35 min");
    }
}
