using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The one filter change (<see cref="FilterChange"/>), which the session and the node's device plane both call: the wheel
/// turned, its arrival awaited, and the focuser moved by the offset of the filter arrived at less the offset of the one left.
/// Over the fake wheel's first preset (L 0, R +20, G 0, B -15) and the fake focuser, on a fake clock the moves' waits advance.
/// </summary>
public class FilterChangeTests(ITestOutputHelper output)
{
    private const int L = 0, R = 1, G = 2, B = 3;

    private async Task<(IFilterWheelDriver Wheel, FakeFocuserDriver Focuser, FakeTimeProviderWrapper Time)> RigAsync(CancellationToken ct)
    {
        var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 10, 7, 11, 30, 0, TimeSpan.Zero));
        var services = new FakeExternal(output, time).BuildServiceProvider();
        var wheel = new FakeFilterWheelDriver(new FakeDevice(DeviceType.FilterWheel, 1), services);
        var focuser = new FakeFocuserDriver(new FakeDevice(DeviceType.Focuser, 1), services);
        await wheel.ConnectAsync(ct);
        await focuser.ConnectAsync(ct);
        return (wheel, focuser, time);
    }

    [Theory]
    [InlineData(L, R, 20)]
    [InlineData(R, G, -20)]
    [InlineData(R, B, -35)]
    [InlineData(G, B, -15)]
    [InlineData(B, L, 15)]
    [InlineData(G, L, 0)]
    [InlineData(R, 7, 0)]
    [InlineData(-1, R, 0)]
    public void TheOffsetIsTheFilterArrivedAtLessTheFilterLeft(int from, int to, int expected)
    {
        // A slot the wheel names no filter for (7, or -1 for a wheel that cannot say) has no offset, so it moves nothing.
        FilterChange.FocusOffsetSteps(FakeFilterWheelDriver.GetDefaultFiltersForId(1), from, to).ShouldBe(expected);
    }

    [Fact(Timeout = 30_000)]
    public async Task AWalkThroughEveryFilterAndBackLandsTheFocuserAtEachFiltersOwnFocus()
    {
        // The session's rule before the hoist added the target's offset against luminance to wherever the focuser stood, and
        // only when that offset was not zero: R to G stayed at +20 and R to B landed at +5. Each filter's focus is its offset
        // from the luminance focus, however the wheel got there.
        var ct = TestContext.Current.CancellationToken;
        var (wheel, focuser, time) = await RigAsync(ct);
        (await wheel.GetPositionAsync(ct)).ShouldBe(L, "the walk starts at luminance");
        var luminanceFocus = await focuser.GetPositionAsync(ct);
        var direction = new FocusDirection(PreferOutward: true, OutwardIsPositive: true);

        int[] walk = [R, G, B, L];
        int[] expected = [20, 0, -15, 0];
        for (var step = 0; step < walk.Length; step++)
        {
            var outcome = await FilterChange.ChangeAsync(wheel, FilterWheelMotion.Direct(wheel), walk[step],
                FilterFocuser.Direct(focuser, direction), time, FakeExternal.CreateLogger(output), "the walk", ct);

            outcome.ToPosition.ShouldBe(walk[step]);
            (await wheel.GetPositionAsync(ct)).ShouldBe(walk[step], "the change returns once the wheel is there");
            (await focuser.GetIsMovingAsync(ct)).ShouldBeFalse("and once the focuser has stopped");
            var relative = await focuser.GetPositionAsync(ct) - luminanceFocus;
            output.WriteLine($"{FilterChange.NameOf(wheel.Filters, outcome.FromPosition)} to {FilterChange.NameOf(wheel.Filters, walk[step])}: "
                + $"focuser moved {outcome.FocusOffsetSteps}, now {relative:+0;-0;0} from the luminance focus");
            relative.ShouldBe(expected[step], $"through {FilterChange.NameOf(wheel.Filters, walk[step])}");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AChangeToTheFilterAlreadyInTheBeamMovesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (wheel, focuser, time) = await RigAsync(ct);
        var before = await focuser.GetPositionAsync(ct);

        var outcome = await FilterChange.ChangeAsync(wheel, FilterWheelMotion.Direct(wheel), L,
            FilterFocuser.Direct(focuser, new FocusDirection(true, true)), time, FakeExternal.CreateLogger(output), "the test", ct);

        outcome.ShouldBe(new FilterChangeOutcome(L, L, 0, null));
        outcome.Turned.ShouldBeFalse();
        (await focuser.GetPositionAsync(ct)).ShouldBe(before);
    }

    /// <summary>
    /// A focus offset that would take the focuser past its travel is refused in words BEFORE the wheel turns, as the focuser's own
    /// move route refuses it; it used to turn the wheel and then fail on the focuser, leaving the new filter out of focus (#1639
    /// review round 1, N3).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnOffsetPastTheFocusersTravelIsRefusedBeforeTheWheelTurns()
    {
        var ct = TestContext.Current.CancellationToken;
        var (wheel, _, time) = await RigAsync(ct);
        var services = new FakeExternal(output, time).BuildServiceProvider();
        var focuser = new FakeFocuserDriver(new FakeDevice(DeviceType.Focuser, 2,
            new System.Collections.Specialized.NameValueCollection { ["focuserInitialPosition"] = "1995" }), services);
        await focuser.ConnectAsync(ct);

        var refusal = await Should.ThrowAsync<InvalidOperationException>(FilterChange.ChangeAsync(wheel, FilterWheelMotion.Direct(wheel), R,
            FilterFocuser.Direct(focuser, new FocusDirection(true, true)), time, FakeExternal.CreateLogger(output), "the test", ct).AsTask());

        output.WriteLine(refusal.Message);
        refusal.Message.ShouldContain("between 0 and 2000");
        (await wheel.GetPositionAsync(ct)).ShouldBe(L, "the wheel did not turn");
        (await focuser.GetPositionAsync(ct)).ShouldBe(1995, "nor did the focuser move");
    }

    [Fact(Timeout = 30_000)]
    public async Task WithNoFocuserOnlyTheWheelTurns()
    {
        var ct = TestContext.Current.CancellationToken;
        var (wheel, _, time) = await RigAsync(ct);

        var outcome = await FilterChange.ChangeAsync(wheel, FilterWheelMotion.Direct(wheel), R, focuser: null, time,
            FakeExternal.CreateLogger(output), "the test", ct);

        outcome.ShouldBe(new FilterChangeOutcome(L, R, 0, null), "an offset with no focuser to move is not applied, nor said to be");
        (await wheel.GetPositionAsync(ct)).ShouldBe(R);
    }
}
