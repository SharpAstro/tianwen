using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the composite <c>PulseGuideAsync</c>: that it WAITS, and that it overlaps the two axes only
/// where the target says it may.
/// </summary>
/// <remarks>
/// <para>Both properties are invisible to a wall clock and to every existing test. The guide loop
/// used to await <c>StartPulseGuideAsync</c> directly, which waits for the pulse only on a driver
/// that happens to block -- SkyWatcher and nothing else. On ASCOM, Alpaca, ST-4 and the fakes the
/// loop went straight on to expose the next frame while the mount was still moving, then measured
/// it as settled. The whole functional suite passed either way, so the assertion has to be on
/// <b>fake time traversed</b>, which is the only thing that separates the two.</para>
///
/// <para>The target here models the LX200 shape (a pulse-end instant, latest wins, one flag for
/// both axes) because that is the contract's reference implementation and it is deterministic under
/// a fake clock: no background task, no race, just "is now past the end".</para>
/// </remarks>
[Collection("Guider")]
public class PulseGuideCompositeTests
{
    private static readonly TimeSpan RaDuration = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan DecDuration = TimeSpan.FromMilliseconds(600);

    private sealed class PulseEndTarget(ITimeProvider timeProvider, bool simultaneous) : IPulseGuideTarget
    {
        private DateTimeOffset _pulseEnd;

        public int Starts { get; private set; }

        public bool CanPulseGuideSimultaneously => simultaneous;

        public ValueTask StartPulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
        {
            Starts++;
            var end = timeProvider.GetUtcNow() + duration;
            if (end > _pulseEnd)
            {
                _pulseEnd = end;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(timeProvider.GetUtcNow() < _pulseEnd);
    }

    private static (FakeTimeProviderWrapper Clock, PulseEndTarget Target) Rig(bool simultaneous)
    {
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero));
        return (clock, new PulseEndTarget(clock, simultaneous));
    }

    private static async Task<TimeSpan> MeasureAsync(
        FakeTimeProviderWrapper clock, Func<Task> action)
    {
        var before = clock.GetUtcNow();
        await action();
        return clock.GetUtcNow() - before;
    }

    [Fact]
    public async Task TheCompositeWaitsForThePulseWhereTheStarterDoesNot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (clock, target) = Rig(simultaneous: true);

        var elapsed = await MeasureAsync(clock, () =>
            target.PulseGuideAsync(GuideDirection.West, RaDuration, clock, cancellationToken).AsTask());

        // The starter returns instantly on this target, so any time at all is the composite's wait.
        elapsed.ShouldBeGreaterThanOrEqualTo(RaDuration);
    }

    [Fact]
    public async Task BothAxesOverlapWhenTheTargetAllowsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (clock, target) = Rig(simultaneous: true);

        var elapsed = await MeasureAsync(clock, () => target.PulseGuideAsync(
            new GuidePulse(GuideDirection.West, RaDuration),
            new GuidePulse(GuideDirection.North, DecDuration),
            clock, cancellationToken).AsTask());

        target.Starts.ShouldBe(2);
        // max(1000, 600), not the sum: the pair costs what the LONGER axis costs.
        elapsed.ShouldBeGreaterThanOrEqualTo(RaDuration);
        elapsed.ShouldBeLessThan(RaDuration + DecDuration);
    }

    [Fact]
    public async Task BothAxesSerialiseWhenTheTargetDoesNotAllowIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (clock, target) = Rig(simultaneous: false);

        var elapsed = await MeasureAsync(clock, () => target.PulseGuideAsync(
            new GuidePulse(GuideDirection.West, RaDuration),
            new GuidePulse(GuideDirection.North, DecDuration),
            clock, cancellationToken).AsTask());

        target.Starts.ShouldBe(2);
        // Sum, because the second pulse is not started until the first has finished. This is the
        // fallback existing to serve mounts that require it, NOT a regression.
        elapsed.ShouldBeGreaterThanOrEqualTo(RaDuration + DecDuration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneAxisAloneNeverConsultsTheCapability(bool simultaneous)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (clock, target) = Rig(simultaneous);

        var elapsed = await MeasureAsync(clock, () => target.PulseGuideAsync(
            null, new GuidePulse(GuideDirection.South, DecDuration), clock, cancellationToken).AsTask());

        target.Starts.ShouldBe(1);
        elapsed.ShouldBeGreaterThanOrEqualTo(DecDuration);
    }

    [Fact]
    public async Task NeitherAxisIsANoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (clock, target) = Rig(simultaneous: true);

        var elapsed = await MeasureAsync(clock, () =>
            target.PulseGuideAsync(null, null, clock, cancellationToken).AsTask());

        target.Starts.ShouldBe(0);
        elapsed.ShouldBe(TimeSpan.Zero);
    }

    /// <summary>Gated target: each axis blocks until the test releases it, and RA then faults.</summary>
    private sealed class GatedTarget : IPulseGuideTarget
    {
        public readonly TaskCompletionSource RaGate = new();
        public readonly TaskCompletionSource DecGate = new();

        public bool DecCompleted { get; private set; }

        public bool CanPulseGuideSimultaneously => true;

        public async ValueTask StartPulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
        {
            if (direction is GuideDirection.East or GuideDirection.West)
            {
                await RaGate.Task;
                throw new InvalidOperationException("the rate restore would not take");
            }

            await DecGate.Task;
            DecCompleted = true;
        }

        public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(false);
    }

    [Fact]
    public async Task AFaultedRaPulseStillAwaitsTheDecPulse()
    {
        var target = new GatedTarget();
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero));

        var call = target.PulseGuideAsync(
            new GuidePulse(GuideDirection.West, RaDuration),
            new GuidePulse(GuideDirection.North, DecDuration),
            clock, TestContext.Current.CancellationToken).AsTask();

        // Fault the RA axis and give the exception every chance to escape the composite.
        target.RaGate.SetResult();
        for (var i = 0; i < 200 && !call.IsCompleted; i++)
        {
            await Task.Yield();
        }

        // The discriminator. Two bare awaits would have propagated the RA fault by now, abandoning
        // the Dec pulse: an unobserved ValueTask, and an axis still running with nobody watching
        // whether its stop ever landed.
        call.IsCompleted.ShouldBeFalse("the composite must still be awaiting the Dec pulse");

        target.DecGate.SetResult();
        await Should.ThrowAsync<InvalidOperationException>(() => call);
        target.DecCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task AnAxisMixUpIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (clock, target) = Rig(simultaneous: true);

        // A Dec direction in the RA slot is a caller bug, not a diagonal correction. Cheap to catch
        // here and near-impossible to spot in a guide log, where it reads as an inverted axis.
        await Should.ThrowAsync<ArgumentException>(async () => await target.PulseGuideAsync(
            new GuidePulse(GuideDirection.North, RaDuration), null, clock, cancellationToken));

        await Should.ThrowAsync<ArgumentException>(async () => await target.PulseGuideAsync(
            null, new GuidePulse(GuideDirection.East, DecDuration), clock, cancellationToken));

        target.Starts.ShouldBe(0);
    }
}
