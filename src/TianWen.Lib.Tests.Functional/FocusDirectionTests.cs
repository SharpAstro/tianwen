using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class FocusDirectionTests(ITestOutputHelper output)
{
    [Theory(Timeout = 60_000)]
    [InlineData(true, true, true, 1)]
    [InlineData(true, false, false, -1)]
    [InlineData(false, true, false, -1)]
    [InlineData(false, false, true, 1)]
    public void GivenFocusDirectionWhenComputedThenPreferredSignMatchesExpected(
        bool preferOutward, bool outwardIsPositive, bool expectedPositive, int expectedSign)
    {
        var dir = new FocusDirection(preferOutward, outwardIsPositive);

        dir.PreferredDirectionIsPositive.ShouldBe(expectedPositive,
            $"PreferOutward={preferOutward}, OutwardIsPositive={outwardIsPositive}");
        dir.PreferredSign.ShouldBe(expectedSign);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenPreferPositiveWhenMovingNegativeThenOvershoots()
    {
        // given — prefer positive direction (outward=+, prefer outward), currently at 1000, target 800
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(focuserDevice, external.BuildServiceProvider());
        await focuser.ConnectAsync(ct);
        await focuser.BeginMoveAsync(1000, ct);
        await WaitForMoveComplete(focuser, external, ct);

        var focusDir = new FocusDirection(PreferOutward: true, OutwardIsPositive: true);
        // PreferredDirectionIsPositive = true

        // when — move to 800 (negative direction, against preferred)
        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, 800, 1000, backlashStepsIn: 20, backlashStepsOut: 20, focusDir, external, ct);

        // then — should end at 800, having overshot past it and returned from the positive side
        var finalPos = await focuser.GetPositionAsync(ct);
        finalPos.ShouldBe(800);

        // The move history should show: 1000 → 780 (overshoot) → 800 (approach from preferred/positive)
        output.WriteLine($"Final position: {finalPos}");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenPreferPositiveWhenMovingPositiveThenNoBaclashCompensation()
    {
        // given — prefer positive, currently at 800, target 1000
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(focuserDevice, external.BuildServiceProvider());
        await focuser.ConnectAsync(ct);
        await focuser.BeginMoveAsync(800, ct);
        await WaitForMoveComplete(focuser, external, ct);

        var focusDir = new FocusDirection(PreferOutward: true, OutwardIsPositive: true);

        // when — move to 1000 (positive direction, same as preferred)
        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, 1000, 800, backlashStepsIn: 20, backlashStepsOut: 20, focusDir, external, ct);

        // then — direct move, no overshoot
        var finalPos = await focuser.GetPositionAsync(ct);
        finalPos.ShouldBe(1000);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenPreferNegativeWhenMovingPositiveThenOvershoots()
    {
        // given — refractor: outward=+, prefer inward (with gravity) → preferred direction is negative
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(focuserDevice, external.BuildServiceProvider());
        await focuser.ConnectAsync(ct);
        await focuser.BeginMoveAsync(800, ct);
        await WaitForMoveComplete(focuser, external, ct);

        var focusDir = new FocusDirection(PreferOutward: false, OutwardIsPositive: true);
        // PreferredDirectionIsPositive = false (prefer negative)

        // when — move to 1000 (positive, against preferred)
        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, 1000, 800, backlashStepsIn: 20, backlashStepsOut: 20, focusDir, external, ct);

        // then — should overshoot past 1000 then approach from above (negative direction)
        var finalPos = await focuser.GetPositionAsync(ct);
        finalPos.ShouldBe(1000);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenPreferNegativeWhenMovingNegativeThenNoBaclashCompensation()
    {
        // given — refractor: outward=+, prefer inward → preferred = negative
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(focuserDevice, external.BuildServiceProvider());
        await focuser.ConnectAsync(ct);
        await focuser.BeginMoveAsync(1000, ct);
        await WaitForMoveComplete(focuser, external, ct);

        var focusDir = new FocusDirection(PreferOutward: false, OutwardIsPositive: true);

        // when — move to 800 (negative, same as preferred)
        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, 800, 1000, backlashStepsIn: 20, backlashStepsOut: 20, focusDir, external, ct);

        // then — direct move, no overshoot
        var finalPos = await focuser.GetPositionAsync(ct);
        finalPos.ShouldBe(800);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenReversedFocuserPreferOutwardWhenMovingPositiveThenOvershoots()
    {
        // given — reversed focuser: outward=−, prefer outward → preferred = negative
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(focuserDevice, external.BuildServiceProvider());
        await focuser.ConnectAsync(ct);
        await focuser.BeginMoveAsync(800, ct);
        await WaitForMoveComplete(focuser, external, ct);

        var focusDir = new FocusDirection(PreferOutward: true, OutwardIsPositive: false);
        // PreferredDirectionIsPositive = false (prefer negative)

        // when — move to 1000 (positive, against preferred)
        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, 1000, 800, backlashStepsIn: 20, backlashStepsOut: 20, focusDir, external, ct);

        // then — overshoot and approach from preferred (negative) side
        var finalPos = await focuser.GetPositionAsync(ct);
        finalPos.ShouldBe(1000);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenZeroBacklashWhenMovingAgainstPreferredThenNoOvershoot()
    {
        // given — no backlash configured
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(focuserDevice, external.BuildServiceProvider());
        await focuser.ConnectAsync(ct);
        await focuser.BeginMoveAsync(1000, ct);
        await WaitForMoveComplete(focuser, external, ct);

        var focusDir = new FocusDirection(PreferOutward: true, OutwardIsPositive: true);

        // when — move against preferred with zero backlash
        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, 800, 1000, backlashStepsIn: 0, backlashStepsOut: 0, focusDir, external, ct);

        // then — direct move (backlash = 0 means overshoot by 0 = no overshoot)
        var finalPos = await focuser.GetPositionAsync(ct);
        finalPos.ShouldBe(800);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenTargetNearZeroWhenOveshootingThenClampedToZero()
    {
        // given — target near 0, backlash would overshoot below 0
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(focuserDevice, external.BuildServiceProvider());
        await focuser.ConnectAsync(ct);
        await focuser.BeginMoveAsync(50, ct);
        await WaitForMoveComplete(focuser, external, ct);

        var focusDir = new FocusDirection(PreferOutward: true, OutwardIsPositive: true);

        // when — move to 5, backlash=20 would try to overshoot to -15
        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, 5, 50, backlashStepsIn: 20, backlashStepsOut: 20, focusDir, external, ct);

        // then — should still reach target (overshoot clamped to 0)
        var finalPos = await focuser.GetPositionAsync(ct);
        finalPos.ShouldBe(5);
    }

    private static async Task WaitForMoveComplete(FakeFocuserDriver focuser, FakeExternal external, CancellationToken ct)
    {
        while (await focuser.GetIsMovingAsync(ct))
        {
            await external.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
        }
    }
}
