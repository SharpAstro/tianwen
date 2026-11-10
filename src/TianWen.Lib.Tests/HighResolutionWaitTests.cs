using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using Xunit;
using Primitive = TianWen.Lib.Devices.HighResolutionWait.WaitPrimitive;

namespace TianWen.Lib.Tests;

/// <summary>
/// WHICH wait <see cref="SystemTimeProvider"/> arms for a duration, pinned by what the code does and never by how late a wait
/// ended (#2031): a short wait and a short one-shot timer go through the high-resolution waitable timer on Windows
/// (<c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, #2020), anything else through <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
/// or the inner provider's timer.
/// </summary>
/// <remarks>
/// <para>This is the half of #2020's tests that can be believed on any machine. Its sibling, <c>SystemTimeProviderPrecisionTests</c>,
/// measures how well that primitive performs against a wall-clock bar, and runs only when asked (<see cref="TimingTestGate"/>).
/// The primitive is read off the call that armed it (<see cref="HighResolutionWait.Arm"/>, <see cref="HighResolutionTimer.ArmedWith"/>),
/// never off a counter shared by the process, which tests running side by side would race.</para>
/// <para>The one wall-clock assertion here is a LOWER bound, that a high-resolution wait does not end before it was asked: a
/// loaded machine only makes a wait later, so it holds whatever else is running. There is no upper bar.</para>
/// </remarks>
public class HighResolutionWaitTests
{
    /// <summary>The slack under which an end before the asked time is the two clocks' disagreement, not an early wait.</summary>
    private const double EarlyToleranceMs = 0.05;

    private static readonly TimeSpan FiveMs = TimeSpan.FromMilliseconds(5);

    [Theory(Timeout = 30_000)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task AShortSleepArmsTheHighResolutionTimerAndNeverEndsBeforeAsked(int milliseconds)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the high-resolution waitable timer is Windows'");
        var ct = TestContext.Current.CancellationToken;
        var duration = TimeSpan.FromMilliseconds(milliseconds);

        // The primitive is read off the very call SleepAsync is made of...
        var (armed, primitive) = SystemTimeProvider.ArmSleep(duration, ct);
        primitive.ShouldBe(Primitive.HighResolution);
        await armed;

        // ...and SleepAsync itself waits at least what was asked.
        var start = Stopwatch.GetTimestamp();
        await SystemTimeProvider.Instance.SleepAsync(duration, ct);
        Stopwatch.GetElapsedTime(start).TotalMilliseconds.ShouldBeGreaterThanOrEqualTo(milliseconds - EarlyToleranceMs, "it never ends before it was asked");
    }

    [Fact(Timeout = 30_000)]
    public async Task ASleepOfExactlyPreciseUpToStillArmsTheHighResolutionTimerAndOneTickPastItDoesNot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the high-resolution waitable timer is Windows'");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var (atEdge, edgePrimitive) = SystemTimeProvider.ArmSleep(HighResolutionWait.PreciseUpTo, cts.Token);
        var (pastEdge, pastPrimitive) = SystemTimeProvider.ArmSleep(HighResolutionWait.PreciseUpTo + TimeSpan.FromTicks(1), cts.Token);

        edgePrimitive.ShouldBe(Primitive.HighResolution, "the longest guide pulse's range is covered, with room");
        pastPrimitive.ShouldBe(Primitive.NotApplicable, "past it a wait is a poll, a settle or a timeout, where a tick is nothing");

