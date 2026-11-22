using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The hold of a host-timed guide pulse (#2330): the pulse's timer is armed before the caller is told the pulse is
/// commanded. Told first, the caller could wait on the same clock before the driver's thread reached its sleep, and on a
/// clock a test advances the pulse was then held from wherever the clock had got to: a 750 ms Dec pulse ran for several
/// times that, and a calibration lost its star (<c>GuiderCalibrationOnFakeGuideCameraTests</c>, once on CI).
/// </summary>
public class PulseHoldTests
{
    private static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(750);

    /// <summary>A clock whose sleep notes whether the caller had been told already, and is over at once (or refuses).</summary>
    private sealed class ArmingClock(TaskCompletionSource commanded, bool refuses = false) : ITimeProvider
    {
        private readonly FakeTimeProviderWrapper _inner = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 11, 22, 3, 0, 0, TimeSpan.Zero));

        public List<(TimeSpan Duration, bool CallerToldAlready)> Armed { get; } = [];

        public DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public long GetTimestamp() => _inner.GetTimestamp();
        public long TimestampFrequency => _inner.TimestampFrequency;
        public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => _inner.CreateTimer(callback, state, dueTime, period);
        public TimeProvider System => _inner.System;

        public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Armed.Add((duration, commanded.Task.IsCompleted));
            return refuses ? throw new OperationCanceledException(cancellationToken) : ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ThePulsesTimerIsArmedBeforeTheCallerIsToldThePulseIsCommanded()
    {
        var commanded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ArmingClock(commanded);

        await PulseHold.RunAsync(clock, Pulse, commanded, TestContext.Current.CancellationToken);

        clock.Armed.ShouldBe([(Pulse, false)], "one sleep of the pulse's length, armed while the caller still waited to be told");
        commanded.Task.IsCompletedSuccessfully.ShouldBeTrue("and then the caller is told");
    }

    [Fact]
    public async Task AHoldWhoseTimerIsRefusedStillTellsTheCaller()
    {
        // A cancelled token refuses the sleep. The board has been commanded all the same, so the caller is released (it
        // would otherwise wait for ever on a pulse whose driver has already gone to stop the axis), and the refusal comes
        // out of the hold for the driver's own handling.
        var commanded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ArmingClock(commanded, refuses: true);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await PulseHold.RunAsync(clock, Pulse, commanded, cancelled.Token));

        commanded.Task.IsCompletedSuccessfully.ShouldBeTrue();
        clock.Armed.ShouldBe([(Pulse, false)]);
    }
}