        // Neither is waited out: the token ends both, and the high-resolution one releases its timer on the way.
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(atEdge);
        await Should.ThrowAsync<OperationCanceledException>(pastEdge);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(-1)]
    public void ANoWaitAndANegativeDurationAreNotAHighResolutionWait(int milliseconds)
        => HighResolutionWait.Applies(TimeSpan.FromMilliseconds(milliseconds)).ShouldBeFalse();

    [Fact]
    public void OnlyAPositiveDurationUpToPreciseUpToAppliesAndOnlyOnWindows()
    {
        var onWindows = OperatingSystem.IsWindows();

        HighResolutionWait.Applies(TimeSpan.FromTicks(1)).ShouldBe(onWindows);
        HighResolutionWait.Applies(TimeSpan.FromMilliseconds(1)).ShouldBe(onWindows);
        HighResolutionWait.Applies(HighResolutionWait.PreciseUpTo).ShouldBe(onWindows);
        HighResolutionWait.Applies(HighResolutionWait.PreciseUpTo + TimeSpan.FromTicks(1)).ShouldBeFalse();
        HighResolutionWait.Applies(Timeout.InfiniteTimeSpan).ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task AZeroWaitTakesTheOrdinaryPathAndIsAlreadyOver()
    {
        var (wait, primitive) = SystemTimeProvider.ArmSleep(TimeSpan.Zero, TestContext.Current.CancellationToken);

        primitive.ShouldBe(Primitive.NotApplicable);
        wait.IsCompletedSuccessfully.ShouldBeTrue("a zero Task.Delay is complete when it returns");
        await wait;
    }

    [Fact(Timeout = 30_000)]
    public async Task AnInfiniteWaitTakesTheOrdinaryPathAndEndsOnlyWithItsToken()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var (wait, primitive) = SystemTimeProvider.ArmSleep(Timeout.InfiniteTimeSpan, cts.Token);

        primitive.ShouldBe(Primitive.NotApplicable);
        wait.IsCompleted.ShouldBeFalse();

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(wait);
    }

    [Fact(Timeout = 30_000)]
    public async Task AWaitWhoseTokenIsAlreadyCancelledTakesTheOrdinaryPathAndIsCancelled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var (wait, primitive) = SystemTimeProvider.ArmSleep(FiveMs, cts.Token);

        primitive.ShouldBe(Primitive.NotApplicable, "no timer is made for a wait that has nothing to wait for");
        wait.IsCanceled.ShouldBeTrue();
    }

    [Theory(Timeout = 30_000)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task AShortOneShotTimerIsTimedByTheHighResolutionTimerAndNeverFiresBeforeDue(int milliseconds)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the high-resolution waitable timer is Windows'");
        var ct = TestContext.Current.CancellationToken;
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Created disarmed and armed by Change, as the DAL camera's pulse timer is.
        await using var timer = SystemTimeProvider.Instance.CreateTimer(static state => ((TaskCompletionSource?)state)?.TrySetResult(), fired,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var precise = timer.ShouldBeOfType<HighResolutionTimer>("the real clock's timers are the high-resolution ones on Windows");
        precise.ArmedWith.ShouldBe(Primitive.NotApplicable, "nothing is armed yet");

        var start = Stopwatch.GetTimestamp();
        precise.Change(duration, Timeout.InfiniteTimeSpan).ShouldBeTrue();
        precise.ArmedWith.ShouldBe(Primitive.HighResolution);

        await fired.Task.WaitAsync(ct);
        Stopwatch.GetElapsedTime(start).TotalMilliseconds.ShouldBeGreaterThanOrEqualTo(milliseconds - EarlyToleranceMs, "it never fires before it is due");
    }

    [Fact(Timeout = 30_000)]
    public async Task AOneShotTimerMadeArmedIsTheHighResolutionOneToo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the high-resolution waitable timer is Windows'");
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var timer = SystemTimeProvider.Instance.CreateTimer(static state => ((TaskCompletionSource?)state)?.TrySetResult(), fired,
            FiveMs, Timeout.InfiniteTimeSpan);

        timer.ShouldBeOfType<HighResolutionTimer>().ArmedWith.ShouldBe(Primitive.HighResolution);
        await fired.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AOneShotTimerAtPreciseUpToIsTheHighResolutionOneAndOnePastItIsTheInnerProvidersOwn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the high-resolution waitable timer is Windows'");
        using var timer = SystemTimeProvider.Instance.CreateTimer(static _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var precise = timer.ShouldBeOfType<HighResolutionTimer>();

        precise.Change(HighResolutionWait.PreciseUpTo, Timeout.InfiniteTimeSpan);
        precise.ArmedWith.ShouldBe(Primitive.HighResolution);

        precise.Change(HighResolutionWait.PreciseUpTo + TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
        precise.ArmedWith.ShouldBe(Primitive.NotApplicable, "a long due time is a poll or a timeout: the inner provider's timer");
    }

    [Fact]
    public void APeriodicTimerAndADisarmTakeTheInnerProvidersTimer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the high-resolution waitable timer is Windows'");
        using var timer = SystemTimeProvider.Instance.CreateTimer(static _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var precise = timer.ShouldBeOfType<HighResolutionTimer>();

        precise.Change(FiveMs, TimeSpan.FromMilliseconds(10));
        precise.ArmedWith.ShouldBe(Primitive.NotApplicable, "a periodic timer is the inner provider's: the tick is not its error");

        precise.Change(FiveMs, Timeout.InfiniteTimeSpan);
        precise.ArmedWith.ShouldBe(Primitive.HighResolution);

        precise.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        precise.ArmedWith.ShouldBe(Primitive.NotApplicable, "a disarm leaves no high-resolution timer armed (and the one that was is cancelled)");
    }

    [Fact(Timeout = 30_000)]
    public async Task OffWindowsEveryWaitAndEveryTimerTakesTheOrdinaryPath()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "other systems' waits are already fine-grained: this pins that they stay on the BCL's");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var (wait, primitive) = SystemTimeProvider.ArmSleep(FiveMs, cts.Token);
        primitive.ShouldBe(Primitive.NotApplicable);

        await using var timer = SystemTimeProvider.Instance.CreateTimer(static _ => { }, null, FiveMs, Timeout.InfiniteTimeSpan);
        timer.ShouldNotBeOfType<HighResolutionTimer>("off Windows the provider's timer is the inner provider's own");

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(wait);
    }
}
